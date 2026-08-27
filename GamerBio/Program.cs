using System.Globalization;
using GamerBio.Components;
using GamerBio.Data;
using GamerBio.Hubs;
using GamerBio.Models;
using GamerBio.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();

// 서버 렌더 페이지들을 위한 인프로세스 팬아웃. 예전에는 각 페이지가
// OnInitializedAsync 안에서 이 호스트의 공개 URL로 SignalR HubConnection을 열어
// 구독했다 — 즉 Cloudflare Tunnel을 거쳐 자기 자신에게 되돌아오는 왕복이었다.
builder.Services.AddSingleton<BioEventBus>();
builder.Services.AddSingleton<TensionAnalyzer>();
builder.Services.AddSingleton<GalleryStorage>();
builder.Services.AddSingleton<NewsStorage>();
builder.Services.AddSingleton<RandomPhotoStore>();

// Discord 봇은 별도 프로세스가 아니라 이 호스트 안의 hosted service로 돌아간다.
// 그래야 TensionAnalyzer 싱글톤을 웹/SignalR과 함께 공유할 수 있다. 한 번만
// 등록하고 hosted service와 주입 가능한 싱글톤 양쪽으로 해석되게 한다
// (엔드포인트가 봇에 알림을 밀어넣을 수 있도록).
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());

// /cam 리버스 프록시가 쓰는 HttpClient. MJPEG는 끊기지 않는 장기 스트림이므로
// 기본 100초 타임아웃을 반드시 꺼야 한다 — 그대로 두면 영상이 끊긴다.
builder.Services.AddHttpClient("camera", c => c.Timeout = Timeout.InfiniteTimeSpan);

// Cloudflare Tunnel 뒤에 있으므로 원본 클라이언트 IP와 스킴(https)은 프록시가
// 붙여주는 X-Forwarded-* 헤더에만 남는다. KnownProxies를 비우는 것은 터널의
// 주소가 고정이 아니라 검증 대상 목록을 만들 수 없기 때문이다.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownProxies.Clear();
});

// 연결 문자열이 없으면 로컬 디버그로 보고 InMemory DB로 폴백한다.
var bioMonitorConnection = builder.Configuration.GetConnectionString("BioMonitor");
var useInMemoryDb = string.IsNullOrWhiteSpace(bioMonitorConnection);

// API 키가 설정된 경우에만 /api 그룹에 인증 필터를 건다 (미설정 = 개발용 무인증).
var bioMonitorApiKey = builder.Configuration["BioMonitor:ApiKey"];
var requireApiKey = !string.IsNullOrWhiteSpace(bioMonitorApiKey);

// PC의 Python MJPEG 서버 내부 주소 (예: http://192.168.0.50:8080/).
// 서버 전용 값이다 — 브라우저는 이 주소를 절대 보지 못하고, 이 호스트의 /cam만 요청한다.
var cameraUpstream = builder.Configuration["Camera:UpstreamUrl"];

builder.Services.AddDbContext<BioMonitorContext>(opts =>
{
    if (useInMemoryDb)
    {
        opts.UseInMemoryDatabase("BioMonitorDev");
    }
    else
    {
        opts.UseNpgsql(bioMonitorConnection);
    }
});

var app = builder.Build();

// 앱 기동 시 스키마를 맞춰둔다: 실제 DB는 마이그레이션 적용, InMemory는 생성만.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BioMonitorContext>();
    if (useInMemoryDb)
    {
        await db.Database.EnsureCreatedAsync();
        app.Logger.LogWarning("BioMonitor connection string not configured — using EF Core InMemory provider for local debug.");
    }
    else
    {
        await db.Database.MigrateAsync();
    }
}

if (!requireApiKey)
{
    app.Logger.LogWarning("BioMonitor:ApiKey not configured — /api endpoints are unauthenticated. Set the key in appsettings.Production.json before exposing publicly.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// 주소창에 직접 입력된 미매칭 경로용. 앱 내부 링크는 인터랙티브 라우터가
// Routes.razor의 <NotFound>로 직접 처리한다 (문서 재요청 없음).
app.UseStatusCodePagesWithReExecute("/not-found");
// UseForwardedHeaders는 반드시 HttpsRedirection보다 먼저 와야 한다. 그래야
// 터널이 넘겨준 X-Forwarded-Proto: https가 반영되어 무한 리다이렉트가 안 생긴다.
app.UseForwardedHeaders();
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// 외부/브라우저 클라이언트용 Hub. 서버 렌더 페이지는 BioEventBus를 쓰므로 여기에 붙지 않는다.
app.MapHub<BioSignalHub>(BioSignalHub.Path);

// PC의 LAN 전용 MJPEG 스트림을 우리 자신의 (HTTPS) 출처 아래로 리버스 프록시한다.
// 그래야 공개 대시보드가 mixed-content 차단이나 도달성 문제 없이 영상을 띄운다:
//   브라우저 → https://bio-monitor.uk/cam → (Cloudflare Tunnel) → 여기 → PC:8080
app.MapGet("/cam", async (HttpContext ctx, IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(cameraUpstream))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var client = httpFactory.CreateClient("camera");
    try
    {
        // ResponseHeadersRead: 본문을 기다리지 않고 헤더만 오면 곧바로 중계 시작.
        using var upstream = await client.GetAsync(
            cameraUpstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString()
            ?? "multipart/x-mixed-replace";
        ctx.Response.Headers.CacheControl = "no-cache, no-store, private";

        // multipart 피드를 그대로 흘려보낸다 — 끝이 없는 본문을 절대 버퍼링하지 않는다.
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var stream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // 브라우저가 페이지를 떠났거나 호스트가 종료 중 — 할 일 없음.
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Camera upstream unreachable: {Upstream}", cameraUpstream);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }
});

// 갤러리 이미지를 id로 서빙한다. 파일 자체는 디스크(wwwroot 바깥)에 있고 여기서
// 스트리밍하므로 저장 경로가 외부에 노출되지 않고 접근도 통제된다.
app.MapGet("/gallery/media/{id:long}", async (
    long id, BioMonitorContext db, GalleryStorage storage) =>
{
    var photo = await db.GalleryPhotos.FindAsync(id);
    if (photo is null)
    {
        return Results.NotFound();
    }

    var path = storage.PathFor(photo);
    if (!File.Exists(path))
    {
        return Results.NotFound();
    }

    return Results.File(path, photo.ContentType, enableRangeProcessing: true);
});

// 하루치 뉴스 HTML(RPi 저녁 스케줄러가 만든 것)을 날짜로 서빙한다.
// 파일은 wwwroot 바깥 디스크에 있고, NewsStorage가 날짜 형식을 검증해 올바른
// yyyy-MM-dd만 실제 파일로 해석되게 한다 (경로 탈출 방지). /news 페이지가 이걸
// 샌드박스 iframe에 심어서 기사가 자기 스타일을 유지하도록 한다.
app.MapGet("/news/media/{date}", (string date, NewsStorage storage) =>
{
    var path = storage.PathFor(date);
    return path is null
        ? Results.NotFound()
        : Results.File(path, "text/html; charset=utf-8");
});

var api = app.MapGroup("/api");
// X-Api-Key 헤더 검사. 그룹 전체에 걸리므로 아래 엔드포인트들은 개별 처리가 없다.
if (requireApiKey)
{
    api.AddEndpointFilter(async (ctx, next) =>
    {
        if (!ctx.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var provided)
            || provided != bioMonitorApiKey)
        {
            return Results.Unauthorized();
        }
        return await next(ctx);
    });
}

// ESP32가 2초 주기로 올리는 생체 샘플: 저장 → 감정과 융합 → 실시간 브로드캐스트.
api.MapPost("/biosignal", async (
    BioSignalDto dto,
    BioMonitorContext db,
    IHubContext<BioSignalHub> hub,
    BioEventBus bus,
    TensionAnalyzer analyzer,
    DiscordBotService bot,
    ILogger<Program> logger) =>
{
    var entity = new BioSignal
    {
        Bpm = dto.Bpm,
        Gsr = dto.Gsr,
        SkinTemp = dto.SkinTemp,
        Timestamp = dto.Timestamp,
        ReceivedAt = DateTimeOffset.UtcNow,
    };
    db.BioSignals.Add(entity);
    await db.SaveChangesAsync();

    var tension = analyzer.UpdateBio(entity, out var deadlyEntry);
    if (deadlyEntry is not null)
    {
        await RecordDeadlyEntryAsync(deadlyEntry, db, hub, bus, logger);
    }

    logger.LogInformation("Biosignal saved: id={Id} BPM={Bpm} GSR={Gsr} Temp={Temp} → tension={State}({Score})",
        entity.Id, entity.Bpm?.ToString() ?? "-", entity.Gsr, entity.SkinTemp, tension.State, tension.Score);

    // 두 경로로 동시에 발행한다: SignalR = 외부 브라우저 클라이언트,
    // BioEventBus = 같은 프로세스 안의 서버 렌더 페이지.
    await hub.Clients.All.SendAsync(BioSignalHub.BioSignalReceived, entity);
    await hub.Clients.All.SendAsync(BioSignalHub.TensionUpdated, tension);
    bus.PublishBioSignal(entity);
    bus.PublishTension(tension);
    bot.NotifyTension(tension);   // 큐에 넣고 반환: 응답이 Discord를 기다리면 안 된다

    return Results.Ok(new { id = entity.Id, receivedAt = entity.ReceivedAt, tension });
});

api.MapGet("/biosignal/recent", async (BioMonitorContext db, int take = 20) =>
{
    var items = await db.BioSignals
        .OrderByDescending(x => x.Timestamp)
        .Take(Math.Clamp(take, 1, 200))
        .ToListAsync();
    return Results.Ok(items);
});

api.MapGet("/deadly/recent", async (BioMonitorContext db, int take = 20) =>
{
    var items = await db.DeadlyEvents
        .OrderByDescending(x => x.OccurredAt)
        .Take(Math.Clamp(take, 1, 200))
        .ToListAsync();
    return Results.Ok(items);
});

// 뉴스를 만드는 세션에서 하루치 뉴스 파일(업로드된 news.html)을 받아 디스크
// (wwwroot 바깥)에 오늘 날짜인 yyyy-MM-dd.html로 이름을 바꿔 보관한다 —
// 그러면 /news 페이지가 목록에 띄우고 /news/media/{date}로 서빙한다.
// html 파일 하나가 담긴 multipart/form-data를 받고, 없으면 원시 html 본문으로
// 폴백한다. 선택 파라미터 ?date=로 오늘 날짜를 덮어쓸 수 있고, 같은 날짜의
// 기존 파일은 덮어쓴다.
api.MapPost("/news", async (
    HttpRequest req,
    NewsStorage storage,
    string? date,
    ILogger<Program> logger) =>
{
    var ct = req.HttpContext.RequestAborted;
    byte[] bytes;

    if (req.HasFormContentType && req.Form.Files.Count > 0)
    {
        // 파일로 업로드된 경우 (예: news.html) — 첫 번째 파일 필드를 사용한다.
        var file = req.Form.Files[0];
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }
    else
    {
        // 폴백: 원시 html 본문.
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }

    if (bytes.Length == 0)
    {
        return Results.BadRequest("empty news body");
    }
    if (bytes.Length > NewsStorage.MaxFileBytes)
    {
        return Results.BadRequest("news too large");
    }

    // 들어오는 파일 이름은 news.html이다. 저장 시점에 날짜 이름으로 바꾼다.
    var day = string.IsNullOrWhiteSpace(date)
        ? DateTime.Now.ToString(NewsStorage.DateFormat, CultureInfo.InvariantCulture)
        : date;

    var key = await storage.SaveHtmlAsync(day, bytes, ct);
    if (key is null)
    {
        return Results.BadRequest($"invalid date '{day}' — expected {NewsStorage.DateFormat}");
    }

    logger.LogInformation("News received for {Date} ({Bytes} bytes)", key, bytes.Length);
    return Results.Ok(new { date = key, bytes = bytes.Length });
});

// PC에서 이미 캡처된 프레임(DeepFace가 임계값 초과로 채점한 바로 그 프레임)을
// 받아 갤러리에 넣는다. 발생 지점에서 캡처하면, 서버가 사후에 라이브 프레임을
// 다시 잡을 때 생기는 지연과 불일치를 피할 수 있다.
api.MapPost("/gallery/capture", async (
    HttpRequest req,
    BioMonitorContext db,
    GalleryStorage storage,
    IHubContext<BioSignalHub> hub,
    BioEventBus bus,
    string? emotion,
    double? score) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms, req.HttpContext.RequestAborted);
    var bytes = ms.ToArray();

    if (bytes.Length == 0)
    {
        return Results.BadRequest("empty image body");
    }
    if (bytes.Length > GalleryStorage.MaxFileBytes)
    {
        return Results.BadRequest("image too large");
    }

    // 이미지 파일은 디스크에, 메타데이터만 DB에 남긴다.
    var storedName = await storage.SaveBytesAsync(bytes, ".jpg", CancellationToken.None);
    var now = DateTimeOffset.UtcNow;
    var emo = string.IsNullOrWhiteSpace(emotion) ? "surprise" : emotion.ToLowerInvariant();
    var photo = new GalleryPhoto
    {
        StoredName = storedName,
        OriginalName = $"auto-{now.LocalDateTime:yyyyMMdd-HHmmss}.jpg",
        ContentType = "image/jpeg",
        SizeBytes = bytes.Length,
        Caption = score is double s
            ? $"😲 {emo} auto-capture · {s:0}%"
            : $"😲 {emo} auto-capture",
        UploadedAt = now,
    };
    db.GalleryPhotos.Add(photo);
    await db.SaveChangesAsync();

    await hub.Clients.All.SendAsync(BioSignalHub.GalleryPhotoAdded, photo);
    bus.PublishGalleryPhoto(photo);
    return Results.Ok(new { id = photo.Id });
});

// PC DeepFace가 보내는 표정 분석 결과. 생체와 달리 DB에 남기지 않고 메모리상
// 최신값으로만 융합에 쓴다.
api.MapPost("/emotion", async (
    EmotionDto dto,
    BioMonitorContext db,
    IHubContext<BioSignalHub> hub,
    BioEventBus bus,
    TensionAnalyzer analyzer,
    DiscordBotService bot,
    ILogger<Program> logger) =>
{
    var reading = new EmotionReading(
        string.IsNullOrWhiteSpace(dto.Dominant) ? "neutral" : dto.Dominant,
        dto.Scores ?? new Dictionary<string, double>(),
        DateTimeOffset.UtcNow);

    // 감정을 가장 최근 생체 샘플과 융합하고 텐션을 다시 브로드캐스트한다.
    var tension = analyzer.UpdateEmotion(reading, out var deadlyEntry);
    if (deadlyEntry is not null)
    {
        await RecordDeadlyEntryAsync(deadlyEntry, db, hub, bus, logger);
    }

    await hub.Clients.All.SendAsync(BioSignalHub.EmotionUpdated, reading);
    await hub.Clients.All.SendAsync(BioSignalHub.TensionUpdated, tension);
    bus.PublishEmotion(reading);
    bus.PublishTension(tension);
    bot.NotifyTension(tension);   // 큐에 넣고 반환: 응답이 Discord를 기다리면 안 된다

    return Results.Ok(new { tension });
});

app.Run();

// Deadly 진입 스냅샷을 저장하고 지금 보고 있는 클라이언트들에게 흘려보낸다
// (/deadly 페이지가 실시간으로 맨 앞에 추가한다 — 갤러리 자동 캡처와 같은 흐름).
static async Task RecordDeadlyEntryAsync(
    DeadlyEvent entry,
    BioMonitorContext db,
    IHubContext<BioSignalHub> hub,
    BioEventBus bus,
    ILogger logger)
{
    db.DeadlyEvents.Add(entry);
    await db.SaveChangesAsync();
    logger.LogWarning("Deadly tension entered: score={Score} BPM={Bpm} GSR={Gsr} emotion={Emotion}",
        entry.Score, entry.Bpm?.ToString() ?? "-", entry.Gsr, entry.DominantEmotion ?? "-");
    await hub.Clients.All.SendAsync(BioSignalHub.DeadlyEventRecorded, entry);
    bus.PublishDeadlyEvent(entry);
}

// Bpm은 선택 항목이다: PPG 센서에 피부 접촉이 없는 동안 웨어러블은 이 값을 빼고
// (null로) 보내며, 분석기는 이를 심박 0이 아니라 "측정값 없음"으로 취급한다.
record BioSignalDto(int? Bpm, int Gsr, double? SkinTemp, DateTimeOffset Timestamp);
record EmotionDto(string? Dominant, Dictionary<string, double>? Scores, DateTimeOffset? Timestamp);
