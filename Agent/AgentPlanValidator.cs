using System;
using System.Collections.Generic;
using System.Linq;

namespace FCUAutoDesign.Agent
{
    public sealed class AgentPlanValidator
    {
        private const int MaxTextLength = 2000;
        private const int MaxListItems = 50;

        public AgentPlanValidationResult Validate(AgentPlan plan)
        {
            AgentPlanValidationResult result = new AgentPlanValidationResult();
            if (plan == null)
            {
                result.Errors.Add("AI 未返回方案对象。");
                return result;
            }
            if (plan.schema_version != 2) result.Errors.Add("不支持的 AI 方案版本。");
            AgentPlanMode mode;
            if (!Enum.TryParse(plan.mode ?? string.Empty, true, out mode))
                result.Errors.Add("AI 方案模式必须是 diagnose、create 或 recalculate。");
            else result.Mode = mode;
            ValidateText(plan.summary, "summary", true, result);
            ValidateList(plan.room_name_keywords, "room_name_keywords", 20, 40, result);
            ValidateList(plan.observations, "observations", MaxListItems, MaxTextLength, result);
            ValidateList(plan.warnings, "warnings", MaxListItems, MaxTextLength, result);
            ValidateList(plan.blocked_actions, "blocked_actions", MaxListItems, MaxTextLength, result);
            ValidateList(plan.clarifications, "clarifications", 10, 500, result);
            result.RequiresClarification = plan.clarifications != null
                && plan.clarifications.Count > 0;
            if (plan.cooling_index_w_per_square_meter.HasValue)
            {
                double value = plan.cooling_index_w_per_square_meter.Value;
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                    result.Errors.Add("冷指标建议必须是有限正数；零负荷仍需产品确认。");
            }
            ValidateRange(plan.door_offset_mm, "距门侧墙距离", 1, 10000, result);
            ValidateRange(plan.fcu_elevation_mm, "FCU安装高度", 1, 20000, result);
            if (plan.enable_automatic_scope_discovery == true
                && (plan.room_name_keywords == null || plan.room_name_keywords.Count == 0))
                result.Errors.Add("启用自动选房时必须提供房间名称关键词。");
            if (mode == AgentPlanMode.Diagnose && HasFormUpdates(plan))
                result.Errors.Add("仅诊断模式不得包含表单修改。");
            if (mode != AgentPlanMode.Diagnose && plan.connect_supply == false)
                result.Errors.Add("现有生成与重算流程必须连接供水主管。");
            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        private static bool HasFormUpdates(AgentPlan plan)
        {
            return (plan.room_name_keywords != null && plan.room_name_keywords.Count > 0)
                || plan.cooling_index_w_per_square_meter.HasValue
                || plan.door_offset_mm.HasValue
                || plan.fcu_elevation_mm.HasValue
                || plan.enable_multiple_fcus.HasValue
                || plan.enable_automatic_scope_discovery.HasValue
                || plan.connect_supply.HasValue
                || plan.connect_return.HasValue
                || plan.connect_condensate.HasValue;
        }

        private static void ValidateRange(double? value, string field, double minimum,
            double maximum, AgentPlanValidationResult result)
        {
            if (!value.HasValue) return;
            double number = value.Value;
            if (double.IsNaN(number) || double.IsInfinity(number)
                || number < minimum || number > maximum)
                result.Errors.Add(field + "超出允许范围 " + minimum + "-" + maximum + " mm。");
        }

        private static void ValidateText(string value, string field, bool required,
            AgentPlanValidationResult result)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required) result.Errors.Add(field + " 不能为空。");
                return;
            }
            if (value.Length > MaxTextLength) result.Errors.Add(field + " 超出长度限制。");
        }

        private static void ValidateList(IList<string> values, string field, int maxItems,
            int maxItemLength, AgentPlanValidationResult result)
        {
            if (values == null) return;
            if (values.Count > maxItems) result.Errors.Add(field + " 条目过多。");
            if (values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > maxItemLength))
                result.Errors.Add(field + " 包含空值或超长条目。");
            if (values.Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != values.Count(x => x != null))
                result.Errors.Add(field + " 包含重复条目。");
        }
    }
}
