using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace FCUAutoDesign.Agent
{
    public sealed class AgentSettingsLoadResult
    {
        public AgentConfiguration Configuration { get; set; }
        public bool IsSavedLocally { get; set; }
        public string Warning { get; set; }
    }

    public sealed class AgentSettingsStore
    {
        private const int CurrentSchemaVersion = 1;
        private readonly string settingsPath;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public AgentSettingsStore()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FCUAutoDesign", "agent-settings.json")) { }

        internal AgentSettingsStore(string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settingsPath))
                throw new ArgumentException("AI 配置路径不能为空。", "settingsPath");
            this.settingsPath = Path.GetFullPath(settingsPath);
        }

        public string SettingsPath => settingsPath;

        public AgentSettingsLoadResult Load()
        {
            if (!File.Exists(settingsPath))
                return EnvironmentFallback(null);
            try
            {
                if (new FileInfo(settingsPath).Length > 64 * 1024)
                    return EnvironmentFallback("本机 AI 配置文件超过大小限制，已改用环境变量或默认值。");
                StoredAgentSettings stored = serializer.Deserialize<StoredAgentSettings>(
                    File.ReadAllText(settingsPath, Encoding.UTF8));
                if (stored == null || stored.schema_version != CurrentSchemaVersion)
                    return EnvironmentFallback("本机 AI 配置版本无法识别，已改用环境变量或默认值。");

                string apiKey = null;
                if (!string.IsNullOrWhiteSpace(stored.encrypted_api_key))
                {
                    byte[] encrypted = Convert.FromBase64String(stored.encrypted_api_key);
                    byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    try { apiKey = Encoding.UTF8.GetString(plain); }
                    finally { Array.Clear(plain, 0, plain.Length); }
                }

                return new AgentSettingsLoadResult
                {
                    Configuration = new AgentConfiguration
                    {
                        BaseUrl = string.IsNullOrWhiteSpace(stored.base_url)
                            ? AgentConfiguration.DefaultBaseUrl : stored.base_url.Trim(),
                        Model = string.IsNullOrWhiteSpace(stored.model)
                            ? AgentConfiguration.DefaultModel : stored.model.Trim(),
                        ApiKey = apiKey
                    },
                    IsSavedLocally = true
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is InvalidOperationException || ex is FormatException
                || ex is CryptographicException || ex is ArgumentException)
            {
                return EnvironmentFallback("本机 AI 配置读取失败，已改用环境变量或默认值：" + ex.Message);
            }
        }

        public void Save(AgentConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");
            Uri endpoint;
            string error;
            if (!OpenAiCompatibleAgentClient.TryBuildEndpoint(configuration, out endpoint, out error))
                throw new InvalidOperationException(error);

            byte[] plain = Encoding.UTF8.GetBytes(configuration.ApiKey ?? string.Empty);
            byte[] encrypted = null;
            try
            {
                encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                StoredAgentSettings stored = new StoredAgentSettings
                {
                    schema_version = CurrentSchemaVersion,
                    base_url = configuration.BaseUrl.Trim(),
                    model = configuration.Model.Trim(),
                    encrypted_api_key = Convert.ToBase64String(encrypted)
                };
                string directory = Path.GetDirectoryName(settingsPath);
                Directory.CreateDirectory(directory);
                string temporaryPath = settingsPath + ".tmp";
                File.WriteAllText(temporaryPath, serializer.Serialize(stored), new UTF8Encoding(false));
                try
                {
                    if (File.Exists(settingsPath)) File.Replace(temporaryPath, settingsPath, null);
                    else File.Move(temporaryPath, settingsPath);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
            }
        }

        public void Delete()
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }

        private static AgentSettingsLoadResult EnvironmentFallback(string warning)
        {
            return new AgentSettingsLoadResult
            {
                Configuration = AgentConfiguration.FromEnvironment(),
                IsSavedLocally = false,
                Warning = warning
            };
        }

        private sealed class StoredAgentSettings
        {
            public int schema_version { get; set; }
            public string base_url { get; set; }
            public string model { get; set; }
            public string encrypted_api_key { get; set; }
        }
    }
}
