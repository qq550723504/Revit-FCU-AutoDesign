using System;
using System.Collections.Generic;

namespace FCUAutoDesign.Agent
{
    public enum AgentPlanMode
    {
        Diagnose,
        Create,
        Recalculate
    }

    public enum AgentCallStatus
    {
        Success,
        Unavailable,
        InvalidResponse,
        TransportFailure
    }

    public sealed class AgentConfiguration
    {
        public const string DefaultBaseUrl =
            "https://ws-fdp0ta8nlc7o4157.cn-beijing.maas.aliyuncs.com/compatible-mode/v1";
        public const string DefaultModel = "qwen3.8-max";

        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }

        public static AgentConfiguration FromEnvironment()
        {
            string configuredBaseUrl = Environment.GetEnvironmentVariable("FCU_AGENT_BASE_URL");
            string configuredModel = Environment.GetEnvironmentVariable("FCU_AGENT_MODEL");
            return new AgentConfiguration
            {
                BaseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
                    ? DefaultBaseUrl
                    : configuredBaseUrl,
                Model = string.IsNullOrWhiteSpace(configuredModel)
                    ? DefaultModel
                    : configuredModel,
                ApiKey = Environment.GetEnvironmentVariable("FCU_AGENT_API_KEY")
            };
        }
    }

    public sealed class AgentRoomContext
    {
        public string RoomUniqueId { get; set; }
        public string Number { get; set; }
        public string Name { get; set; }
        public bool HasManagedDesign { get; set; }
    }

    public sealed class AgentRequestContext
    {
        public string UserRequest { get; set; }
        public FcuOperationMode CurrentMode { get; set; }
        public IList<string> CurrentRoomNameKeywords { get; set; } = new List<string>();
        public double CoolingIndexWPerSquareMeter { get; set; }
        public IList<AgentRoomContext> Rooms { get; set; } = new List<AgentRoomContext>();
        public string DiagnosticText { get; set; }
    }

    public sealed class AgentPlan
    {
        public int schema_version { get; set; }
        public string summary { get; set; }
        public string mode { get; set; }
        public IList<string> room_name_keywords { get; set; } = new List<string>();
        public double? cooling_index_w_per_square_meter { get; set; }
        public IList<string> observations { get; set; } = new List<string>();
        public IList<string> warnings { get; set; } = new List<string>();
        public IList<string> blocked_actions { get; set; } = new List<string>();
    }

    public sealed class AgentPlanValidationResult
    {
        public bool IsValid { get; set; }
        public AgentPlanMode Mode { get; set; }
        public IList<string> Errors { get; } = new List<string>();
    }

    public sealed class AgentCallResult
    {
        public AgentCallStatus Status { get; set; }
        public AgentPlan Plan { get; set; }
        public string Message { get; set; }
        public AgentPlanValidationResult Validation { get; set; }
    }
}
