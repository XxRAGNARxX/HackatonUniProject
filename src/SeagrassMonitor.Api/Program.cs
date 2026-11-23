using SeagrassMonitor.Api.Models;
using SeagrassMonitor.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<SyntheticDataGenerator>();
builder.Services.AddSingleton<SuitabilityEngine>();
builder.Services.AddHostedService<LiveTelemetryWorker>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "Sozopol Bay Seagrass Suitability API",
    utc = DateTime.UtcNow
}));

app.MapGet("/api/map", (
    SuitabilityEngine engine,
    string? species,
    string? pathway,
    double? tempOffsetC,
    double? turbidityFactor,
    string? offline) =>
{
    var offlineList = string.IsNullOrWhiteSpace(offline)
        ? new List<string>()
        : offline.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    var req = new SimulationRequest(
        Species: species ?? "zostera_marina",
        Pathway: pathway ?? "assisted",
        TempOffsetC: tempOffsetC ?? 0.0,
        TurbidityFactor: turbidityFactor ?? 1.0,
        OfflineStationIds: offlineList
    );
    return Results.Ok(engine.ComputeMap(req));
});

app.MapPost("/api/map/simulate", (SuitabilityEngine engine, SimulationRequest req) =>
    Results.Ok(engine.ComputeMap(req)));

app.MapGet("/api/stations", (SyntheticDataGenerator data) =>
    Results.Ok(data.GetStations()));

app.MapGet("/api/stations/{stationId}/readings", (SyntheticDataGenerator data, string stationId) =>
{
    var readings = data.GetReadings(stationId.ToUpperInvariant());
    return readings.Count == 0
        ? Results.NotFound(new { error = $"Station '{stationId}' not found." })
        : Results.Ok(new
        {
            stationId = stationId.ToUpperInvariant(),
            intervalMinutes = 10,
            count = readings.Count,
            isSyntheticData = true,
            readings
        });
});

app.MapGet("/api/live/status", (SyntheticDataGenerator data) =>
    Results.Ok(data.GetLiveStatus()));

app.MapPost("/api/live/toggle", (SyntheticDataGenerator data, bool? enabled) =>
{
    var current = data.GetLiveStatus();
    bool next = enabled ?? !current.IsRunning;
    return Results.Ok(data.SetLiveRunning(next));
});

app.Run();
