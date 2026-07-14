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
builder.Services.AddSingleton<TensionAnalyzer>();
builder.Services.AddSingleton<GalleryStorage>();
builder.Services.AddSingleton<NewsStorage>();
builder.Services.AddSingleton<RandomPhotoStore>();

// Discord bot runs as a hosted service in this same host so it can share the
// TensionAnalyzer singleton. Registered once and resolved as both the hosted
// service and an injectable singleton (so endpoints can push alerts to it).
builder.Services.AddSingleton<DiscordBotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());

// HttpClient used by the /cam reverse proxy. MJPEG is a long-lived stream, so
// the default 100s timeout must be disabled or it would kill the feed.
builder.Services.AddHttpClient("camera", c => c.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownProxies.Clear();
});

var bioMonitorConnection = builder.Configuration.GetConnectionString("BioMonitor");
var useInMemoryDb = string.IsNullOrWhiteSpace(bioMonitorConnection);

var bioMonitorApiKey = builder.Configuration["BioMonitor:ApiKey"];
var requireApiKey = !string.IsNullOrWhiteSpace(bioMonitorApiKey);

// Internal address of the PC's Python MJPEG server (e.g. http://192.168.0.50:8080/).
// Server-side only: the browser never sees it — it just requests /cam on this host.
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
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseForwardedHeaders();
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<BioSignalHub>(BioSignalHub.Path);

// Reverse-proxy the PC's LAN-only MJPEG stream under our own (HTTPS) origin so
// the public dashboard can show it without mixed-content or reachability issues:
//   browser → https://bio-monitor.uk/cam → (Cloudflare Tunnel) → here → PC:8080
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
        using var upstream = await client.GetAsync(
            cameraUpstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString()
            ?? "multipart/x-mixed-replace";
        ctx.Response.Headers.CacheControl = "no-cache, no-store, private";

        // Stream the multipart feed straight through — never buffer an infinite body.
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var stream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // Browser navigated away or the host is shutting down — nothing to do.
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

// Serve a gallery image by id. Files live on disk (outside wwwroot) and are
// streamed here so the storage path stays server-side and access stays guarded.
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

// Serve a day's news HTML (produced by the RPi's evening scheduler) by date.
// Files live on disk outside wwwroot; NewsStorage validates the date so only a
// well-formed yyyy-MM-dd resolves to a file (no path traversal). The /news page
// embeds this in a sandboxed iframe so the article keeps its own styling.
app.MapGet("/news/media/{date}", (string date, NewsStorage storage) =>
{
    var path = storage.PathFor(date);
    return path is null
        ? Results.NotFound()
        : Results.File(path, "text/html; charset=utf-8");
});

var api = app.MapGroup("/api");
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

api.MapPost("/biosignal", async (
    BioSignalDto dto,
    BioMonitorContext db,
    IHubContext<BioSignalHub> hub,
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
        await RecordDeadlyEntryAsync(deadlyEntry, db, hub, logger);
    }

    logger.LogInformation("Biosignal saved: id={Id} BPM={Bpm} GSR={Gsr} Temp={Temp} → tension={State}({Score})",
        entity.Id, entity.Bpm, entity.Gsr, entity.SkinTemp, tension.State, tension.Score);

    await hub.Clients.All.SendAsync(BioSignalHub.BioSignalReceived, entity);
    await hub.Clients.All.SendAsync(BioSignalHub.TensionUpdated, tension);
    await bot.NotifyTensionAsync(tension);

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

// Receive the day's news file (an uploaded news.html) from the news-producing
// session and file it on disk (outside wwwroot), renamed to today's date as
// yyyy-MM-dd.html — which the /news page then lists and serves via
// /news/media/{date}. Accepts multipart/form-data with a single html file, or a
// raw html body as a fallback. Optional ?date= overrides today; an existing file
// for that date is overwritten.
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
        // Uploaded as a file (e.g. news.html) — take the first file field.
        var file = req.Form.Files[0];
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
    }
    else
    {
        // Fallback: raw html body.
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

    // The incoming file is named news.html; we rename it to the date on save.
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

// Receive an already-captured frame from the PC (the exact frame DeepFace
// scored above threshold) and file it in the gallery. Capturing at the source
// avoids the lag/mismatch of the server re-grabbing a live frame after the fact.
api.MapPost("/gallery/capture", async (
    HttpRequest req,
    BioMonitorContext db,
    GalleryStorage storage,
    IHubContext<BioSignalHub> hub,
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
    return Results.Ok(new { id = photo.Id });
});

api.MapPost("/emotion", async (
    EmotionDto dto,
    BioMonitorContext db,
    IHubContext<BioSignalHub> hub,
    TensionAnalyzer analyzer,
    DiscordBotService bot,
    ILogger<Program> logger) =>
{
    var reading = new EmotionReading(
        string.IsNullOrWhiteSpace(dto.Dominant) ? "neutral" : dto.Dominant,
        dto.Scores ?? new Dictionary<string, double>(),
        DateTimeOffset.UtcNow);

    // Fuse the emotion with the most recent biosignal and re-broadcast tension.
    var tension = analyzer.UpdateEmotion(reading, out var deadlyEntry);
    if (deadlyEntry is not null)
    {
        await RecordDeadlyEntryAsync(deadlyEntry, db, hub, logger);
    }

    await hub.Clients.All.SendAsync(BioSignalHub.EmotionUpdated, reading);
    await hub.Clients.All.SendAsync(BioSignalHub.TensionUpdated, tension);
    await bot.NotifyTensionAsync(tension);

    return Results.Ok(new { tension });
});

app.Run();

// Persist a Deadly-entry snapshot and fan it out to live viewers (the /deadly
// page prepends it in real time, mirroring the gallery's auto-capture flow).
static async Task RecordDeadlyEntryAsync(
    DeadlyEvent entry,
    BioMonitorContext db,
    IHubContext<BioSignalHub> hub,
    ILogger logger)
{
    db.DeadlyEvents.Add(entry);
    await db.SaveChangesAsync();
    logger.LogWarning("Deadly tension entered: score={Score} BPM={Bpm} GSR={Gsr} emotion={Emotion}",
        entry.Score, entry.Bpm, entry.Gsr, entry.DominantEmotion ?? "-");
    await hub.Clients.All.SendAsync(BioSignalHub.DeadlyEventRecorded, entry);
}

record BioSignalDto(int Bpm, int Gsr, double? SkinTemp, DateTimeOffset Timestamp);
record EmotionDto(string? Dominant, Dictionary<string, double>? Scores, DateTimeOffset? Timestamp);
