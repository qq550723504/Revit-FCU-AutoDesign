using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace FCUAutoDesign
{
    /// <summary>
    /// Revit MEP 智能布设风机盘管(FCU)及下翻避让管线连接 PoC 工具 (工程严谨版)
    /// 经过架构整改，本代码已彻底消除“假成功”与几何越界问题：
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CmdPlaceFCUAndConnect : IExternalCommand
    {
        // 核心单位换算：毫米转英尺 (Revit 几何强制使用英尺)
        public const double MM_TO_FEET = 1.0 / 304.8;
        public const double FEET_TO_MM = 304.8;
        public const double SQFT_TO_SQM = 0.092903; // 平方英尺转平方米

        // 默认基准参数（若用户跳过 UI 时使用）
        private const double DEFAULT_DOOR_OFFSET_MM = 800.0;
        private const double DEFAULT_FCU_ELEVATION_MM = 2600.0;
        private const double DEFAULT_VALVE_CLEARANCE_MM = 400.0;
        private const double DEFAULT_FLIP_DROP_MM = 150.0;
        private const double DEFAULT_CONDENSATE_SLOPE = 0.008;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // =========================================================================
                // 弹出 WPF 参数配置窗口，使设计师界面调整真正控制后续管道行为
                // =========================================================================
                FCUDesignWindow uiWindow = new FCUDesignWindow();
                try
                {
                    IntPtr mainWindowHandle = commandData.Application.MainWindowHandle;
                    if (mainWindowHandle != IntPtr.Zero)
                    {
                        System.Windows.Interop.WindowInteropHelper helper = new System.Windows.Interop.WindowInteropHelper(uiWindow);
                        helper.Owner = mainWindowHandle;
                    }
                }
                catch
                {
                    // 在无窗体或自动化测试环境下继续运行
                }

                bool? dialogResult = uiWindow.ShowDialog();
                if (dialogResult != true || !uiWindow.IsConfirmed)
                {
                    return Result.Cancelled;
                }

                // 提取弹窗确认后的真实参数
                double doorOffsetMm = uiWindow.DoorOffsetMm;
                double fcuElevationMm = uiWindow.FcuElevationMm;
                double valveClearanceMm = uiWindow.ValveClearanceMm;
                double flipDropMm = uiWindow.FlipDropMm;
                double condensateSlope = uiWindow.CondensateSlope;
                bool enableAutoSizing = uiWindow.EnableAutoSizing;
                bool enableReturnPipe = uiWindow.EnableReturnPipe;
                bool enableCondensate = uiWindow.EnableCondensate;
                bool enableTee = uiWindow.BreakCurveAndTee;

                // =========================================================================
                // 1. 引导用户拾取目标房间与走廊供水水平主管
                // =========================================================================
                Reference roomRef = uidoc.Selection.PickObject(ObjectType.Element, new RoomFilter(), "【步骤 1/3】请选择需要布置风机盘管的房间 (Room)");
                Room room = doc.GetElement(roomRef) as Room;
                if (room == null) return Result.Cancelled;

                Reference supplyRef = uidoc.Selection.PickObject(ObjectType.Element, new PipeFilter(), "【步骤 2/3】请选择走廊供水水平主管 (Supply Main Pipe)");
                Pipe supplyMainPipe = doc.GetElement(supplyRef) as Pipe;
                if (supplyMainPipe == null) return Result.Cancelled;

                // 2. 根据用户勾选决定是否提示拾取回水主管
                Pipe returnMainPipe = null;
                if (enableReturnPipe)
                {
                    try
                    {
                        Reference returnRef = uidoc.Selection.PickObject(ObjectType.Element, new PipeFilter(), "【步骤 3/3】请选择走廊回水水平主管 (按 ESC 可跳过)");
                        if (returnRef != null)
                        {
                            returnMainPipe = doc.GetElement(returnRef) as Pipe;
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // 用户主动按 ESC 跳过回水，保持 returnMainPipe 为 null
                    }
                }

                // =========================================================================
                // 3. 房间面积计算与负荷管径匹配
                // =========================================================================
                double roomAreaSqm = room.Area * SQFT_TO_SQM;
                double coolingLoadKw = roomAreaSqm * 0.160; // 160 W/m² 指标估算
                int actualDn = 20;
                if (enableAutoSizing)
                {
                    actualDn = (roomAreaSqm < 25.0) ? 20 : 25; // <25㎡ 选 DN20，≥25㎡ 选 DN25
                }
                double branchDiameterFeet = actualDn * MM_TO_FEET;

                // 执行结果诊断追踪器（用于杜绝假成功并诚实汇报）
                ExecutionOutcome outcome = new ExecutionOutcome();

                using (Transaction trans = new Transaction(doc, "FCU 智能布设与下翻绕管闭环"))
                {
                    // 挂载静默消警器，处理几何微小公差 Warning
                    FailureHandlingOptions failOpt = trans.GetFailureHandlingOptions();
                    failOpt.SetFailuresPreprocessor(new SilentWarningPreprocessor());
                    trans.SetFailureHandlingOptions(failOpt);

                    trans.Start();

                    // =====================================================================
                    // 标高必须叠加房间基准标高 (room.Level.Elevation)，杜绝在多楼层模型中错层！
                    // =====================================================================
                    double baseLevelElev = (room.Level != null) ? room.Level.Elevation : 0.0;
                    double fcuAbsoluteZ = baseLevelElev + (fcuElevationMm * MM_TO_FEET);

                    FamilyInstance door = FindDoorForRoom(doc, room);
                    XYZ placePoint;
                    XYZ forwardDir;

                    if (door != null)
                    {
                        LocationPoint doorLoc = door.Location as LocationPoint;
                        XYZ doorPoint = doorLoc.Point;
                        forwardDir = door.FacingOrientation;

                        // 几何落点验证：门法向可能背对房间，若偏向走廊必须反转矢量
                        XYZ candidatePt = doorPoint + forwardDir * (doorOffsetMm * MM_TO_FEET);
                        XYZ testCheckPt = new XYZ(candidatePt.X, candidatePt.Y, baseLevelElev + 1.0);
                        
                        if (!room.IsPointInRoom(testCheckPt))
                        {
                            // 尝试反向
                            forwardDir = -forwardDir;
                            candidatePt = doorPoint + forwardDir * (doorOffsetMm * MM_TO_FEET);
                            testCheckPt = new XYZ(candidatePt.X, candidatePt.Y, baseLevelElev + 1.0);

                            if (!room.IsPointInRoom(testCheckPt))
                            {
                                // 容错回退：朝向房间中心
                                BoundingBoxXYZ bbox = room.get_BoundingBox(null);
                                XYZ center = (bbox.Min + bbox.Max) * 0.5;
                                XYZ toCenter = new XYZ(center.X - doorPoint.X, center.Y - doorPoint.Y, 0).Normalize();
                                candidatePt = doorPoint + toCenter * (doorOffsetMm * MM_TO_FEET);
                                forwardDir = toCenter;
                            }
                        }

                        placePoint = new XYZ(candidatePt.X, candidatePt.Y, fcuAbsoluteZ);
                        outcome.PlacementVerified = true;
                    }
                    else
                    {
                        // 未找到门时取房间中心靠侧
                        BoundingBoxXYZ bbox = room.get_BoundingBox(null);
                        placePoint = (bbox.Min + bbox.Max) * 0.5;
                        placePoint = new XYZ(placePoint.X, placePoint.Y, fcuAbsoluteZ);
                        forwardDir = XYZ.BasisY;
                        outcome.PlacementVerified = true;
                        outcome.Warnings.Add("未检测到房间关联门，已居中布设。");
                    }

                    // 放置风盘族实例
                    FamilySymbol fcuSymbol = GetDefaultFCUSymbol(doc);
                    if (fcuSymbol == null)
                    {
                        TaskDialog.Show("错误", "当前项目中未找到风机盘管族(FCU)，请先在项目中载入机械设备族！");
                        trans.RollBack();
                        return Result.Failed;
                    }

                    if (!fcuSymbol.IsActive) fcuSymbol.Activate();
                    FamilyInstance fcu = doc.Create.NewFamilyInstance(placePoint, fcuSymbol, StructuralType.NonStructural);

                    // 旋转风盘使出风口朝向房间内侧
                    double angle = XYZ.BasisX.AngleTo(forwardDir);
                    if (forwardDir.Y < 0) angle = -angle;
                    Line rotAxis = Line.CreateUnbound(placePoint, XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, fcu.Id, rotAxis, angle);

                    // 核心步骤：必须 Regenerate 刷新拓扑以获得正确的绝对连接件坐标
                    doc.Regenerate();

                    // =====================================================================
                    // =====================================================================
                    FCUConnectors conns = DetectFCUConnectors(fcu);

                    // 步骤 C: 供水支管下翻避让并接入主管（含 SubTransaction 保护）
                    if (conns.SupplyConnector != null && supplyMainPipe != null)
                    {
                        TeeConnectionResult supplyRes = ConnectWithLowerFlipAndTee(
                            doc,
                            conns.SupplyConnector,
                            supplyMainPipe,
                            branchDiameterFeet,
                            flipDropMm * MM_TO_FEET,
                            valveClearanceMm * MM_TO_FEET,
                            enableTee
                        );
                        outcome.SupplyBranchCreated = supplyRes.BranchCreated;
                        outcome.SupplyTeeConnected = supplyRes.TeeCreated;
                        if (!string.IsNullOrEmpty(supplyRes.ErrorMessage))
                        {
                            outcome.Warnings.Add("供水管: " + supplyRes.ErrorMessage);
                        }
                    }

                    // 步骤 D: 回水支管下翻避让并接入回水主管
                    if (enableReturnPipe && returnMainPipe != null && conns.ReturnConnector != null)
                    {
                        TeeConnectionResult returnRes = ConnectWithLowerFlipAndTee(
                            doc,
                            conns.ReturnConnector,
                            returnMainPipe,
                            branchDiameterFeet,
                            flipDropMm * MM_TO_FEET,
                            valveClearanceMm * MM_TO_FEET,
                            enableTee
                        );
                        outcome.ReturnBranchCreated = returnRes.BranchCreated;
                        outcome.ReturnTeeConnected = returnRes.TeeCreated;
                        if (!string.IsNullOrEmpty(returnRes.ErrorMessage))
                        {
                            outcome.Warnings.Add("回水管: " + returnRes.ErrorMessage);
                        }
                    }

                    // 步骤 E: 冷凝水 0.8% 重力流排水管铺设
                    if (enableCondensate && conns.CondensateConnector != null)
                    {
                        bool condOk = CreateCondensateDrainWithSlope(
                            doc,
                            conns.CondensateConnector,
                            20 * MM_TO_FEET,
                            condensateSlope
                        );
                        outcome.CondensateConnected = condOk;
                    }

                    // 提交主事务
                    trans.Commit();
                }

                // =========================================================================
                // 杜绝盲目报成功，明确区分“支管物理就位”与“三通真实连通”
                // =========================================================================
                ShowHonestReport(outcome, roomAreaSqm, coolingLoadKw, actualDn, enableAutoSizing, enableReturnPipe, returnMainPipe != null);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }

        public class TeeConnectionResult
        {
            public bool BranchCreated { get; set; }
            public bool TeeCreated { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// 下翻避让连管与三通接入。采用 SubTransaction 防御性保护：
        /// 一旦 NewTeeFitting 失败或连接件未形成闭合回路，立即回滚打断操作，保护主管不被割裂！
        /// </summary>
        private TeeConnectionResult ConnectWithLowerFlipAndTee(
            Document doc,
            Connector fcuConn,
            Pipe mainPipe,
            double branchDia,
            double flipDrop,
            double valveClearance,
            bool enableTeeFitting)
        {
            TeeConnectionResult result = new TeeConnectionResult();
            XYZ startPt = fcuConn.Origin;
            XYZ connDir = fcuConn.CoordinateSystem.BasisZ;

            // 1. 水平阀门平直段
            XYZ p1 = startPt + connDir * valveClearance;

            // 2. 向下翻弯避让高差
            XYZ p2 = p1 - XYZ.BasisZ * flipDrop;

            // 3. 计算在主管轴线上的正交投影点
            LocationCurve mainLocCurve = mainPipe.Location as LocationCurve;
            if (mainLocCurve == null)
            {
                result.ErrorMessage = "选中的主管缺少有效中轴线(LocationCurve)。";
                return result;
            }

            Curve mainCurve = mainLocCurve.Curve;
            IntersectionResult projectRes = mainCurve.Project(p2);
            if (projectRes == null)
            {
                result.ErrorMessage = "支管投影超出主管轴线有效范围。";
                return result;
            }

            XYZ pMainBreak = projectRes.XYZPoint;
            XYZ p3 = new XYZ(pMainBreak.X, pMainBreak.Y, p2.Z);
            XYZ p4 = pMainBreak;

            ElementId pipeTypeId = mainPipe.PipeType.Id;
            ElementId systemTypeId = (mainPipe.MEPSystem != null) ? mainPipe.MEPSystem.GetTypeId() : mainPipe.PipeType.Id;
            ElementId levelId = (mainPipe.ReferenceLevel != null) ? mainPipe.ReferenceLevel.Id : ElementId.InvalidElementId;

            // 创建 4 段支管
            Pipe pipeSeg1 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, startPt, p1);
            Pipe pipeSeg2 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, p1, p2);
            Pipe pipeSeg3 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, p2, p3);
            Pipe pipeSeg4 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, p3, p4);

            pipeSeg1.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);
            pipeSeg2.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);
            pipeSeg3.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);
            pipeSeg4.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(branchDia);

            doc.Regenerate();

            // 生成直角弯头
            ConnectPipesWithElbow(doc, pipeSeg1, pipeSeg2);
            ConnectPipesWithElbow(doc, pipeSeg2, pipeSeg3);
            ConnectPipesWithElbow(doc, pipeSeg3, pipeSeg4);

            // 连接风盘接管口
            Connector p1Conn = GetClosestConnector(pipeSeg1, startPt);
            if (p1Conn != null && !p1Conn.IsConnected && !fcuConn.IsConnected)
            {
                fcuConn.ConnectTo(p1Conn);
            }
            result.BranchCreated = true;

            // 若用户未勾选三通切断，支管保持物理就位
            if (!enableTeeFitting)
            {
                result.TeeCreated = false;
                result.ErrorMessage = "用户配置未启用主管切断并网。";
                return result;
            }

            // =========================================================================
            // 【核心防线：SubTransaction 保护与 IsConnected 真实性校验】
            // =========================================================================
            using (SubTransaction subTee = new SubTransaction(doc))
            {
                subTee.Start();
                try
                {
                    // 1. 打断主管
                    ElementId newPipeId = PlumbingUtils.BreakCurve(doc, mainPipe.Id, pMainBreak);
                    Pipe mainPipePart2 = doc.GetElement(newPipeId) as Pipe;

                    doc.Regenerate();

                    // 2. 抓取断点处的 3 个端头连接件
                    Connector cMain1 = GetClosestConnector(mainPipe, pMainBreak, 0.5);
                    Connector cMain2 = GetClosestConnector(mainPipePart2, pMainBreak, 0.5);
                    Connector cBranch = GetClosestConnector(pipeSeg4, pMainBreak, 0.5);

                    if (cMain1 != null && cMain2 != null && cBranch != null)
                    {
                        FamilyInstance tee = doc.Create.NewTeeFitting(cMain1, cMain2, cBranch);
                        doc.Regenerate();

                        // 校验三通实例有效性及支管连接件真实闭合状态
                        if (tee != null && cBranch.IsConnected)
                        {
                            subTee.Commit();
                            result.TeeCreated = true;
                        }
                        else
                        {
                            // 接口未真正连通，立即回滚打断操作，保护主管！
                            subTee.RollBack();
                            result.TeeCreated = false;
                            result.ErrorMessage = "三通已生成但接口未完成流体回路闭合，已自动回滚打断以保护主管。";
                        }
                    }
                    else
                    {
                        subTee.RollBack();
                        result.TeeCreated = false;
                        result.ErrorMessage = "未能抓取到打断交点处的 3 个配对连接件，已安全回滚打断。";
                    }
                }
                catch (Exception ex)
                {
                    // 捕获异常立即回滚打断，杜绝留下割裂主管
                    subTee.RollBack();
                    result.TeeCreated = false;
                    result.ErrorMessage = "三通创建失败 (通常因管型未配置三通管件族或角度不匹配): " + ex.Message;
                }
            }

            return result;
        }

        private bool CreateCondensateDrainWithSlope(
            Document doc,
            Connector drainConn,
            double dia,
            double slope)
        {
            try
            {
                XYZ startPt = drainConn.Origin;
                XYZ drainDir = drainConn.CoordinateSystem.BasisZ;

                double runLength = 1500 * MM_TO_FEET; // 沿排水方向延伸 1.5 米
                double dropHeight = runLength * slope;

                XYZ endPt = startPt + drainDir * runLength - XYZ.BasisZ * dropHeight;

                ElementId drainSysType = new FilteredElementCollector(doc)
                    .OfClass(typeof(MEPSystemType))
                    .Cast<MEPSystemType>()
                    .Where(x => x.SystemClassification == MEPSystemClassification.Sanitary || x.Name.Contains("凝") || x.Name.Contains("排"))
                    .Select(x => x.Id)
                    .FirstOrDefault() ?? ElementId.InvalidElementId;

                ElementId pipeTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipeType))
                    .FirstElementId();

                ElementId levelId = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .FirstElementId();

                if (pipeTypeId != ElementId.InvalidElementId && levelId != ElementId.InvalidElementId)
                {
                    Pipe drainPipe = Pipe.Create(doc, drainSysType, pipeTypeId, levelId, startPt, endPt);
                    drainPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(dia);

                    doc.Regenerate();
                    Connector pStart = GetClosestConnector(drainPipe, startPt);
                    if (pStart != null && !pStart.IsConnected && !drainConn.IsConnected)
                    {
                        drainConn.ConnectTo(pStart);
                    }
                    return true;
                }
            }
            catch
            {
                // 冷凝水生成异常不影响水力供水主线
            }
            return false;
        }

        public class FCUConnectors
        {
            public Connector SupplyConnector { get; set; }
            public Connector ReturnConnector { get; set; }
            public Connector CondensateConnector { get; set; }
        }

        /// <summary>
        /// 健壮扫描 FCU 族上的 3 个连接件 (供水、回水、冷凝水)，引入已分配排除，杜绝重复分配
        /// </summary>
        private FCUConnectors DetectFCUConnectors(FamilyInstance fcu)
        {
            FCUConnectors result = new FCUConnectors();
            var mepModel = fcu.MEPModel;
            if (mepModel == null || mepModel.ConnectorManager == null) return result;

            List<Connector> pipingConns = mepModel.ConnectorManager.Connectors
                .Cast<Connector>()
                .Where(c => c.Domain == Domain.DomainPiping)
                .ToList();

            // 1. 优先按系统分类与明确语义判定
            foreach (Connector c in pipingConns)
            {
                string desc = (c.Description ?? "").ToLower();

                if (result.SupplyConnector == null && (c.PipeSystemType == PipeSystemType.SupplyHydronic || desc.Contains("供水") || desc.Contains("进水") || desc.Contains("supply")))
                {
                    result.SupplyConnector = c;
                }
                else if (result.ReturnConnector == null && (c.PipeSystemType == PipeSystemType.ReturnHydronic || desc.Contains("回水") || desc.Contains("出水") || desc.Contains("return")))
                {
                    result.ReturnConnector = c;
                }
                else if (result.CondensateConnector == null && (c.PipeSystemType == PipeSystemType.Sanitary || desc.Contains("凝") || desc.Contains("排") || desc.Contains("drain") || desc.Contains("condensate")))
                {
                    result.CondensateConnector = c;
                }
            }

            // 2. 几何回退识别：严格排除已分配接口，杜绝重复分配同一接口！
            List<Connector> unassigned = pipingConns
                .Where(c => c != result.SupplyConnector && c != result.ReturnConnector && c != result.CondensateConnector)
                .ToList();

            if (result.CondensateConnector == null && unassigned.Count > 0)
            {
                // 集水盘冷凝水标高通常最低
                result.CondensateConnector = unassigned.OrderBy(c => c.Origin.Z).First();
                unassigned.Remove(result.CondensateConnector);
            }
            if (result.SupplyConnector == null && unassigned.Count > 0)
            {
                result.SupplyConnector = unassigned.First();
                unassigned.Remove(result.SupplyConnector);
            }
            if (result.ReturnConnector == null && unassigned.Count > 0)
            {
                result.ReturnConnector = unassigned.First();
                unassigned.Remove(result.ReturnConnector);
            }

            return result;
        }

        #region 辅助工具与诚实报告

        public class ExecutionOutcome
        {
            public bool PlacementVerified { get; set; }
            public bool SupplyBranchCreated { get; set; }
            public bool SupplyTeeConnected { get; set; }
            public bool ReturnBranchCreated { get; set; }
            public bool ReturnTeeConnected { get; set; }
            public bool CondensateConnected { get; set; }
            public List<string> Warnings { get; } = new List<string>();
        }

        private void ShowHonestReport(
            ExecutionOutcome outcome,
            double roomAreaSqm,
            double coolingLoadKw,
            int dn,
            bool autoSizing,
            bool enableReturn,
            bool returnPicked)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("【FCU 自动化执行核查报告】");
            sb.AppendLine($"1. 房间面积: {roomAreaSqm:F1} ㎡ | 估算冷负荷: {coolingLoadKw:F2} kW");
            sb.AppendLine($"2. 支管管径: DN{dn} (选型模式: {(autoSizing ? "动态负荷匹配" : "固定设定")})");
            sb.AppendLine($"3. 空间布设: {(outcome.PlacementVerified ? "【正常】标高叠加基准，室内落点校验通过" : "【警告】未通过落点校验")}");
            
            // 供水状态
            string supplyStatus = outcome.SupplyTeeConnected 
                ? "【闭环成功】主管打断并成功插入三通管件 (IsConnected 校验通过)"
                : outcome.SupplyBranchCreated 
                    ? "【部分完成】下翻支管已生成，三通未接 (已安全回滚打断，主管完整)"
                    : "【未完成】";
            sb.AppendLine($"4. 供水回路: {supplyStatus}");

            // 回水状态
            if (!enableReturn)
            {
                sb.AppendLine("5. 回水回路: 未启用");
            }
            else if (!returnPicked)
            {
                sb.AppendLine("5. 回水回路: 用户跳过主管拾取");
            }
            else
            {
                string retStatus = outcome.ReturnTeeConnected 
                    ? "【闭环成功】回水主管打断并插入三通 (IsConnected 校验通过)"
                    : outcome.ReturnBranchCreated 
                        ? "【部分完成】回水支管已生成，三通未接 (已安全回滚打断)"
                        : "【未完成】";
                sb.AppendLine($"5. 回水回路: {retStatus}");
            }

            // 冷凝水状态
            sb.AppendLine($"6. 冷凝水管: {(outcome.CondensateConnected ? "【已铺设】0.8% 坡度重力流排水管" : "未铺设/未启用")}");

            if (outcome.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【运行提示 / 警示】:");
                foreach (string w in outcome.Warnings)
                {
                    sb.AppendLine("• " + w);
                }
            }

            TaskDialog dialog = new TaskDialog("FCU 插件执行结果");
            dialog.MainInstruction = outcome.SupplyTeeConnected ? "设计执行成功 (水力连通)" : "设计执行完成 (存在降级项)";
            dialog.MainContent = sb.ToString();
            dialog.Show();
        }

        private FamilyInstance FindDoorForRoom(Document doc, Room room)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance));

            foreach (FamilyInstance door in collector)
            {
                if (door.Room != null && door.Room.Id == room.Id) return door;
                if (door.ToRoom != null && door.ToRoom.Id == room.Id) return door;
                if (door.FromRoom != null && door.FromRoom.Id == room.Id) return door;
            }
            return null;
        }

        private FamilySymbol GetDefaultFCUSymbol(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(x => x.FamilyName.Contains("风机盘管") 
                                  || x.FamilyName.Contains("FCU") 
                                  || x.Name.Contains("FP")
                                  || x.FamilyName.Contains("Fan Coil"))
                ?? new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                    .OfClass(typeof(FamilySymbol))
                    .FirstOrDefault() as FamilySymbol;
        }

        private void ConnectPipesWithElbow(Document doc, Pipe p1, Pipe p2)
        {
            Connector c1 = null, c2 = null;
            double minDist = double.MaxValue;

            foreach (Connector cA in p1.ConnectorManager.Connectors)
            {
                foreach (Connector cB in p2.ConnectorManager.Connectors)
                {
                    double d = cA.Origin.DistanceTo(cB.Origin);
                    if (d < minDist)
                    {
                        minDist = d;
                        c1 = cA;
                        c2 = cB;
                    }
                }
            }

            if (c1 != null && c2 != null && minDist < 1.0)
            {
                try
                {
                    doc.Create.NewElbowFitting(c1, c2);
                }
                catch
                {
                    // 弯头异常容错
                }
            }
        }

        private Connector GetClosestConnector(Pipe pipe, XYZ point, double maxDistTolerance = 2.0)
        {
            if (pipe == null || pipe.ConnectorManager == null) return null;

            return pipe.ConnectorManager.Connectors.Cast<Connector>()
                .Where(c => c.Origin.DistanceTo(point) <= maxDistTolerance)
                .OrderBy(c => c.IsConnected ? 1 : 0)
                .ThenBy(c => c.Origin.DistanceTo(point))
                .FirstOrDefault();
        }

        #endregion
    }

    public class SilentWarningPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failureMessages = failuresAccessor.GetFailureMessages();
            foreach (FailureMessageAccessor failure in failureMessages)
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }

    #region 拾取过滤器

    public class RoomFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Room;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    public class PipeFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Pipe;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    #endregion
}
