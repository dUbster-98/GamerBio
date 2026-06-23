using GamerBio.Components;
using GamerBio.Data;
using GamerBio.Hubs;
using GamerBio.Models;
using GamerBio.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();
builder.Services.AddSingleton<TensionAnalyzer>();

builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
});

var bioMonitorConnection = builder.Configuration.GetConnectionString("BioMonitor");
var useInMemoryDb = string.IsNullOrWhiteSpace(bioMonitorConnection);

var bioMonitorApiKey = builder.Configuration["BioMonitor:ApiKey"];
var requireApiKey = !string.IsNullOrWhiteSpace(bioMonitorApiKey);

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
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseForwardedHeaders();
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<BioSignalHub>(BioSignalHub.Path);

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

    var tension = analyzer.Update(entity);

    logger.LogInformation("Biosignal saved: id={Id} BPM={Bpm} GSR={Gsr} Temp={Temp} → tension={State}({Score})",
        entity.Id, entity.Bpm, entity.Gsr, entity.SkinTemp, tension.State, tension.Score);

    await hub.Clients.All.SendAsync(BioSignalHub.BioSignalReceived, entity);
    await hub.Clients.All.SendAsync(BioSignalHub.TensionUpdated, tension);

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

app.Run();

record BioSignalDto(int Bpm, int Gsr, double? SkinTemp, DateTimeOffset Timestamp);
