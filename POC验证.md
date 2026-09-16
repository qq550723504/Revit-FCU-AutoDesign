# FCU 最小 PoC 验证

本轮目标：指定一个 FCU 类型，在一个房间中放置设备，并验证供回水接口到各自主管的完整模型连接链。

## 2026-09-16 直达路线、首段安装净长与墙体检查

### 后续回归修复：第二房间墙内转弯未尝试设备侧先横移

用户现场结果：首房间连接完成，第二房间部分完成；冷凝水预筛 22、跳过 10、试建 12，12 次均因墙 `373443` 判定首段/转弯位于墙内或墙面而回滚。诊断文件 `route-20260916-024627-ef1434c6fad24a46b3ab890c968738b7.txt` 显示障碍前转弯长度为 258/193.5/129 mm；400 mm 正常路线被已有供水下翻管预筛挡住，原算法没有“设备侧先横移、再水平穿墙”的候选。本次现场结果为 FAIL。

修复：路径规划新增设备侧先横移段，优先尝试“先横移避开供水下翻 → 保留直段穿墙 → 过墙后上翻/下翻接主管”，并保留原横移后下翻候选。横移和转弯仍受短段、墙体实体和供回水/冷凝水实体检查约束；候选失败继续完整回滚。新增几何回归覆盖先横移路径。

PASS：Release/Revit2020 Rebuild Exit 0；`Verify-LowerFlipRoute.ps1` 输出 `48 checks passed; 3132 active route candidates validated`；`Verify-OutletLead.ps1` 输出 `28 outlet obstacle checks passed`；`Verify-Dialog.ps1 -AssemblyPath .\bin\Release\FCUAutoDesign.dll` 输出 `42 checks passed`；`Verify-MainPipeSegments.ps1` 输出 `14 segment checks passed`。真实 Revit 连接及墙体场景为 NOT_RUN，不能以本地几何检查替代客户模型复测。

### 后续回归修复：冷凝水结果传递丢失首段管 ID

用户 0a6cd0e 截图：首房间 1.98 秒后失败并回滚，后两房间未执行，Revit 报 `Document.GetElement` 参数 `id` 为空。对应 `route-20260916-022227-750bb3e347b14cda999b0edc11e49499.txt` 记录冷凝水第 3 次试建 252 mm 首段、150 mm 下翻在 489 ms 通过候选检查。候选通过不代表主事务/事务组验收成功；本次整体为 FAIL。

代码根因：`CondensateSeparationService.Wrap` 逐字段复制连接记录，漏掉新增的 `FirstPipeId` 和 `MinimumStraightLength`。提交后 `ConnectionInstallationVerifier` 读取空首段 ID 导致回滚。改为结果构造时直接保留完整的已验证连接记录；缺失首段 ID 仍明确失败，不能跳过校验。

PASS：Revit 2020/.NET 4.8 Release Rebuild Exit 0。运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Verify-ConnectionResult.ps1`，真实输出 `11 connection result checks passed. Revit geometry/commit/rollback: NOT_RUN.` 测试编译生产结果模型，使用惰性的 Revit ID/XYZ 替身验证记录传递、净长、主管/过渡件及失败状态；不是 Revit 运行时测试。测试编译器使用 Visual Studio Roslyn，与项目 C# 语法兼容。

NOT_RUN：修复后的实际主事务提交、提交后复核、多房间连续接管及模型回滚；需干净模型副本复测。

### 后续回归修复：最小净长留空不能隐含锁死目标距离

现场截图及 `route-20260916-021334-75086c487707466cac13305d489a12f4.txt` 显示：整房间 1.18 秒，供回水已连接，冷凝水预筛 264、跳过 264、试建 0。全部首段撞同一供水下翻管；`earlyLeads(mm)` 为空。根因是 aae8618 在最小净长留空时禁用了提前转弯，错误地把 400 mm 目标距离当作硬下限。该版本此用例为 FAIL。

修复后无论是否填写最小净长都计算障碍前转弯建议；显式填写时筛掉不满足下限的建议，且保持安装后与提交后的实际净长验证。留空不伪造规范值，允许缩短并明确未校验工程安装净长。此规则替代下面历史记录中的“未指定时禁止缩短”。实体碰撞、墙体、连接链、回滚及试建预算不变。

PASS：Release/Revit2020 Rebuild Exit 0；`Verify-OutletLead.ps1` 输出 `28 outlet obstacle checks passed`（新增未知下限保留建议、显式下限过滤、过大下限拒绝、非法下限拒绝）；`Verify-LowerFlipRoute.ps1` 输出 `47 checks passed; 3132 active route candidates validated`；`Verify-Dialog.ps1 -AssemblyPath .\bin\Release\FCUAutoDesign.dll` 输出 `42 checks passed`。命令均使用 Windows PowerShell `-NoProfile -ExecutionPolicy Bypass -File`，窗口测试加 `-STA`。

NOT_RUN：本修复在真实 Revit 内的冷凝水连接、墙体碰撞及多房间验收。不能用单元测试证明现场已接通。

三路优先不下翻；新增可空的最小净直管参数，接入候选校验与提交后复核。未指定时禁止自动缩短首段。使用 Revit 原生实体交线及干涉过滤器检查宿主直墙穿越和墙内管件，无新增第三方依赖。

PASS 命令和真实输出：

```text
MSBuild FCUAutoDesign.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:RevitVersion=2020 /nologo /verbosity:minimal
FCUAutoDesign -> bin\Release\FCUAutoDesign.dll (Exit 0)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Verify-LowerFlipRoute.ps1
47 checks passed; 3132 active route candidates validated.
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests/Verify-Dialog.ps1 -AssemblyPath .\bin\Release\FCUAutoDesign.dll
42 checks passed. Revit geometry/connection tests are NOT_RUN by this script.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Verify-OutletLead.ps1
24 outlet obstacle checks passed. Reproduced 261 blocked old candidates.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Verify-MainPipeSegments.ps1
14 segment checks passed. Revit batch integration tests are NOT_RUN.
```

覆盖同标高省弯、主管高低差、折返/短段、管件占长后的净长下限及 WPF 参数读取。几何测试的墙后升降案例只验证折线次序，不证明真实墙体穿越。

NOT_RUN：真实 Revit 宿主墙正交穿越、墙内弯头拒绝、弯头占长、管壁擦墙、墙面转折、候选回滚、多房间性能及提交后复核。需使用干净模型副本。未确认工程安装尺寸；链接模型、保温/套管净距及排水性能未覆盖。本地检查无失败；未执行的模型项不得记为通过，FCU-206 整体仍未验收。

不包含设备容量选型、水力计算、障碍物检测、净距检查、跨链接模型拾取和完整冷凝水排放。面积分档只用于选择 DN20 / DN25。

## 2026-09-13 连接方向失败跟进

最新朝向要求（用户实测先纠正 90°，再反转 180°）：FCU 出风口平面与门所在直墙平行，送风接口法向垂直于墙，取门向房间内缩方向的反向（相对上一版旋转 180°）。已将门内缩落点与设备旋转分开计算；使用唯一的 HVAC / End / SupplyAir 接口法向确定真实出风轴，不再假定族局部 +X 为出风方向。根据门向房间内缩方向的反向选择墙法向，旋转后及主事务提交后均复核送风轴与目标法向同向，拒绝反向送风。无门、多门、非直墙、送风接口缺失或不唯一、非水平出风均明确失败并回滚。本次仅调整设备朝向，不生成送风风管。

本次 Revit 2020 编译 PASS；加载目录更新为 `bin/OutletPlaneReversed`（覆盖下文历史目录）。Revit 内朝向与接管联合验证 NOT_RUN。重启 Revit 后，在单门直墙房间中验证沿 X、沿 Y 及斜向墙：送风接口法向均应垂直于墙且与门内缩方向反向，设备定位点仍在房间内；不支持的情况不应留下新增设备或管道。

最新一次用户堆栈在回水调用阶段触发接入点范围检查，尚未进入该回水支管的管件生成与方向检查。已将接入点错误细化为供回水、主管 ElementId、系统名称、管段长度及投影超出起点/终点的距离；系统分类检查提前，避免选错系统先收到几何错误。此轮编译通过，本机注册已更新到 `bin/EndpointDiagnostic/FCUAutoDesign.dll`，替代下文的旧诊断路径；真实模型复验仍待执行。此改动没有新增自动寻找接入点的能力。

用户实测报告主事务 RolledBack：`DuctPipeModified`，FailureId `1643aec9-a57a-4eec-a30e-2cae191b350d`，失败元素 `1780625`。尚无该元素的管段角色和几何快照，不能确定它是供水、回水、新建支管还是原主管，也不能断言是下翻距离不足。

已将弯头连接从“每次重新选最近端头”改为创建管道时记录端头 ID，后续重新读取指定接口。每个弯头、设备接口和三通连接后检查支管沿原方向的剩余长度；遇到过短或反向即拒绝该连接。错误诊断同时记录供回水及各支管段角色，保留主事务回滚保护。

Revit 2020 / Debug / x64 编译通过。当前本机注册指向 `bin/ConnectionDiagnostic/FCUAutoDesign.dll`；需重启 Revit 复验。此修改尚未通过真实模型复验，不代表上述失败原因已经确认或接管已经成功。下表为此前检查记录，旧 DLL 哈希不代表本次产物。

## 当前证据（2026-09-12）

| 检查 | 状态 | 说明 |
| --- | --- | --- |
| Revit 2020 / .NET Framework 4.8 重新编译 | PASS | Visual Studio MSBuild，Debug / x64 |
| WPF 确认、取消、关闭、类型选择和参数校验 | PASS | `tests/Verify-Dialog.ps1`，23 项断言 |
| 用户建筑模型打开 | PASS | Revit 窗口显示建筑模型及“立面: 出图-东”；不代表接管验证通过 |
| 插件注册与真实命令调用 | PASS | 当前会话“附加模块 → 外部工具”显示 FCU 命令，已真实调用 |
| 当前建筑模型前置条件 | FAIL | 命令提示“请先载入指定的 FCU 机械设备族”，在开始事务前退出，无模型修改 |
| Revit 内供回水完整连接链 | NOT_RUN | 必须执行下表中的真实模型用例 |
| 三通失败后的主管恢复 | NOT_RUN | 必须在 Revit 中验证失败路径 |
| 提交失败/提交后复核失败的整次回滚 | NOT_RUN | 编译和 WPF 检查不覆盖 Revit 事务行为 |

编译产物：`bin/Debug/FCUAutoDesign.dll`。本次检查后的 SHA256：`B09668A71D59270390A48A63F0DDEDC504F8B45F44BB77727306674239EEEF05`。

## 模型条件

使用模型副本，固定 Revit 2020。用户提供的建筑、机电、结构文件和 FCU 族在 `C:\Users\Henry\Documents\revits`；测试副本已复制到 `C:\Users\Henry\AppData\Local\Temp\FCUAutoDesign-PoC-20260912-232821`。

当前打开的建筑模型路径尚未通过应用确认是否为副本，不应直接在该会话进行写入测试。原始文件未由本轮代码或自动化保存。

目标宿主文档需要同时具备：

- 一个有面积、边界和有效标高的 Room。房间上限必须覆盖安装高度。
- 一个有明确供水/回水系统分类、水平管道端接口的非宿主 FCU 族类型。在参数窗口明确选择类型；多类型时不自动选第一个。
- 两条不同的水平直线主管，分别属于供水和回水系统，具有有效参考标高。
- 管道类型已配置适用管径的弯头和三通。支管路径上的各段长度必须足够容纳管件。
- 测试族需要唯一的水平送风（SupplyAir）风管接口；测试房间需要唯一关联门，且门宿主为直墙。按真实接口法向复核垂直于墙且与门内缩方向反向，无需族局部 +X 约定。

建筑和机电分文件本身不满足上述条件。当前拾取使用 `ObjectType.Element`，不支持直接选择链接建筑中的 Room，也不把机电 Space 当成 Room。先在独立测试宿主中准备一个本地 Room 和本地供回水管，再验证接管；跨文档支持另行设计，不能用随意坐标转换或重复房间数据替代。

## 操作与验收

运行“附加模块 → 外部工具 → FCU 放置与接管 PoC”，选定族类型。首轮保留供回水和三通，关闭冷凝水。

| 用例 | 操作 | 必须观察到的结果 |
| --- | --- | --- |
| 标准双管连接 | 选择规则房间和两条主管 | FCU 实际定位点在房间内且安装标高正确；供回水每个接口、管段、弯头、三通与两段主管逐段连通；提交后复核通过 |
| 非首层 | 在非零标高房间执行 | 绝对安装高度等于房间标高加输入高度，不能只看设备参考标高文字 |
| 候选点不在室内 | 单门房间内缩落点在室外，或输入高度超过房间上限 | 失败并回滚，没有新增 FCU 或管线；不能报告落点验证通过 |
| 缺少弯头规则 | 在专用测试管型中移除适用弯头配置后运行 | 抛出错误并回滚整次操作，不留下断开的支管并报成功 |
| 三通失败 | 保留有效弯头，但测试管型缺少适用三通规则 | 三通局部操作回滚；原主管端点、长度、管径、系统及已有连接不变；保留的支管只报告部分完成，不计双管 PoC 通过 |
| 三通禁用 | 关闭主管打断选项 | 支管连接到 FCU，末端开放，主管未打断；明确报告局部完成 |
| 选错系统 | 把供水主管选为回水目标 | 拒绝执行并回滚，不能通过接口顺序猜测或自动纠正系统 |
| 退化路径 | 将主管高度设为下翻后水平段高度，或投影超出主管端点 | 明确拒绝零长度/越界路径，不创建无效管段 |
| 取消 | 参数窗口取消/关闭，或房间拾取时 Esc | 无模型修改；回水拾取 Esc 保留原“跳过回水”的行为，不能计双管通过 |

每个真实模型用例应记录 DLL SHA256、模型副本、Room/FCU/主管 ElementId、输入参数、结果报告和连接复核证据。未执行写 NOT_RUN，失败写 FAIL，不能以没有弹出异常当成 PASS。

## 实现边界

- 三通连通校验沿本次创建的固定元素链进行，用 `IsConnectedTo` 验证相邻节点，且检查每个内部管段/弯头的两个端接口及三通的三个端接口。
- 主事务提交后按 ElementId 重新读取位置和连接关系；复核失败则回滚外层事务组。
- 供回水接口只按唯一的系统分类识别；不按高度、描述或枚举顺序猜测。
- 保留 Revit 警告；提交出现错误时回滚，不再统一删除警告。
- 冷凝水默认关闭；启用后拾取 Sanitary 或 OtherPipe 主管并接入，单独验证支管坡度和设备到主管的完整连接链。该功能不等于整体排水系统验收。

## 冷凝水主管接入回归

本地编译通过；`tests/Verify-CondensateRoute.ps1` 的 27 项纯几何检查、`tests/Verify-Dialog.ps1` 的 24 项窗口检查通过。下面的 Revit 模型内场景为 NOT_RUN，需在隔离模型副本中执行：

- 勾选供回水及冷凝水后必须出现第四步；第四步 ESC 取消时不创建任何设备或支管。关闭回水时为第三步。
- 系统名称包含“凝”或“排”但分类不是 Sanitary/OtherPipe 的管道点击后明确显示实际分类并要求重选。
- 选择使用非首项管型的主管，在非首层房间执行；生成支管沿用主管管型、系统类型和房间标高。
- 正常水平/有坡度直线主管：非竖直支管保持设定下降坡度，必要时竖直落管；检查设备、弯头、全部支管、三通和两段主管逐段连通。
- 主管太高、投影越界、接近端点、落差过短或需原路折返：报告具体原因，不留下冷凝水残管或打断主管。
- 测试模型中缺少适用弯头/三通或管件会使支管过短：整个冷凝水子事务回滚，核对原主管端点、长度、类型、系统与已有连接均恢复。
- 主事务提交后复核失败：事务组回滚，不能把提交前布尔值当作成功证据。

纯几何测试覆盖顺坡、竖直落管、同向管段合并、刚好满足坡度、斜主管插值、多楼层平移旋转、逆坡、越界、端点、立管、折返、过短落差与无效坡度。不能替代真实 Revit 管件适配和回滚测试。

## 供回水避让回归（模型内 NOT_RUN）

- 设备供回水接口上下排列且原路线相交：检查回水采用错开的预留长度/下翻高度，报告尺寸与模型一致，两路管段、弯头和三通不相交。
- 原路线不相交且管件可用：采用原输入尺寸，不无故偏移。
- 回水前几个候选因相交或管件失败而回滚，后续候选成功：模型不残留失败候选的管段、管件或主管断口；冷凝水继续使用重新读取的设备接口。
- 无可行候选：整次事务组回滚，包括本次供水和设备，原主管保持不变。
- 主事务提交后才发生实体相交：提交后复核拒绝整次操作。

本轮已通过 Revit 2020 编译；未把冷凝水几何测试或窗口测试当作供回水实体避让测试。

OtherPipe 分类修复：编译及 `tests/Verify-CondensateSystem.ps1` 的 33 项实际 Revit 枚举映射检查通过。模型内的 OtherPipe 主管与唯一同分类 FCU 接口接管、分类不匹配及多接口歧义场景均为 NOT_RUN。

## 当前冷凝水无坡度设置变更

本节替代此前关于固定坡度、坡度输入和竖直落管的验收要求。已移除坡度输入、参数传递及固定坡度校验。路径按设备接口和主管的实际标高连线，同标高允许水平连接，主管更高时也允许向上接入。

最新验证：23 项窗口检查及 12 项实际标高几何检查通过。真实模型内管件适配、连接和回滚仍为 NOT_RUN，不能据此声称重力排水性能合格。

最新变更取消冷凝水高低方向校验，包含主管更高的折线路径及直接上升路径回归。编译和 12 项几何检查通过；Revit 实际连接/回滚仍为 NOT_RUN。

Global 管道端接口候选修复：编译与 229 项分类映射/候选组合检查通过；真实 Global 接口接管、风管接口排除、多候选拒绝的模型内场景为 NOT_RUN。

当前冷凝水分类规则按用户要求统一为卫生设备（Sanitary），替代前述 OtherPipe/Global 候选规则。编译及 229 项分类组合检查通过；实际 Revit 连接仍为 NOT_RUN。

冷凝水连接链修复：纳入 ConnectTo 自动创建的唯一两端过渡管件，增加具体断点诊断；编译通过。设备直接连管、自动过渡管件、真实断点和回滚等 Revit 模型内回归为 NOT_RUN，不能仅凭编译判定截图中的问题已解决。

三通自动过渡管件回归：运行截图显示支管 1791029 与三通 1791039 共同邻接 1791041，旧验证遗漏中间节点。现已记录三通三个端口的新建过渡管件，编译通过；直接连接、支管自动过渡、主管自动过渡、真实断链及回滚的 Revit 模型内复测为 NOT_RUN。

## 冷凝水与供回水避让回归

编译及 18 项纯几何检查通过；以下模型内场景为 NOT_RUN：
- 冷凝水与供水/回水支管或过渡管件相交时重试，最终管线不相交，报告参数与模型一致。
- 原路线无干涉时不无故偏移。
- 多次失败后成功，原主管未留下失败候选断口、残管或管件。
- 所有冷凝水避让候选失败时不保留本次冷凝水操作，供回水状态仍如实报告。
- 提交后相交或连通性复核失败时整组回滚。

纯几何检查验证中间点偏移及端点不变，不代替 Revit 实体干涉与回滚验证。

## 多房间批量验收（模型内 NOT_RUN）

新增 14 项主管分段几何测试，验证前后段定位、多次打断、枚举/端点反序、三通间隙、越界、端点邻近、斜主管与无效输入。此测试不代替 Revit 事务与批量集成。

- 多选两个及以上同层单门房间，共用各回路主管：逐间接入成功，并生成汇总。
- 后一房间位于原主管打断后的第二段：正确接入该段，前一房间的连接仍完整。
- 前一房间失败、后一房间成功：失败房间无设备、残管或主管断口，后一房间不引用回滚元素 ID。
- 中间房间因多门或无可行路径失败：前后成功房间保留，报告失败原因。
- 后一房间路径与前一房间相交：候选重试或明确失败，不保留相交路线；共用主管不误报为自相交。
- 多房间共用三通附近位置：端点/已有管件处不得强行重复打断。
- 选择跨楼层房间或不符楼层主管：执行前拒绝，不创建模型元素。
- 重复选同一房间仅执行一次；只选一个房间也可完成。
- 单间部分回路失败如实显示“部分完成”；未执行房间不得计为成功。
- 后续打断若破坏已成功房间连接，新房间事务组必须回滚。

冷凝水现改用与供回水相同的固定路径：水平出管、下翻、横向接近主管、竖直接入并插入三通。先尝试预留长度和下翻高度各增加 0～8 个步长的 81 条路线；仍冲突时，再尝试设备侧左右横移 1～2 个步长并将下翻高度增加 0～4 个步长，共最多 261 条候选。每条候选仍要求完整连接及实体干涉检查通过，失败完整回滚。真实 Revit 管件生成及本例碰撞消除仍需模型验证。

## 261 条候选弯头失败后的路径修正

用户提供的 `543595c` 测试截图：5 间均部分完成，供回水已接主管，冷凝水均失败，最后提示 `failed to insert elbow`。该版本冷凝水模型验收为 FAIL；截图不能证明失败发生在第几个弯头，也不能证明回滚后的全部模型状态。

代码及几何复现确认：原来的“下翻 → 横移 → 接近主管”在出管方向平行主管时产生同向共线或反向折返，仍尝试插入弯头；主管段定位也遗漏了横移量。本次改为“水平预留 → 横移 → 下翻 → 接近主管 → 竖直接入”，定位和投影共用横移后的接近点，创建前拒绝过短和非直角路线。候选上限仍为 261；实体干涉、连接链及候选回滚保护继续执行。首条和最后失败原因均保留，弯头错误带段位和管型。

当前运行路径由 `HydronicConnectionService` 调用 `LowerFlipRoutePlanner`。旧 `Verify-CondensateRoute.ps1` 只测试未接入的旧算法，不能作为本次修正的验证证据。

本次命令与真实输出：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' FCUAutoDesign.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:RevitVersion=2020 /nologo /verbosity:minimal
# Exit 0; FCUAutoDesign -> C:\Users\Henry\code\FCUAutoDesign\bin\Release\FCUAutoDesign.dll
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-LowerFlipRoute.ps1
# 32 checks passed; 3132 active route candidates validated. Revit fitting, collision and rollback acceptance: NOT_RUN.
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-MainPipeSegments.ps1
# 14 segment checks passed. Revit batch integration tests are NOT_RUN.
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-Dialog.ps1 -AssemblyPath .\bin\Release\FCUAutoDesign.dll
# 23 checks passed. Revit geometry/connection tests are NOT_RUN by this script.
```

新测试复现旧共线/折返缺陷，覆盖供回水原路径、正负横移、旋转/平移/主管反序、平行和垂直主管、横移跨主管分段、三通间隙、越界及无效输入。3132 是 12 种几何场景各 261 条候选，不代表在 Revit 创建了 3132 条管线。

修正后的实际族/管型弯头生成、5 房间冷凝水接入、实体避让和回滚复核：NOT_RUN。需在干净模型副本中复测，先验证一个房间，再验证五房间批量；不能在此前部分完成的结果上直接重复创建。

## 冷凝水首段被供水下翻管阻挡后的修正

用户提供的 `62f652f` 四房间截图均为部分完成，冷凝水未接入。报告中的首条冲突为供水第 2 段下翻管与冷凝水第一个弯头，最后冲突为同一供水下翻管与冷凝水第 1 段水平预留管。该版本此案例的冷凝水验收为 FAIL；本次修正前仅检查转角，不足以证明能避开障碍。

原候选首段从 400 mm 开始只增加长度，而转弯发生在首段之后。现按已生成供回水管道/管件包围盒建议最多三个更短首段，让冷凝水在障碍前转弯；每组横移优先试这些长度，之后仍保留原组合。该候选建议仅接入冷凝水；供回水阀门预留参数不变。窗口和成功报告明确告知冷凝水首段可以缩短，失败报告显示已加入几个提前转弯长度。最终 Revit 管件、连接链、实体干涉及回滚检查仍执行。

新增自动化场景使用合成几何复现“261 条原候选首段均撞供水下翻管”，独立的线段/盒相交检查确认三条提前转弯路线绕过测试障碍。还覆盖首弯头附近障碍、远离首段的障碍、最近障碍选择、旋转/平移、无正长度空间和无效输入。合成坐标不代表客户模型实际尺寸。

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' FCUAutoDesign.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:RevitVersion=2020 /nologo /verbosity:minimal
# Exit 0; FCUAutoDesign -> C:\Users\Henry\code\FCUAutoDesign\bin\Release\FCUAutoDesign.dll
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-OutletLead.ps1
# 20 outlet obstacle checks passed. Reproduced 261 blocked old candidates. Revit fitting/collision/rollback acceptance: NOT_RUN.
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-LowerFlipRoute.ps1
# 32 checks passed; 3132 active route candidates validated. Revit fitting, collision and rollback acceptance: NOT_RUN.
powershell -NoProfile -STA -ExecutionPolicy Bypass -File tests\Verify-Dialog.ps1 -AssemblyPath .\bin\Release\FCUAutoDesign.dll
# 23 checks passed. Revit geometry/connection tests are NOT_RUN by this script.
```

上述测试及编译为 PASS；修正后的真实模型首弯头安装、冷凝水连通、实体避让及失败回滚验收为 NOT_RUN。包围盒可能保守，提前转弯长度不构成管件安装空间或工程检修净距保证。

## 单房间性能诊断轮

最新用户报告仍是四房间部分完成、每间 261 次冷凝水失败、新增提前转弯长度 0。真实模型接管验收为 FAIL，耗时未有实测秒数，不能给出加速倍数。

发现可复现的候选生成缺陷：覆盖出管起点的包围盒得到非正可用长度，原逻辑以全局最小值清空其他障碍提供的候选。现仅把正长度建议参与排序；并不据此忽略起点障碍的真实碰撞。增加回归证明这个缺陷已被覆盖，但客户模型的触发障碍仍需日志确认。

诊断版在创建前用不可变几何快照筛查首段、横移和下翻段与本次供回水直管的中心线距离。筛查不包含完整管件几何及后半段，不声称通过即为可行；最终 Revit 检查继续执行。最多 12 次冷凝水试建，10 秒以后不启动下一次，预算未覆盖单次正在执行的 Revit 调用及先前供回水过程。启用冷凝水时仅执行首个房间，其余 NOT_RUN；局部失败仍报告部分完成。

诊断输出位于 `%LOCALAPPDATA%\FCUAutoDesign\Diagnostics\`，含接口、管段、包围盒、候选及各阶段时间戳。需用户重启 Revit 在干净模型副本中运行首房间后，读取日志建立真实几何回归。本轮未直接操作用户 Revit 模型。

验证命令：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' FCUAutoDesign.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:RevitVersion=2020 /nologo /verbosity:minimal
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-SegmentClearance.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-OutletLead.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-LowerFlipRoute.ps1
powershell -NoProfile -STA -ExecutionPolicy Bypass -File tests\Verify-Dialog.ps1 -AssemblyPath .\bin\Release\FCUAutoDesign.dll
```

真实输出：Rebuild Exit 0；16 clearance checks passed；21 outlet obstacle checks passed；32 checks passed / 3132 active route candidates validated；23 checks passed。实际单房间运行耗时、日志模型数据、试建上限与回滚的 Revit 验证仍为 NOT_RUN。

## 首房间诊断实测结果（运行程序集 5ec1e75）

读取本地日志 `route-20260915-150703-2a20ce4cce20412d8ed6d0fdd23047aa.txt` 和 `route-20260915-150738-493ca8b8a62141cc9ee08df5b01705a1.txt`。两次运行 MVID 均为 `c1eaeb1f-80e2-4451-9e37-ba017f82e617`，FCU ID 分别为 575792、576003。两次均生成 252/189/126 mm 提前转弯候选，首条 252 mm、下翻 150 mm、横移 0 即通过，预筛 1、跳过 0、试建 1。

后一份日志：5 ms 开始试建，122 ms 接管返回，123 ms 验证通过；前一份到验证通过为 200 ms。这仅计冷凝水阶段，不能作为整房间或批量耗时。用户后一张结果截图显示会议室 1（房间 ID 561856）供水、回水、冷凝水均已接主管，最终为连接完成；第二间按单房间诊断规则未执行。

现场几何确认原候选被清空的原因：供水水平段包围盒在冷凝水出管方向的范围为 0～362 mm，垂直相对高差 63.25～96.75 mm，落入原候选扩展范围而产生负可用长度。供水首弯头前缘为 362 mm，扣除半径 10 mm 和搜索余量 100 mm 得到 252 mm；修正后这个正长度建议得以保留。

将上述局部几何移至冷凝水接口原点并转换出管方向后加入 `tests/Verify-OutletLead.ps1`，不提交客户绝对坐标或完整模型日志。命令 `powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-OutletLead.ps1` 真实输出 `24 outlet obstacle checks passed`；新增三项复现实际候选长度、相邻供水水平段和提前转弯折线。

- PASS（用户截图及本地日志）：该首房间连接完成、一次冷凝水试建、阶段计时输出、后续房间未执行。
- PASS（自动化）：实际局部几何回归。
- NOT_RUN：恢复批量后的多房间连接与总耗时、失败预算触发与回滚复核、工程排水工况及人工净距验收。

本次仅记录证据并增加回归，运行程序集仍为 5ec1e75；未解除单房间诊断限制。

## 恢复受限批量验证

在首房间实测通过后解除“只运行首房间”限制。开启冷凝水时，连接成功继续下一间；冷凝水未完成或房间异常则停止后续房间并标为未执行。每房间仍使用前段预筛、最多 12 次冷凝水试建、10 秒后不启动新试建的预算及唯一诊断日志。预算不是整房间硬超时。

结果窗口新增每个已执行房间的计时（异常路径也记录）和整批计时，含设备放置、供回水、冷凝水及提交复核；不含选择/预览/结果窗口等待。未执行房间不显示虚假的执行耗时。

本轮验证命令及输出：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' FCUAutoDesign.csproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:RevitVersion=2020 /nologo /verbosity:minimal
# Exit 0; FCUAutoDesign -> bin\Release\FCUAutoDesign.dll
powershell -NoProfile -STA -ExecutionPolicy Bypass -File tests\Verify-Dialog.ps1 -AssemblyPath .\bin\Release\FCUAutoDesign.dll
# 23 checks passed. Revit geometry/connection tests are NOT_RUN by this script.
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-MainPipeSegments.ps1
# 14 segment checks passed. Revit batch integration tests are NOT_RUN.
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-OutletLead.ps1
# 24 outlet obstacle checks passed. Reproduced 261 blocked old candidates. Revit fitting/collision/rollback acceptance: NOT_RUN.
```

PASS：编译及上述 61 项本地检查。NOT_RUN：恢复后的多房间实际接管、暂停后续房间行为、整体耗时、已有主管连接复核和模型回滚。不能以首房间的 123/200 ms 冷凝水阶段结果代替批量性能证据。复测应从干净模型副本开始，避免在已完成房间上重复创建。
