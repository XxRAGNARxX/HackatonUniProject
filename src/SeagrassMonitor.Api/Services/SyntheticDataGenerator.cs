using SeagrassMonitor.Api.Models;

namespace SeagrassMonitor.Api.Services;

/// <summary>
/// Generates and continuously updates realistic, clearly tagged SYNTHETIC / PROVISIONAL
/// 10-minute telemetry for a ~600 m coastal LoRa buoy network in Sozopol Bay, Bulgaria.
/// Grounded in sensor specs from the project PDFs (Aqua TROLL 600 + LI-COR LI-192 PAR).
/// </summary>
public class SyntheticDataGenerator
{
    private record StationSpec(
        string Id,
        string Name,
        string ZoneType,
        double Lat,
        double Lon,
        double DepthM,
        string Substrate,
        int Hops,
        string? RelayId,
        double BaseKd,
        double BaseTemp,
        double BaseDo,
        double BaseSal,
        string BaseQuality
    );

    private readonly object _sync = new();
    private readonly Random _rng = new(42026);
    private readonly List<StationSpec> _specs = new();
    private readonly Dictionary<string, List<SensorReading>> _readingsByStation = new();
    private readonly Dictionary<string, SensorStation> _stationsById = new();
    private readonly List<DisturbanceZone> _disturbanceZones = new();
    private readonly List<ReferenceMeadow> _referenceMeadows = new();

    private DateTime _simulatedUtc;
    private DateTime _lastUpdatedUtc;
    private long _tickCount;
    private bool _isLiveRunning = true;

    public const int LiveTickIntervalSeconds = 3;

    // Clean water polygon for Sozopol Bay (WGS 84 lat, lon)
    public static readonly double[][] SozopolBayWaterPolygon =
    [
        [42.4395, 27.6525], // Gradina North
        [42.4320, 27.6535], // Gradina Mid
        [42.4255, 27.6560], // Zlatna Ribka West
        [42.4208, 27.6620], // Zlatna Ribka South
        [42.4198, 27.6715], // Sozopol West Bay
        [42.4208, 27.6815], // Sozopol Marina approach
        [42.4238, 27.6890], // Harbor mouth
        [42.4275, 27.6925], // Old Town NW cove
        [42.4335, 27.6940], // St. Ivan Channel
        [42.4368, 27.6895], // St. Ivan Island SW shelf
        [42.4418, 27.6855], // NE bay entrance
        [42.4428, 27.6680], // North bay entrance
        [42.4410, 27.6565]  // Chernomorets south approach
    ];

    public SyntheticDataGenerator()
    {
        _simulatedUtc = DateTime.UtcNow.Date.AddHours(12);
        _lastUpdatedUtc = DateTime.UtcNow;
        InitializeReferenceMeadows();
        InitializeDisturbanceZones();
        InitializeStationsAndTelemetry();
    }

    public IReadOnlyList<SensorStation> GetStations()
    {
        lock (_sync)
        {
            return _specs.Select(s => _stationsById[s.Id]).ToList();
        }
    }

    public IReadOnlyList<DisturbanceZone> GetDisturbanceZones() => _disturbanceZones;
    public IReadOnlyList<ReferenceMeadow> GetReferenceMeadows() => _referenceMeadows;

    public IReadOnlyList<SensorReading> GetReadings(string stationId)
    {
        lock (_sync)
        {
            return _readingsByStation.TryGetValue(stationId, out var list) ? list.ToList() : [];
        }
    }

    public LiveStatusResponse GetLiveStatus()
    {
        lock (_sync)
        {
            return new LiveStatusResponse(
                IsRunning: _isLiveRunning,
                TickCount: _tickCount,
                SimulatedUtc: _simulatedUtc,
                LastUpdatedUtc: _lastUpdatedUtc,
                IntervalSeconds: LiveTickIntervalSeconds
            );
        }
    }

    public LiveStatusResponse SetLiveRunning(bool enabled)
    {
        lock (_sync)
        {
            _isLiveRunning = enabled;
            return new LiveStatusResponse(
                IsRunning: _isLiveRunning,
                TickCount: _tickCount,
                SimulatedUtc: _simulatedUtc,
                LastUpdatedUtc: _lastUpdatedUtc,
                IntervalSeconds: LiveTickIntervalSeconds
            );
        }
    }

    /// <summary>
    /// Advances the simulated coastal sensor network by one 10-minute LoRa telemetry cycle,
    /// appending a new reading per buoy, rolling the 24-hour window, drifting buoy GPS within
    /// mooring swing radius, and recomputing station aggregates + live turbidity/temp waves.
    /// </summary>
    public void AdvanceLiveTick()
    {
        lock (_sync)
        {
            if (!_isLiveRunning) return;

            _tickCount++;
            _simulatedUtc = _simulatedUtc.AddMinutes(10);
            _lastUpdatedUtc = DateTime.UtcNow;

            // Slow coastal environmental waves (passing turbidity plume + thermal breeze)
            double phase = _tickCount * 0.22;

            for (int idx = 0; idx < _specs.Count; idx++)
            {
                var spec = _specs[idx];
                var readings = _readingsByStation[spec.Id];
                long nextSeq = readings.Count > 0 ? readings[^1].Sequence + 1 : 10000;

                // Regional drift that varies across the bay so grid cells visibly shift over time
                double kdWave = 1.0 + 0.22 * Math.Sin(phase + idx * 0.55);
                double tempWave = 0.85 * Math.Sin(phase * 0.6 + idx * 0.35);
                double doWave = 0.45 * Math.Cos(phase * 0.7 + idx * 0.4);

                double liveKd = Math.Round(Math.Clamp(spec.BaseKd * kdWave, 0.18, 1.35), 3);
                var reading = GenerateSingleReading(spec, _simulatedUtc, nextSeq, liveKd, tempWave, doWave);

                readings.Add(reading);
                if (readings.Count > 144)
                    readings.RemoveAt(0);

                // Subtle buoy GPS drift within ~12 m mooring swing radius
                double swingLat = Math.Sin(phase * 1.1 + idx) * 0.00008 + (_rng.NextDouble() - 0.5) * 0.00002;
                double swingLon = Math.Cos(phase * 0.9 + idx) * 0.00010 + (_rng.NextDouble() - 0.5) * 0.00002;

                _stationsById[spec.Id] = BuildStationRecord(spec, readings, liveKd, swingLat, swingLon);
            }
        }
    }

    private void InitializeReferenceMeadows()
    {
        _referenceMeadows.AddRange(
        [
            new ReferenceMeadow(
                Id: "MDW-GRADINA",
                Name: "Gradina / Zlatna Ribka Meadow (Synthetic Ref)",
                Species: "Zostera marina & Zostera noltei",
                CenterLat: 42.4295,
                CenterLon: 27.6565,
                RadiusM: 420,
                PolygonLatLon:
                [
                    [42.4332, 27.6540],
                    [42.4330, 27.6585],
                    [42.4260, 27.6602],
                    [42.4254, 27.6565],
                    [42.4290, 27.6545]
                ]
            ),
            new ReferenceMeadow(
                Id: "MDW-STIVAN",
                Name: "St. Ivan Island SW Sheltered Patch (Synthetic Ref)",
                Species: "Zostera marina",
                CenterLat: 42.4368,
                CenterLon: 27.6880,
                RadiusM: 260,
                PolygonLatLon:
                [
                    [42.4384, 27.6862],
                    [42.4382, 27.6898],
                    [42.4352, 27.6902],
                    [42.4354, 27.6865]
                ]
            )
        ]);
    }

    private void InitializeDisturbanceZones()
    {
        _disturbanceZones.AddRange(
        [
            new DisturbanceZone(
                Id: "DIST-HARBOR",
                Name: "Sozopol Marina & Harbor Channel",
                Type: "Anchoring & Vessel Traffic (HOLD)",
                PolygonLatLon:
                [
                    [42.4208, 27.6795],
                    [42.4258, 27.6820],
                    [42.4265, 27.6895],
                    [42.4235, 27.6892],
                    [42.4206, 27.6825]
                ]
            ),
            new DisturbanceZone(
                Id: "DIST-OUTFALL",
                Name: "West Bay Stormwater / Dredge Zone",
                Type: "Unresolved Disturbance (HOLD)",
                PolygonLatLon:
                [
                    [42.4200, 27.6695],
                    [42.4222, 27.6695],
                    [42.4222, 27.6730],
                    [42.4200, 27.6730]
                ]
            )
        ]);
    }

    private void InitializeStationsAndTelemetry()
    {
        _specs.AddRange(
        [
            new("SOZ-01", "Gradina North (Ref Meadow)",    "ReferenceMeadow", 42.4365, 27.6558, 2.6, "Sand",      2, "SOZ-02", 0.28, 21.8, 7.6, 17.2, "OK"),
            new("SOZ-02", "Gradina Mid (Ref Meadow)",      "ReferenceMeadow", 42.4312, 27.6562, 3.1, "Sand",      1, null,     0.30, 22.0, 7.5, 17.2, "OK"),
            new("SOZ-03", "Zlatna Ribka (Meadow Edge)",    "ReferenceMeadow", 42.4260, 27.6584, 3.6, "Sand",      1, null,     0.34, 22.3, 7.3, 17.1, "OK"),
            new("SOZ-04", "Zlatna Ribka East (Bare Sand)", "CandidateBare",   42.4222, 27.6638, 4.1, "Sand",      1, null,     0.38, 22.6, 7.1, 17.0, "OK"),
            new("SOZ-05", "Sozopol West Bay (Candidate)",  "CandidateBare",   42.4212, 27.6710, 4.6, "MuddySand", 1, null,     0.44, 23.0, 6.7, 16.9, "OK"),
            new("SOZ-06", "Marina Approach (Turbid)",      "HarborDisturbed", 42.4220, 27.6782, 5.4, "Silt",      1, null,     0.72, 23.8, 5.4, 16.6, "FOULING_WARN"),
            new("SOZ-07", "Harbor Channel (Disturbed)",    "HarborDisturbed", 42.4244, 27.6848, 6.4, "Silt",      1, null,     0.85, 24.1, 4.9, 16.5, "FOULING_WARN"),
            new("SOZ-08", "Old Town NW Cove (Candidate)",  "CandidateBare",   42.4282, 27.6898, 4.3, "Sand",      1, null,     0.39, 22.4, 7.0, 17.3, "OK"),
            new("SOZ-09", "St. Ivan Channel (Exposed)",    "ExposedTurbid",   42.4332, 27.6915, 7.8, "MuddySand", 2, "SOZ-08", 0.42, 21.6, 7.2, 17.5, "OK"),
            new("SOZ-10", "St. Ivan SW Shelf (Clear Ref)", "ReferenceMeadow", 42.4375, 27.6882, 3.4, "Sand",      2, "SOZ-08", 0.27, 21.5, 7.7, 17.6, "OK")
        ]);

        var startUtc = _simulatedUtc.AddHours(-24);

        foreach (var spec in _specs)
        {
            var readings = new List<SensorReading>(144);
            for (int i = 0; i < 144; i++)
            {
                var t = startUtc.AddMinutes(i * 10);
                readings.Add(GenerateSingleReading(spec, t, 10000 + i, spec.BaseKd, 0.0, 0.0));
            }

            _readingsByStation[spec.Id] = readings;
            double offsetLat = (_rng.NextDouble() - 0.5) * 0.00016;
            double offsetLon = (_rng.NextDouble() - 0.5) * 0.00020;
            _stationsById[spec.Id] = BuildStationRecord(spec, readings, spec.BaseKd, offsetLat, offsetLon);
        }
    }

    private SensorReading GenerateSingleReading(
        StationSpec spec,
        DateTime utcTime,
        long seq,
        double liveKd,
        double tempWave,
        double doWave)
    {
        const double surfaceParPeak = 1400.0;
        double sensorDepth = Math.Max(0.5, spec.DepthM - 0.35);
        double hourOfDay = utcTime.Hour + utcTime.Minute / 60.0;

        double solarPhase = Math.Sin((hourOfDay - 3.5) / 14.5 * Math.PI);
        double surfacePar = solarPhase > 0 ? surfaceParPeak * Math.Pow(solarPhase, 1.25) : 0.0;
        double noisePar = 1.0 + (_rng.NextDouble() - 0.5) * 0.08;
        double bottomPar = Math.Round(surfacePar * Math.Exp(-liveKd * sensorDepth) * noisePar, 1);

        double doCycle = Math.Sin((hourOfDay - 8.5) / 24.0 * 2.0 * Math.PI) * 0.85;
        double doMgL = Math.Round(Math.Max(2.2, spec.BaseDo + doCycle + doWave + (_rng.NextDouble() - 0.5) * 0.16), 2);
        double doPct = Math.Round(doMgL / 8.1 * 100.0, 1);

        double tempCycle = Math.Sin((hourOfDay - 9.0) / 24.0 * 2.0 * Math.PI) * 0.75;
        double tempC = Math.Round(spec.BaseTemp + tempCycle + tempWave + (_rng.NextDouble() - 0.5) * 0.14, 2);

        double salPsu = Math.Round(spec.BaseSal + (_rng.NextDouble() - 0.5) * 0.18, 2);
        double depthReading = Math.Round(sensorDepth + Math.Sin(seq * 0.25) * 0.08 + (_rng.NextDouble() - 0.5) * 0.03, 2);
        double batteryV = Math.Round(12.6 + (solarPhase > 0 ? 0.65 * solarPhase : -0.15), 2);

        return new SensorReading(
            UtcTime: utcTime,
            StationId: spec.Id,
            Sequence: seq,
            ParUmol: bottomPar,
            DoMgL: doMgL,
            DoPctSat: doPct,
            TempC: tempC,
            SalinityPsu: salPsu,
            SensorDepthM: depthReading,
            BatteryV: batteryV,
            QualityFlag: spec.BaseQuality
        );
    }

    private static SensorStation BuildStationRecord(
        StationSpec spec,
        List<SensorReading> readings,
        double liveKd,
        double buoyOffsetLat,
        double buoyOffsetLon)
    {
        var daytimePars = readings.Where(r => r.ParUmol > 5.0).Select(r => r.ParUmol).ToList();
        double meanDaytimePar = daytimePars.Count > 0 ? Math.Round(daytimePars.Average(), 1) : 0;
        double dli = Math.Round(readings.Sum(r => r.ParUmol * 600.0) / 1_000_000.0, 2);
        double overnightDoLow = Math.Round(readings.Min(r => r.DoMgL), 2);
        double meanTemp = Math.Round(readings.Average(r => r.TempC), 2);
        double maxTemp = Math.Round(readings.Max(r => r.TempC), 2);
        double heatHours = Math.Round(readings.Count(r => r.TempC >= 25.0) * (10.0 / 60.0), 1);
        double meanSal = Math.Round(readings.Average(r => r.SalinityPsu), 2);

        return new SensorStation(
            StationId: spec.Id,
            Name: spec.Name,
            ZoneType: spec.ZoneType,
            SeabedLat: spec.Lat,
            SeabedLon: spec.Lon,
            BuoyGpsLat: Math.Round(spec.Lat + buoyOffsetLat, 6),
            BuoyGpsLon: Math.Round(spec.Lon + buoyOffsetLon, 6),
            BuoySwingRadiusM: 12.0,
            SeabedDepthM: spec.DepthM,
            SensorHeightAboveSeabedM: 0.35,
            Substrate: spec.Substrate,
            LoraHopCount: spec.Hops,
            RelayStationId: spec.RelayId,
            BatteryV: readings[^1].BatteryV,
            QualityFlag: spec.BaseQuality,
            IsOnline: true,
            DailySummary: new StationDailySummary(
                StationId: spec.Id,
                MeanDaytimePar: meanDaytimePar,
                DailyLightIntegralMol: dli,
                OvernightDoLowMgL: overnightDoLow,
                MeanTempC: meanTemp,
                MaxTempC: maxTemp,
                HeatExposureHours: heatHours,
                MeanSalinityPsu: meanSal,
                EstimatedKd: liveKd
            )
        );
    }
}
