using System;
using System.Collections.Generic;
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
                if (i > 0 && options.EnableCondensate && stopped == null)
                    stopped = "当前冷凝水诊断版仅执行首个房间，其余未执行；先检查首房间报告和诊断文件。";
                if (stopped != null)
                {
                    results[i].NotRun = true; results[i].Error = stopped; continue;
                }
                try
                {
                    // 每个房间在 FcuDesignService 内独立持有事务组，失败不登记管段。
                    FcuDesignResult design = new FcuDesignService().Execute(doc,
                        doc.GetElement(roomIds[i]) as Room, batch.Supply.AnySegment(doc),
                        batch.Return?.AnySegment(doc), batch.Condensate?.AnySegment(doc), options, batch);
                    batch.Register(design);
                    results[i].Design = design;
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
                }
            }
            return results;
        }
    }
}
