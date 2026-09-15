using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;

namespace FCUAutoDesign
{
    internal class FcuBatchService
    {
        public static void ValidateRooms(IList<Room> rooms)
        {
            if (rooms == null || rooms.Count == 0) throw new InvalidOperationException("请至少选择一个房间。");
            if (rooms.Any(r => r == null || r.Level == null))
                throw new InvalidOperationException("所选房间必须具有有效标高。");
            if (rooms.Select(r => r.Level.Id.IntegerValue).Distinct().Count() != 1)
                throw new InvalidOperationException("本版批量布置仅支持同一楼层的房间，请按楼层分别运行。");
        }

        public List<RoomExecutionResult> Execute(Document doc, IList<Room> rooms, Pipe supply,
            Pipe ret, Pipe drain, FcuDesignOptions options)
        {
            ValidateRooms(rooms);
            ElementId level = rooms[0].Level.Id;
            foreach (Pipe main in new[] { supply, ret, drain }.Where(p => p != null))
                if (main.ReferenceLevel == null || main.ReferenceLevel.Id != level)
                    throw new InvalidOperationException("批量布置要求所选主管参考标高与房间楼层一致。");
            RoomBatchContext batch = new RoomBatchContext(supply, ret, drain);
            var results = rooms.Select(r => new RoomExecutionResult
            {
                RoomLabel = $"{r.Number} {r.Name}（房间 ID {r.Id.IntegerValue}）"
            }).ToList();
            ElementId[] roomIds = rooms.Select(r => r.Id).ToArray();
            string stopped = null;
            for (int i = 0; i < roomIds.Length; i++)
            {
                if (stopped != null)
                {
                    results[i].NotRun = true; results[i].Error = stopped; continue;
                }
                Stopwatch roomClock = Stopwatch.StartNew();
                try
                {
                    // 每个房间在 FcuDesignService 内独立持有事务组，失败不登记管段。
                    FcuDesignResult design = new FcuDesignService().Execute(doc,
                        doc.GetElement(roomIds[i]) as Room, batch.Supply.AnySegment(doc),
                        batch.Return?.AnySegment(doc), batch.Condensate?.AnySegment(doc), options, batch);
                    batch.Register(design);
                    results[i].Design = design;
                    if (options.EnableCondensate && !design.Outcome.CondensateConnected)
                        stopped = "前一房间冷凝水未完成，受限批量已停止后续房间；请检查该房间报告及诊断文件。";
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException ex)
                {
                    results[i].Error = ex.Message;
                    stopped = "模型再生成失败，已停止后续房间。";
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    results[i].Error = "用户取消当前房间。";
                    stopped = "用户取消，未继续执行后续房间。";
                }
                catch (Exception ex)
                {
                    results[i].Error = ex.Message;
                    if (options.EnableCondensate)
                        stopped = "前一房间执行失败，受限批量已停止后续房间。";
                }
                finally { roomClock.Stop(); results[i].Elapsed = roomClock.Elapsed; }
            }
            return results;
        }
    }
}
