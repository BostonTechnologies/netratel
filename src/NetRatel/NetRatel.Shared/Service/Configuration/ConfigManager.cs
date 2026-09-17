using NetRatel.Shared.Service.Logging;

namespace NetRatel.Shared.Service.Configuration
{
    public static class ConfigManager
    {
        private static readonly string ConfigFileName = "config.ini";
        public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        // Ensures the config.ini file exists, creating it with default keys if it doesn't.
        public static void EnsureConfigFileExists()
        {
            if (!File.Exists(ConfigPath))
            {
                LogManager.WriteLog("Creating config.ini");

                // Create the file with default keys and empty values.
                string[] defaultLines = {
                    "database=",
                    "rundeckuri=",
                    "rundecktoken="
                };

                File.WriteAllLines(ConfigPath, defaultLines);
            }
        }

        // Loads configuration from config.ini into a Dictionary.
        public static Dictionary<string, string> LoadConfig()
        {
            LogManager.WriteLog("Loading config.ini");
            // Ensure the file exists before attempting to read it.
            EnsureConfigFileExists();

            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(ConfigPath);
            foreach (string line in lines)
            {
                // Skip empty lines or lines that do not contain a key-value separator.
                if (string.IsNullOrWhiteSpace(line) || !line.Contains("="))
                    continue;

                string[] parts = line.Split('=', 2);  // Split into key and value.
                string key = parts[0].Trim();
                string value = parts.Length > 1 ? parts[1].Trim() : "";
                config[key] = value;
            }
            return config;
        }
    }
}
