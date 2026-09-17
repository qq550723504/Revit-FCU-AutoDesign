using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FCUAutoDesign.Agent
{
    public sealed class OpenAiCompatibleAgentClient : IDisposable
    {
        private const int MaxResponseCharacters = 128 * 1024;
        private readonly HttpClient http;
        private readonly AgentConfiguration configuration;
        private readonly AgentPromptBuilder prompts = new AgentPromptBuilder();
        private readonly AgentPlanValidator validator = new AgentPlanValidator();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        public OpenAiCompatibleAgentClient(AgentConfiguration configuration)
            : this(configuration, new HttpClientHandler()) { }

        public OpenAiCompatibleAgentClient(AgentConfiguration configuration, HttpMessageHandler handler)
        {
            this.configuration = configuration ?? new AgentConfiguration();
            http = new HttpClient(handler ?? throw new ArgumentNullException("handler"), true)
            {
                Timeout = TimeSpan.FromSeconds(90)
            };
        }

        public async Task<AgentCallResult> CreatePlanAsync(AgentRequestContext context,
            CancellationToken cancellationToken)
        {
            Uri endpoint;
            string configurationError;
            if (!TryBuildEndpoint(configuration, out endpoint, out configurationError))
                return Result(AgentCallStatus.Unavailable, configurationError);

            Dictionary<string, object> request = new Dictionary<string, object>
            {
                { "model", configuration.Model.Trim() },
                { "temperature", 0 },
                { "response_format", BuildResponseFormat() },
                { "messages", new List<ChatMessage>
                {
                    new ChatMessage { role = "system", content = prompts.SystemPrompt() },
                    new ChatMessage { role = "user", content = prompts.UserPrompt(context) }
                } }
            };
            if (endpoint.Host.EndsWith("aliyuncs.com", StringComparison.OrdinalIgnoreCase))
                request["enable_thinking"] = false;
            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                if (!string.IsNullOrWhiteSpace(configuration.ApiKey))
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey.Trim());
                message.Content = new StringContent(serializer.Serialize(request), Encoding.UTF8, "application/json");
                try
                {
                    using (HttpResponseMessage response = await http.SendAsync(
                        message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (body.Length > MaxResponseCharacters)
                            return Result(AgentCallStatus.InvalidResponse, "Agent 响应超过长度限制。");
                        if (!response.IsSuccessStatusCode)
                            return Result(AgentCallStatus.TransportFailure,
                                "Agent 接口返回 HTTP " + (int)response.StatusCode + "。未生成计划。");
                        return Parse(body);
                    }
                }
                catch (OperationCanceledException)
                {
                    return Result(AgentCallStatus.TransportFailure, "Agent 请求已取消或超时。");
                }
                catch (HttpRequestException ex)
                {
                    return Result(AgentCallStatus.TransportFailure, "Agent 接口不可用：" + ex.Message);
                }
            }
        }

        private AgentCallResult Parse(string body)
        {
            try
            {
                ChatCompletionResponse response = serializer.Deserialize<ChatCompletionResponse>(body);
                string content = response?.choices != null && response.choices.Count > 0
                    ? response.choices[0]?.message?.content : null;
                if (string.IsNullOrWhiteSpace(content))
                    return Result(AgentCallStatus.InvalidResponse, "Agent 响应没有计划内容。");
                AgentPlan plan = serializer.Deserialize<AgentPlan>(content.Trim());
                AgentPlanValidationResult validation = validator.Validate(plan);
                if (!validation.IsValid)
                    return new AgentCallResult
                    {
                        Status = AgentCallStatus.InvalidResponse,
                        Plan = plan,
                        Validation = validation,
                        Message = "Agent 计划未通过本地只读契约校验：" + string.Join("；", validation.Errors)
                    };
                return new AgentCallResult
                {
                    Status = AgentCallStatus.Success,
                    Plan = plan,
                    Validation = validation,
                    Message = "Agent 只读计划已生成；尚未应用到 Revit。"
                };
            }
            catch (InvalidOperationException)
            {
                return Result(AgentCallStatus.InvalidResponse, "Agent 返回内容不是有效的兼容 JSON 计划。");
            }
            catch (ArgumentException)
            {
                return Result(AgentCallStatus.InvalidResponse, "Agent 返回内容不是有效的兼容 JSON 计划。");
            }
        }

        internal static bool TryBuildEndpoint(AgentConfiguration value, out Uri endpoint, out string error)
        {
            endpoint = null;
            error = null;
            if (value == null || string.IsNullOrWhiteSpace(value.BaseUrl)
                || string.IsNullOrWhiteSpace(value.Model))
            {
                error = "Agent 尚未配置 Base URL 和模型；未发送网络请求。";
                return false;
            }
            Uri baseUri;
            if (!Uri.TryCreate(value.BaseUrl.Trim(), UriKind.Absolute, out baseUri))
            {
                error = "Agent Base URL 无效；未发送网络请求。";
                return false;
            }
            bool loopback = baseUri.IsLoopback;
            if (!loopback && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "远程 Agent 接口必须使用 HTTPS。";
                return false;
            }
            if (!loopback && string.IsNullOrWhiteSpace(value.ApiKey))
            {
                error = "远程 Agent 接口尚未配置 API Key；未发送网络请求。";
                return false;
            }
            string absolute = baseUri.AbsoluteUri.TrimEnd('/');
            endpoint = new Uri(absolute.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? absolute : absolute + "/chat/completions");
            return true;
        }

        private static AgentCallResult Result(AgentCallStatus status, string message)
        {
            return new AgentCallResult { Status = status, Message = message };
        }

        public void Dispose() { http.Dispose(); }

        private static object BuildResponseFormat()
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "schema_version", Field("integer", "const", 2) },
                { "summary", Field("string") },
                { "mode", Field("string", "enum", new[] { "diagnose", "create", "recalculate" }) },
                { "room_name_keywords", StringArrayField() },
                { "cooling_index_w_per_square_meter", NullableField("number") },
                { "door_offset_mm", NullableField("number") },
                { "fcu_elevation_mm", NullableField("number") },
                { "enable_multiple_fcus", NullableField("boolean") },
                { "enable_automatic_scope_discovery", NullableField("boolean") },
                { "connect_supply", NullableField("boolean") },
                { "connect_return", NullableField("boolean") },
                { "connect_condensate", NullableField("boolean") },
                { "clarifications", StringArrayField() },
                { "observations", StringArrayField() },
                { "warnings", StringArrayField() },
                { "blocked_actions", StringArrayField() }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "properties", properties },
                { "required", new List<string>(properties.Keys) }
            };
            return new
            {
                type = "json_schema",
                json_schema = new { name = "fcu_agent_plan_v2", strict = true, schema }
            };
        }

        private static Dictionary<string, object> Field(string type, string key = null, object value = null)
        {
            Dictionary<string, object> field = new Dictionary<string, object> { { "type", type } };
            if (key != null) field[key] = value;
            return field;
        }

        private static Dictionary<string, object> NullableField(string type)
        {
            return new Dictionary<string, object> { { "type", new[] { type, "null" } } };
        }

        private static Dictionary<string, object> StringArrayField()
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", new Dictionary<string, object> { { "type", "string" } } }
            };
        }

        private sealed class ChatMessage { public string role { get; set; } public string content { get; set; } }
        private sealed class ChatCompletionResponse { public IList<ChatChoice> choices { get; set; } }
        private sealed class ChatChoice { public ChatMessage message { get; set; } }
    }
}
