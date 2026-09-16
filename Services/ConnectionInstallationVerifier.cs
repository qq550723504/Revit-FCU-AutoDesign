using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using static FCUAutoDesign.RevitUnits;

namespace FCUAutoDesign
{
    // 只验证宿主模型墙体和首段净长；不创建洞口，不代替套管/保温/结构审核。
    internal static class ConnectionInstallationVerifier
    {
        public static void Verify(Document doc, TeeConnectionResult result)
        {
            if (result == null || !result.BranchCreated) return;
            Pipe first = doc.GetElement(result.FirstPipeId) as Pipe;
            Line firstLine = (first?.Location as LocationCurve)?.Curve as Line;
            if (firstLine == null) throw new InvalidOperationException("FCU 首段直管已丢失。");
            if (!InstallationClearance.MeetsMinimum(firstLine.Length, result.MinimumStraightLength))
                throw new InvalidOperationException($"FCU 首段净直管仅 {firstLine.Length * FEET_TO_MM:F1} mm，"
                    + $"小于指定最小值 {result.MinimumStraightLength.Value * FEET_TO_MM:F1} mm；不能自动缩短安装空间。");

            var ids = result.Chain.Skip(1).ToList();
            if (result.MainPart1AdapterId != null) ids.Add(result.MainPart1AdapterId);
            if (result.MainPart2AdapterId != null) ids.Add(result.MainPart2AdapterId);
            foreach (ElementId id in ids.Distinct())
            {
                Element element = doc.GetElement(id);
                if (element == null) throw new InvalidOperationException("接管元素已丢失：" + id.IntegerValue);
                Pipe pipe = element as Pipe;
                if (pipe != null)
                {
                    Line line = (pipe.Location as LocationCurve)?.Curve as Line;
                    if (line == null) throw new InvalidOperationException("仅支持直管穿墙。");
                    VerifySegment(doc, line);
                    // 中心线不进墙而管壁擦墙，也不能当作合法穿越。
                    using (var pipeFilter = new ElementIntersectsElementFilter(pipe))
                    using (var walls = new FilteredElementCollector(doc))
                    foreach (Wall wall in walls.OfClass(typeof(Wall)).WherePasses(pipeFilter))
                    {
                        bool crosses = false;
                        using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
                        foreach (Solid solid in Solids(wall.get_Geometry(options)))
                        using (var intersectionOptions = new SolidCurveIntersectionOptions())
                        using (var intersection = solid.IntersectWithCurve(line, intersectionOptions))
                            crosses |= intersection.SegmentCount > 0;
                        if (!crosses) throw new InvalidOperationException(
                            $"管道 {id.IntegerValue} 擦碰墙 {wall.Id.IntegerValue}，不构成可验证的直管穿越。");
                    }
                }
                else
                {
                    // 管件实体也必须在墙外，不能仅依据折线转折点。
                    using (var filter = new ElementIntersectsElementFilter(element))
                    using (var collector = new FilteredElementCollector(doc))
                    {
                        Element wall = collector.OfClass(typeof(Wall)).WherePasses(filter).FirstElement();
                        if (wall != null) throw new InvalidOperationException(
                            $"管件 {id.IntegerValue} 与墙 {wall.Id.IntegerValue} 实体相交，需将转弯移至墙外。");
                    }
                }
            }
        }

        public static void VerifyPlanned(Document doc, IList<XYZ> points)
        {
            for (int i = 1; i < points.Count; i++)
                VerifySegment(doc, Line.CreateBound(points[i - 1], points[i]));
        }

        private static void VerifySegment(Document doc, Line path)
        {
            XYZ a = path.GetEndPoint(0), b = path.GetEndPoint(1);
            double tol = MM_TO_FEET;
            using (var outline = new Outline(
                new XYZ(Math.Min(a.X,b.X)-tol, Math.Min(a.Y,b.Y)-tol, Math.Min(a.Z,b.Z)-tol),
                new XYZ(Math.Max(a.X,b.X)+tol, Math.Max(a.Y,b.Y)+tol, Math.Max(a.Z,b.Z)+tol)))
            using (var filter = new BoundingBoxIntersectsFilter(outline))
            using (var collector = new FilteredElementCollector(doc))
            {
                foreach (Wall wall in collector.OfClass(typeof(Wall)).WherePasses(filter))
                {
                    using (var options = new Options { DetailLevel = ViewDetailLevel.Fine })
                    {
                        var solids = Solids(wall.get_Geometry(options)).ToList();
                        if (solids.Count == 0) throw new InvalidOperationException(
                            $"墙 {wall.Id.IntegerValue} 无可验证实体，不能确认穿墙路线。");
                        foreach (Solid solid in solids)
                        using (var intersectOptions = new SolidCurveIntersectionOptions())
                        using (var intersection = solid.IntersectWithCurve(path, intersectOptions))
                        {
                            for (int i = 0; i < intersection.SegmentCount; i++)
                            {
                                Curve inside = intersection.GetCurveSegment(i);
                                if (inside.Length < 1e-7) continue;
                                Line wallAxis = (wall.Location as LocationCurve)?.Curve as Line;
                                if (wallAxis == null || Math.Abs(wallAxis.Direction.Z) > 1e-6
                                    || Math.Abs(path.Direction.Z) > 1e-6
                                    || Math.Abs(path.Direction.DotProduct(wallAxis.Direction)) > 1e-6)
                                    throw new InvalidOperationException($"墙 {wall.Id.IntegerValue} 仅允许水平正交直管穿越；曲墙或斜向穿墙需人工处理。");
                                if (inside.GetEndPoint(0).DistanceTo(a) <= tol
                                    || inside.GetEndPoint(1).DistanceTo(b) <= tol)
                                    throw new InvalidOperationException($"管段端点/转弯位于墙 {wall.Id.IntegerValue} 内或墙面，需移至墙外。");
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<Solid> Solids(GeometryElement geometry)
        {
            if (geometry == null) yield break;
            foreach (GeometryObject item in geometry)
            {
                Solid solid = item as Solid;
                if (solid != null && solid.Volume > 1e-9) yield return solid;
                GeometryInstance instance = item as GeometryInstance;
                if (instance != null)
                    foreach (Solid nested in Solids(instance.GetInstanceGeometry())) yield return nested;
            }
        }
    }
}
