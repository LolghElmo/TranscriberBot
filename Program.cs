using System;
using System.IO;
using System.Threading.Tasks;
using TranscriberBot.Data.Handler;
using TranscriberBot.Data.Models;

namespace TranscriberBot
{
    public class Program
    {
        private const string ConfigPath = "config.json";

        private static async Task Main(string[] args)
        {
            Console.WriteLine("                                                                          \r\n,------.,--.                 ,--.           ,----.                  ,---. \r\n|  .---'|  |,--,--,--. ,---. |  |,---.     '  .-./    ,---.  ,---. /  .-' \r\n|  `--, |  ||        || .-. |`-'(  .-'     |  | .---.| .-. || .-. ||  `-, \r\n|  `---.|  ||  |  |  |' '-' '   .-'  `)    '  '--'  |' '-' '' '-' '|  .-' \r\n`------'`--'`--`--`--' `---'    `----'      `------'  `---'  `---' `--'   \r\n                                                                          ");
            Console.WriteLine("If you ever need to change your tokens, edit or delete 'config.json' and restart.\n");

            var config = LoadOrCreateConfig();

            while (true)
            {
                try
                {
                    await Bot.InitializeAsync(config);
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
