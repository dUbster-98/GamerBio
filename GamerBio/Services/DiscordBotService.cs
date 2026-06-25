using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GamerBio.Models;

namespace GamerBio.Services;

/// <summary>
/// Runs the Discord bot inside the same ASP.NET host as a hosted service, so it
/// shares the <see cref="TensionAnalyzer"/> singleton with the web/SignalR side.
/// Handles two flows: slash commands (BioCommands) and outbound stress alerts.
/// </summary>
public class DiscordBotService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly string _token;
    private readonly ulong _alertChannelId;

    // Only fire an alert when the state actually changes, so we don't spam the
    // channel on every biosignal sample.
    private TensionState _lastNotified = TensionState.Calibrating;

    public DiscordBotService(
        IConfiguration config,
        IServiceProvider services,
        ILogger<DiscordBotService> logger)
    {
        _services = services;
        _logger = logger;
        _token = config["Discord:Token"] ?? "";
        _ = ulong.TryParse(config["Discord:AlertChannelId"], out _alertChannelId);

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged,
            LogLevel = LogSeverity.Info,
        });
        _interactions = new InteractionService(_client);

        _client.Log += msg =>
        {
            _logger.LogInformation("[Discord] {Message}", msg.Message);
            return Task.CompletedTask;
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            _logger.LogWarning("Discord:Token not configured — Discord bot disabled.");
            return;
        }

        // Discover slash-command modules (BioCommands) in this assembly.
        await _interactions.AddModulesAsync(typeof(DiscordBotService).Assembly, _services);

        _client.Ready += async () =>
        {
            // Global commands can take up to ~1h to propagate. For instant
            // iteration during development, register to a single test guild
            // instead via RegisterCommandsToGuildAsync(guildId).
            await _interactions.RegisterCommandsGloballyAsync();
            _logger.LogInformation("Discord bot ready as {User}", _client.CurrentUser);
        };

        _client.InteractionCreated += async interaction =>
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(ctx, _services);
        };

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();

        // Keep the service alive until the host shuts down, then log out cleanly.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            await _client.LogoutAsync();
            await _client.StopAsync();
        }
    }

    /// <summary>
    /// Push a stress alert to the configured channel, but only on a transition
    /// into the Stressed state (deduplicated against the last notified state).
    /// </summary>
    public async Task NotifyTensionAsync(TensionReading tension)
    {
        if (tension.State == _lastNotified)
        {
            return;
        }
        _lastNotified = tension.State;

        if (tension.State != TensionState.Stressed || _alertChannelId == 0)
        {
            return;
        }

        if (_client.GetChannel(_alertChannelId) is IMessageChannel channel)
        {
            await channel.SendMessageAsync(
                $"🔥 **스트레스 감지!** 텐션 {tension.Score}/100 · 감정: {tension.DominantEmotion ?? "—"}");
        }
    }
}
