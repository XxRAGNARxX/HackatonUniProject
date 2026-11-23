namespace SeagrassMonitor.Api.Services;

/// <summary>
/// Background worker that periodically advances the synthetic Sozopol Bay LoRa buoy network
/// to simulate continuous real-time coastal monitoring.
/// </summary>
public class LiveTelemetryWorker : BackgroundService
{
    private readonly SyntheticDataGenerator _data;
    private readonly ILogger<LiveTelemetryWorker> _logger;

    public LiveTelemetryWorker(SyntheticDataGenerator data, ILogger<LiveTelemetryWorker> logger)
    {
        _data = data;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "LiveTelemetryWorker started (tick interval: {Seconds}s = +10 simulated min/tick).",
            SyntheticDataGenerator.LiveTickIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SyntheticDataGenerator.LiveTickIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            _data.AdvanceLiveTick();
        }
    }
}
