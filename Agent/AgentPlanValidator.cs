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
                result.Errors.Add("Agent 未返回计划对象。");
                return result;
            }
            if (plan.schema_version != 1) result.Errors.Add("不支持的 Agent 计划版本。");
            AgentPlanMode mode;
            if (!Enum.TryParse(plan.mode ?? string.Empty, true, out mode))
                result.Errors.Add("Agent 计划模式必须是 diagnose、create 或 recalculate。");
            else result.Mode = mode;
            ValidateText(plan.summary, "summary", true, result);
            ValidateList(plan.room_name_keywords, "room_name_keywords", 20, 40, result);
            ValidateList(plan.observations, "observations", MaxListItems, MaxTextLength, result);
            ValidateList(plan.warnings, "warnings", MaxListItems, MaxTextLength, result);
            ValidateList(plan.blocked_actions, "blocked_actions", MaxListItems, MaxTextLength, result);
            if (plan.cooling_index_w_per_square_meter.HasValue)
            {
                double value = plan.cooling_index_w_per_square_meter.Value;
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                    result.Errors.Add("冷指标建议必须是有限正数；零负荷仍需产品确认。");
            }
            result.IsValid = result.Errors.Count == 0;
            return result;
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
