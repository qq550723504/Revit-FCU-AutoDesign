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
                schema_version = 1,
                summary = "Read-only plan",
                mode = mode,
                room_name_keywords = new List<string> { "office" },
                cooling_index_w_per_square_meter = coolingIndex,
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
                    "Aliyun workspace compatible endpoint is the default base URL");
                Check(string.IsNullOrWhiteSpace(defaults.Model)
                    && string.IsNullOrWhiteSpace(defaults.ApiKey),
                    "Default configuration keeps model and API key empty");
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
                    && handler.RequestBody.Contains("explain failures"),
                    "Request contains configured model and serialized user context");
            }

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

            var duplicate = new AgentPlan
            {
                schema_version = 1, summary = "plan", mode = "diagnose",
                room_name_keywords = new List<string> { "office", "OFFICE" }
            };
            Check(!new AgentPlanValidator().Validate(duplicate).IsValid,
                "Duplicate plan values are rejected deterministically");
            Console.WriteLine(count + " agent core checks passed. External model calls and Revit writes are NOT_RUN.");
        }
    }
}

public static class Program
{
    public static void Main() { FCUAutoDesign.Agent.AgentCoreChecks.Run(); }
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
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Agent core regression failed.' }
