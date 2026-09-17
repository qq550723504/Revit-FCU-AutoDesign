param([switch]$Live)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
$files = @(
    'Agent\AgentContracts.cs',
    'Agent\AgentPlanValidator.cs',
    'Agent\AgentPromptBuilder.cs',
    'Agent\OpenAiCompatibleAgentClient.cs'
)
$sources = foreach ($file in $files) {
    $value = Get-Content (Join-Path $root $file) -Raw -Encoding UTF8
    [regex]::Replace($value, '(?m)^using .*;\r?$', '')
}
$checks = @'
namespace FCUAutoDesign { public enum FcuOperationMode { Create, Recalculate } }

namespace FCUAutoDesign.Agent
{
    public sealed class FakeHandler : HttpMessageHandler
    {
        public bool Called;
        public string RequestUri;
        public string Authorization;
        public string RequestBody;
        public string ResponseBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Called = true;
            RequestUri = request.RequestUri.AbsoluteUri;
            Authorization = request.Headers.Authorization == null ? null
                : request.Headers.Authorization.Scheme + " " + request.Headers.Authorization.Parameter;
            RequestBody = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    public static class AgentCoreChecks
    {
        private static int count;
        private static void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            count++; Console.WriteLine("PASS: " + name);
        }

        private static string Response(string mode, double? coolingIndex)
        {
            var serializer = new JavaScriptSerializer();
            var plan = new AgentPlan
            {
                schema_version = 2,
                summary = "Read-only plan",
                mode = mode,
                room_name_keywords = new List<string> { "office" },
                cooling_index_w_per_square_meter = coolingIndex,
                door_offset_mm = 500,
                fcu_elevation_mm = 2500,
                enable_multiple_fcus = true,
                enable_automatic_scope_discovery = true,
                connect_supply = true,
                connect_return = true,
                observations = new List<string> { "diagnostic" },
                warnings = new List<string>(),
                blocked_actions = new List<string> { "direct model write" }
            };
            return serializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = serializer.Serialize(plan) } } }
            });
        }

        public static void Run()
        {
            string originalBaseUrl = Environment.GetEnvironmentVariable("FCU_AGENT_BASE_URL");
            string originalModel = Environment.GetEnvironmentVariable("FCU_AGENT_MODEL");
            string originalApiKey = Environment.GetEnvironmentVariable("FCU_AGENT_API_KEY");
            try
            {
                Environment.SetEnvironmentVariable("FCU_AGENT_BASE_URL", null);
                Environment.SetEnvironmentVariable("FCU_AGENT_MODEL", null);
                Environment.SetEnvironmentVariable("FCU_AGENT_API_KEY", null);
                AgentConfiguration defaults = AgentConfiguration.FromEnvironment();
                Check(defaults.BaseUrl == AgentConfiguration.DefaultBaseUrl,
                    "Verified Aliyun compatible endpoint is the default base URL");
                Check(defaults.Model == "qwen3.8-max"
                    && string.IsNullOrWhiteSpace(defaults.ApiKey),
                    "Latest Qwen flagship alias is default while API key stays empty");
            }
            finally
            {
                Environment.SetEnvironmentVariable("FCU_AGENT_BASE_URL", originalBaseUrl);
                Environment.SetEnvironmentVariable("FCU_AGENT_MODEL", originalModel);
                Environment.SetEnvironmentVariable("FCU_AGENT_API_KEY", originalApiKey);
            }

            var blankHandler = new FakeHandler { ResponseBody = Response("diagnose", null) };
            using (var blank = new OpenAiCompatibleAgentClient(new AgentConfiguration(), blankHandler))
            {
                var result = blank.CreatePlanAsync(new AgentRequestContext(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(result.Status == AgentCallStatus.Unavailable && !blankHandler.Called,
                    "Blank configuration never sends a network request");
            }

            var handler = new FakeHandler { ResponseBody = Response("recalculate", 210) };
            using (var client = new OpenAiCompatibleAgentClient(new AgentConfiguration
            {
                BaseUrl = "http://localhost:12345/v1",
                Model = "compatible-model"
            }, handler))
            {
                var result = client.CreatePlanAsync(new AgentRequestContext
                {
                    UserRequest = "explain failures",
                    CurrentMode = FcuOperationMode.Recalculate,
                    CoolingIndexWPerSquareMeter = 200
                }, CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Status == AgentCallStatus.Success && result.Validation.IsValid
                    && result.Validation.Mode == AgentPlanMode.Recalculate,
                    "Valid compatible response produces a read-only plan");
                Check(handler.RequestUri == "http://localhost:12345/v1/chat/completions",
                    "Base URL maps to the compatible chat completions endpoint");
                Check(handler.Authorization == null, "API key is optional for a local compatible endpoint");
                Check(handler.RequestBody.Contains("compatible-model")
                    && handler.RequestBody.Contains("explain failures")
                    && handler.RequestBody.Contains("json_schema")
                    && handler.RequestBody.Contains("additionalProperties"),
                    "Request contains model, runtime context and strict response schema");
            }

            var promptBuilder = new AgentPromptBuilder();
            string systemPrompt = promptBuilder.SystemPrompt();
            string runtimePrompt = promptBuilder.UserPrompt(new AgentRequestContext());
            Check(!systemPrompt.Contains("500m") && !systemPrompt.Contains("2.5m")
                && !systemPrompt.Contains("enable_multiple_fcus"),
                "System prompt contains stable role constraints rather than phrase-specific business rules");
            Check(runtimePrompt.Contains("execution_contract")
                && runtimePrompt.Contains("allowed_form_updates")
                && runtimePrompt.Contains("request_context"),
                "Runtime prompt carries capabilities, policy and current state as data");

            var invalidHandler = new FakeHandler { ResponseBody = Response("execute", 0) };
            using (var invalid = new OpenAiCompatibleAgentClient(new AgentConfiguration
            {
                BaseUrl = "https://agent.example/v1",
                Model = "model",
                ApiKey = "test-token"
            }, invalidHandler))
            {
                var result = invalid.CreatePlanAsync(new AgentRequestContext(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(result.Status == AgentCallStatus.InvalidResponse
                    && result.Validation.Errors.Count >= 2,
                    "Unsupported mode and zero cooling index are rejected locally");
                Check(invalidHandler.Authorization == "Bearer test-token",
                    "Configured key uses the Bearer authorization scheme");
            }

            var insecureHandler = new FakeHandler { ResponseBody = Response("diagnose", null) };
            using (var insecure = new OpenAiCompatibleAgentClient(new AgentConfiguration
            {
                BaseUrl = "http://agent.example/v1",
                Model = "model"
            }, insecureHandler))
            {
                var result = insecure.CreatePlanAsync(new AgentRequestContext(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(result.Status == AgentCallStatus.Unavailable && !insecureHandler.Called,
                    "Remote plaintext HTTP is rejected before transport");
            }

            var unauthenticatedHandler = new FakeHandler { ResponseBody = Response("diagnose", null) };
            using (var unauthenticated = new OpenAiCompatibleAgentClient(new AgentConfiguration
            {
                BaseUrl = AgentConfiguration.DefaultBaseUrl,
                Model = AgentConfiguration.DefaultModel
            }, unauthenticatedHandler))
            {
                var result = unauthenticated.CreatePlanAsync(new AgentRequestContext(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(result.Status == AgentCallStatus.Unavailable && !unauthenticatedHandler.Called,
                    "Remote endpoint without API key never sends a network request");
            }

            var aliyunHandler = new FakeHandler { ResponseBody = Response("create", 200) };
            using (var aliyun = new OpenAiCompatibleAgentClient(new AgentConfiguration
            {
                BaseUrl = AgentConfiguration.DefaultBaseUrl,
                Model = AgentConfiguration.DefaultModel,
                ApiKey = "test-token"
            }, aliyunHandler))
            {
                var result = aliyun.CreatePlanAsync(new AgentRequestContext(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Check(result.Status == AgentCallStatus.Success
                    && aliyunHandler.RequestBody.Contains("\"enable_thinking\":false"),
                    "Aliyun intent extraction explicitly disables provider thinking mode");
            }

            var duplicate = new AgentPlan
            {
                schema_version = 2, summary = "plan", mode = "diagnose",
                room_name_keywords = new List<string> { "office", "OFFICE" }
            };
            Check(!new AgentPlanValidator().Validate(duplicate).IsValid,
                "Duplicate plan values are rejected deterministically");

            var impossibleUnit = new AgentPlan
            {
                schema_version = 2, summary = "plan", mode = "create",
                room_name_keywords = new List<string> { "office" },
                enable_automatic_scope_discovery = true,
                connect_supply = true,
                door_offset_mm = 500000
            };
            Check(!new AgentPlanValidator().Validate(impossibleUnit).IsValid,
                "Implausible converted wall offset is rejected locally");

            var clarification = new AgentPlan
            {
                schema_version = 2, summary = "plan", mode = "create",
                connect_supply = true,
                clarifications = new List<string> { "Does 500m mean 500mm?" }
            };
            var clarificationResult = new AgentPlanValidator().Validate(clarification);
            Check(clarificationResult.IsValid && clarificationResult.RequiresClarification,
                "Ambiguous units produce a valid but non-applicable clarification plan");
            Console.WriteLine(count + " local agent core checks passed. Revit writes are NOT_RUN.");
        }

        public static void RunLive()
        {
            using (var client = new OpenAiCompatibleAgentClient(AgentConfiguration.FromEnvironment()))
            {
                var result = client.CreatePlanAsync(new AgentRequestContext
                {
                    UserRequest = "Cooling index 200 W/m2. Select all offices and meeting rooms in the current plan. Place units evenly, 500 mm from the door-side wall at elevation 2.5 m. Connect supply and return corridor mains. Do not enable condensate.",
                    CurrentMode = FcuOperationMode.Create,
                    CurrentRoomNameKeywords = new List<string> { "meeting room", "office" },
                    CoolingIndexWPerSquareMeter = 200,
                    DoorOffsetMm = 500,
                    FcuElevationMm = 2600,
                    EnableMultipleFcus = true,
                    EnableAutomaticScopeDiscovery = false,
                    EnableReturnPipe = true,
                    EnableCondensate = false
                }, CancellationToken.None).GetAwaiter().GetResult();
                bool valid = result.Validation != null && result.Validation.IsValid;
                Console.WriteLine("LIVE status=" + result.Status + "; validated=" + valid
                    + "; mode=" + (result.Validation == null ? "none" : result.Validation.Mode.ToString()));
                if (result.Plan != null)
                    Console.WriteLine("LIVE plan cooling=" + result.Plan.cooling_index_w_per_square_meter
                        + "; offset_mm=" + result.Plan.door_offset_mm
                        + "; elevation_mm=" + result.Plan.fcu_elevation_mm
                        + "; multiple=" + result.Plan.enable_multiple_fcus
                        + "; auto_scope=" + result.Plan.enable_automatic_scope_discovery
                        + "; supply=" + result.Plan.connect_supply
                        + "; return=" + result.Plan.connect_return
                        + "; condensate=" + result.Plan.connect_condensate
                        + "; clarifications=" + (result.Plan.clarifications == null ? 0 : result.Plan.clarifications.Count));
                if (result.Status != AgentCallStatus.Success || !valid)
                    throw new Exception("Live Agent contract check failed: " + result.Message);
            }
        }
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        FCUAutoDesign.Agent.AgentCoreChecks.Run();
        if (args.Length > 0 && args[0] == "--live") FCUAutoDesign.Agent.AgentCoreChecks.RunLive();
    }
}
'@
$imports = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

'@
$testDir = Join-Path ([IO.Path]::GetTempPath()) ('FCU-AgentCore-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testDir)
$testSource = Join-Path $testDir 'Checks.cs'
$exe = Join-Path $testDir 'Checks.exe'
$combined = $imports + ($sources -join [Environment]::NewLine) + [Environment]::NewLine + $checks
[IO.File]::WriteAllText($testSource, $combined, (New-Object Text.UTF8Encoding($true)))
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$framework = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
& $compiler /nologo /target:exe "/out:$exe" `
    "/reference:$framework\System.dll" `
    "/reference:$framework\System.Core.dll" `
    "/reference:$framework\System.Net.Http.dll" `
    "/reference:$framework\System.Web.Extensions.dll" $testSource
if ($LASTEXITCODE -ne 0) { throw 'Agent core harness compilation failed.' }
$originalProcessKey = $env:FCU_AGENT_API_KEY
if ($Live) {
    $env:FCU_AGENT_API_KEY = [Environment]::GetEnvironmentVariable('FCU_AGENT_API_KEY', 'User')
    & $exe --live
} else {
    & $exe
}
$env:FCU_AGENT_API_KEY = $originalProcessKey
if ($LASTEXITCODE -ne 0) { throw 'Agent core regression failed.' }
