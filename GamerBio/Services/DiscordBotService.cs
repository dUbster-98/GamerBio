using System.Threading.Channels;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GamerBio.Models;

namespace GamerBio.Services;

public class DiscordBotService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly string _token;
    private readonly ulong _alertChannelId;

    // 알람은 ESP32에 대한 HTTP 응답을 지연시키지 않도록 인라인으로 전송되는 대신 대기열에 추가
    // 정체된 봇이 이를 무제한으로 성장시키지 못하도록 함.
    private readonly Channel<TensionReading> _alerts =
        Channel.CreateBounded<TensionReading>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    // 알람은 상태가 실제로 변경될 때만 발생
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

        await _interactions.AddModulesAsync(typeof(DiscordBotService).Assembly, _services);

        _client.Ready += async () =>
        {
            // 디스코드 명령 등록 시간을 기다리지 않고 테스트
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

        // 알람을 처리하는 동안 봇이 종료되면 예외발생할 수 있으므로 무시하고 종료.
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

    // 알람을 대기열에 추가. 봇이 종료되면 무시.
    public void NotifyTension(TensionReading tension) => _alerts.Writer.TryWrite(tension);

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
