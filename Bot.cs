
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using TranscriberBot.Data.Models;
using TranscriberBot.Data.Handler;
using TranscriberBot.Commands.SlashCommands;
using NetCord.Gateway;
using NetCord;
using NetCord.Services.ApplicationCommands;
using NetCord.Rest;
using NetCord.Services;

namespace TranscriberBot
{
    public static class Bot
    {
        public static Config? configFile { get; private set; }
        
        public static async Task InitializeAsync(Config config)
        {
            configFile = config;
            GatewayClientConfiguration gatewayClientConfig = new GatewayClientConfiguration()
                {
                Intents = default
            };
            GatewayClient client = new(new BotToken(configFile.BotToken), gatewayClientConfig);

            ApplicationCommandService<ApplicationCommandContext> applicationCommandService = new();
            applicationCommandService.AddModules(typeof(Program).Assembly);
            
            client.InteractionCreate += async interaction =>
            {
                if (interaction is not ApplicationCommandInteraction applicationCommandInteraction)
                    return;

                var result = await applicationCommandService.ExecuteAsync(new ApplicationCommandContext(applicationCommandInteraction, client));

                if (result is not IFailResult failResult)
                    return;

                try
                {
                    await interaction.SendResponseAsync(InteractionCallback.Message(failResult.Message));
                }
                catch
                {
                }
            };

            await applicationCommandService.CreateCommandsAsync(client.Rest, client.Id);
            client.Log += message =>
            {
                Console.WriteLine(message);
                return default;
            };
            await client.StartAsync();
            await Task.Delay(-1);
        }
    }
}
