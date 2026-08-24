using System.Threading.Channels;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GamerBio.Models;

namespace GamerBio.Services;

/// <summary>
/// Runs the Discord bot inside the same ASP.NET host as a hosted service, so it
/// shares the <see cref="TensionAnalyzer"/> singleton with the web/SignalR side.
/// Handles two flows: slash commands and outbound stress alerts.
/// </summary>
public class DiscordBotService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly string _token;
    private readonly ulong _alertChannelId;

    // Alerts are queued here instead of being sent inline, so that a slow (or
    // rate-limited) Discord REST call never delays the HTTP response to the ESP32.
    // Bounded + DropOldest means a stalled bot can't grow this without limit, and
    // dropping a stale reading is harmless: the dedup below keys off the newest state.
    private readonly Channel<TensionReading> _alerts =
        Channel.CreateBounded<TensionReading>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    // Only fire an alert when the state actually changes, so we don't spam the
    // channel on every biosignal sample. Touched only by the single drain loop,
    // so it needs no synchronization.
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

        // Discover slash-command modules in this assembly.
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

        // Drain queued alerts until the host shuts down, then log out cleanly.
        try
        {
            await DrainAlertsAsync(stoppingToken);
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
    /// Queue a stress alert. Returns immediately and never throws, so request
    /// handlers can call it without tying their response time to Discord's API.
    /// If the bot is disabled, queued readings are simply discarded.
    /// </summary>
    public void NotifyTension(TensionReading tension) => _alerts.Writer.TryWrite(tension);

    /// <summary>
    /// Single consumer for <see cref="_alerts"/>. A failed send is logged and skipped;
    /// it must never tear down the loop, or alerts would stop silently.
    /// </summary>
    private async Task DrainAlertsAsync(CancellationToken stoppingToken)
    {
        await foreach (var tension in _alerts.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAlertAsync(tension);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord alert send failed; skipping this one.");
            }
        }
    }

    /// <summary>
    /// Push a stress alert to the configured channel, but only on a transition
    /// into the Stressed or Deadly state (deduplicated against the last notified state).
    /// </summary>
    private async Task SendAlertAsync(TensionReading tension)
    {
        if (tension.State == _lastNotified)
        {
            return;
        }
        _lastNotified = tension.State;

        if (tension.State is not (TensionState.Stressed or TensionState.Deadly)
            || _alertChannelId == 0)
        {
            return;
        }

        string message = tension.State == TensionState.Deadly
            ? $"☠️ **위험 수준 텐션!** 텐션 {tension.Score}/100 · 감정: {tension.DominantEmotion ?? "—"}"
            : $"🔥 **스트레스 감지!** 텐션 {tension.Score}/100 · 감정: {tension.DominantEmotion ?? "—"}";

        if (_client.GetChannel(_alertChannelId) is IMessageChannel channel)
        {
            await channel.SendMessageAsync(message);
        }
        else
        {
            _logger.LogWarning("Discord alert channel {ChannelId} not reachable (bot not ready?)", _alertChannelId);
        }
    }
}
