using TranscriberBot.Data.Handler;
using TranscriberBot.Data.Models;

namespace TranscriberBot
{
    public class Program
    {
        private const string ConfigPath = "config.json";
        private const string VoiceConfigPath = "voicemodule.json";
        private static async Task Main(string[] args)
        {
            Console.WriteLine("                                                                          \r\n,------.,--.                 ,--.           ,----.                  ,---. \r\n|  .---'|  |,--,--,--. ,---. |  |,---.     '  .-./    ,---.  ,---. /  .-' \r\n|  `--, |  ||        || .-. |`-'(  .-'     |  | .---.| .-. || .-. ||  `-, \r\n|  `---.|  ||  |  |  |' '-' '   .-'  `)    '  '--'  |' '-' '' '-' '|  .-' \r\n`------'`--'`--`--`--' `---'    `----'      `------'  `---'  `---' `--'   \r\n                                                                          ");
            Console.WriteLine("If you ever need to change your tokens, edit or delete 'config.json' and restart.\n");

            var config = LoadOrCreateConfig();
            var voiceConfig = LoadOrCreateVoiceConfig();

            while (true)
            {
                try
                {
                    await Bot.InitializeAsync(config, voiceConfig);
                    Console.WriteLine("Bot started successfully!");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\nError initializing bot: {ex.Message}");
                    Console.WriteLine("Let's try re‑entering your tokens.\n");

                    config = PromptForConfig();
                    JsonHandler.SaveJson(ConfigPath, config);

                    Console.WriteLine("\nConfiguration updated. Retrying...\n");
                }
            }
        }

        private static Config LoadOrCreateConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    Console.WriteLine("Loading configuration from 'config.json'…");
                    var existing = JsonHandler.LoadJson<Config>(ConfigPath);
                    Console.WriteLine("Loaded.\n");
                    return existing;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load existing config: {ex.Message}");
                }
            }

            Console.WriteLine("No valid configuration found. Let's set it up now.\n");
            var config = PromptForConfig();
            JsonHandler.SaveJson(ConfigPath, config);
            Console.WriteLine("\nConfiguration saved to 'config.json'.\n");
            return config;
        }
        private static VoiceModuleConfig LoadOrCreateVoiceConfig()
        {
            if (File.Exists(VoiceConfigPath))
            {
                try
                {
                    Console.WriteLine($"Loading voice config from '{VoiceConfigPath}'…");
                    var existing = JsonHandler.LoadJson<VoiceModuleConfig>(VoiceConfigPath);
                    Console.WriteLine("Loaded voice config.\n");
                    return existing;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load voice config: {ex.Message}");
                }
            }

            Console.WriteLine("No valid voice config found. Creating a new one.\n");
            var cfg = new VoiceModuleConfig();
            JsonHandler.SaveJson(VoiceConfigPath, cfg);
            Console.WriteLine($"Voice config saved to '{VoiceConfigPath}'.\n");
            return cfg;
        }

        private static Config PromptForConfig()
        {
            string botToken;
            do
            {
                Console.Write("Discord Bot Token: ");
                botToken = Console.ReadLine()?.Trim();
            }
            while (string.IsNullOrEmpty(botToken));

            string assemblyToken;
            do
            {
                Console.Write("Assembly AI Token: ");
                assemblyToken = Console.ReadLine()?.Trim();
            }
            while (string.IsNullOrEmpty(assemblyToken));

            return new Config
            {
                BotToken = botToken,
                AssemblyAIToken = assemblyToken
            };
        }
    }
}
