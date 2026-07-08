using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using Microsoft.Extensions.DependencyInjection;

using OneObfuscator.CLI.Emitters.LuauFrontend; // TODO: Migrate to separate `Emitters` namespace. Check for conflicts

namespace Heimdall;

class Program
{
    private static Task Log(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

    public static async Task Main()
    {
        var services = ConfigureServices();
        var client = services.GetRequiredService<DiscordSocketClient>();
        client.Log += Log;

        var handler = services.GetRequiredService<InteractionHandler>();
        await handler.InitializeAsync();

        await client.LoginAsync(
            TokenType.Bot,
            Environment.GetEnvironmentVariable("HEIMDALL_DC_TOKEN")
                ?? throw new InvalidOperationException("HEIMDALL_DC_TOKEN is missing.")
        );

        await client.StartAsync();
        await client.SetGameAsync("Great mountains shall never bow", type: ActivityType.Streaming);

        await Task.Delay(-1);
    }

    private static ServiceProvider ConfigureServices()
    {
        return new ServiceCollection()
            .AddSingleton(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds
            })
            .AddSingleton<DiscordSocketClient>()
            .AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>()))
            .AddSingleton<InteractionHandler>()
            .AddSingleton<HttpClient>()
            .AddSingleton(sp => new IrWorkerPool(workerCount: 4, sp.GetRequiredService<HttpClient>()))
            .BuildServiceProvider();
    }
}
