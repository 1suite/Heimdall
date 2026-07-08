using System.Reflection;
using System.Text;

using Discord.Interactions;
using Discord.WebSocket;

namespace Heimdall;

public class InteractionHandler(DiscordSocketClient client, InteractionService interactions, IServiceProvider services)
{
    private readonly DiscordSocketClient _client = client;
    private readonly InteractionService _interactions = interactions;
    private readonly IServiceProvider _services = services;

    public async Task InitializeAsync()
    {
        await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

        _client.InteractionCreated += async interaction =>
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(ctx, _services);
        };

        _client.Ready += async () => await _interactions.RegisterCommandsToGuildAsync(
            ulong.Parse(
                Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("REGISTRY_SERVER_ID")
                    ?? throw new InvalidOperationException("REGISTRY_SERVER_ID is missing."))
            )
        );
    }
}