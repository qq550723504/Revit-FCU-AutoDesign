using System;
using System.Collections.Generic;
using System.Linq;

namespace FCUAutoDesign.Business.DesignReconciliation
{
    public sealed class ReconciliationApplyDecision
    {
        public bool CanApplyRecordOnly { get; internal set; }
        public bool HasUpdates { get; internal set; }
        public string Reason { get; internal set; }
    }

    /// <summary>
    /// Defines the deliberately narrow first writable slice of FCU-205.
    /// It may advance the persisted calculation baseline, but it must never
    /// imply that a Revit element, family type, count, position or pipe changed.
    /// </summary>
    public sealed class ReconciliationApplyPolicy
    {
        private static readonly ISet<string> AllowedRoomFields = new HashSet<string>(
            new[] { "CoolingLoadDensity", "DesignCoolingLoad" }, StringComparer.Ordinal);
        private static readonly ISet<string> AllowedDeviceFields = new HashSet<string>(
            new[] { "DesignCoolingLoad" }, StringComparer.Ordinal);

        public ReconciliationApplyDecision Evaluate(ReconciliationPlan plan)
        {
            if (plan == null || plan.ErrorCode != DesignErrorCode.None || !plan.IsApplicable)
                return Block("差异计划无效或存在冲突。");

            bool hasUpdates = plan.Items.Any(x => x.Action == ReconciliationAction.Update);
            if (!hasUpdates)
                return new ReconciliationApplyDecision
                {
                    CanApplyRecordOnly = false,
                    HasUpdates = false,
                    Reason = "没有需要写入的计算字段。"
                };

            foreach (ReconciliationItem item in plan.Items)
            {
                if (item.Action != ReconciliationAction.Update
                    && item.Action != ReconciliationAction.NoChange)
                    return Block("包含新增、删除、冲突或未管理对象，必须保持只读预览。");

                ISet<string> allowed = item.EntityKind == ReconciliationEntityKind.Room
                    ? AllowedRoomFields
                    : item.EntityKind == ReconciliationEntityKind.Device
                        ? AllowedDeviceFields
                        : null;
                if (allowed == null || item.ChangedFields.Any(x => !allowed.Contains(x)))
                    return Block("包含设备几何、型号、数量、房间几何或管线变化，必须保持只读预览。");
            }

            return new ReconciliationApplyDecision
            {
                CanApplyRecordOnly = true,
                HasUpdates = true,
                Reason = "仅包含冷指标和设计负荷变化。"
            };
        }

        private static ReconciliationApplyDecision Block(string reason)
        {
            return new ReconciliationApplyDecision
            {
                CanApplyRecordOnly = false,
                HasUpdates = true,
                Reason = reason
            };
        }
    }
}
