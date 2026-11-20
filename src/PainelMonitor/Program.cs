using System.Text.Json;
using PainelMonitor;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
// O painel e' consultado a cada 1 s e o log de requisicao do ASP.NET (~5 linhas por
// chamada) enchia ~65 MB/dia. Do ASP.NET so avisos e erros; o resto (Hosting.Lifetime,
// PainelMonitor, SensorHub) continua em Information.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton<SensorHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SensorHub>());
builder.Services.AddSingleton<FrameHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FrameHub>());
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));

var url = Environment.GetEnvironmentVariable("PAINEL_URL") ?? "http://0.0.0.0:8080";
builder.WebHost.UseUrls(url);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/metrics", (SensorHub hub) =>
    Results.Text(hub.Json, "application/json; charset=utf-8"));

// Diagnostico: lista crua de sensores, util para achar nomes no LHM.
app.MapGet("/api/raw", (SensorHub hub) =>
    Results.Text(hub.RawReport, "text/plain; charset=utf-8"));

// Diagnostico sob demanda, com duas passadas de Update.
app.MapGet("/api/debug", (SensorHub hub) =>
    Results.Text(hub.DebugDump(), "text/plain; charset=utf-8"));

app.MapGet("/api/health", (SensorHub hub) => Results.Json(new { ok = hub.Started, age = hub.AgeSeconds }));

// FPS e frametime do jogo em primeiro plano (PresentMon); {"ativo":false} quando nao ha jogo.
app.MapGet("/api/fps", (FrameHub fps) =>
    Results.Text(fps.ObterJson(), "application/json; charset=utf-8"));

app.Logger.LogInformation("painel servindo em {Url}", url);
app.Run();
