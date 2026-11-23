namespace SeagrassMonitor.Api.Models;

public enum SeagrassSpecies
{
    ZosteraMarina,
    ZosteraNoltei
}

public enum RecoveryPathway
{
    AssistedRestoration,
    NaturalRecovery
}

public record SensorReading(
    DateTime UtcTime,
    string StationId,
    long Sequence,
    double ParUmol,        // µmol photons/m²/s (10-min mean)
    double DoMgL,          // mg/L
    double DoPctSat,       // % saturation
    double TempC,          // °C
    double SalinityPsu,    // PSU (provisional synthetic)
    double SensorDepthM,   // m
    double BatteryV,       // V
    string QualityFlag     // OK, FOULING_WARN, DEPTH_DRIFT
);

public record StationDailySummary(
    string StationId,
    double MeanDaytimePar,
    double DailyLightIntegralMol, // mol photons/m²/day
    double OvernightDoLowMgL,
    double MeanTempC,
    double MaxTempC,
    double HeatExposureHours,
    double MeanSalinityPsu,
    double EstimatedKd            // vertical light attenuation coefficient (m^-1)
);

public record SensorStation(
    string StationId,
    string Name,
    string ZoneType,              // ReferenceMeadow, CandidateBare, ExposedTurbid, HarborDisturbed
    double SeabedLat,
    double SeabedLon,
    double BuoyGpsLat,
    double BuoyGpsLon,
    double BuoySwingRadiusM,
    double SeabedDepthM,
    double SensorHeightAboveSeabedM,
    string Substrate,             // Sand, MuddySand, Silt, Rock
    int LoraHopCount,             // 1 = direct to shore, 2 = via relay buoy
    string? RelayStationId,
    double BatteryV,
    string QualityFlag,
    bool IsOnline,
    StationDailySummary DailySummary
);

public record DisturbanceZone(
    string Id,
    string Name,
    string Type,                  // Anchoring, HarborChannel, Outfall
    double[][] PolygonLatLon
);

public record ReferenceMeadow(
    string Id,
    string Name,
    string Species,
    double CenterLat,
    double CenterLon,
    double RadiusM,
    double[][] PolygonLatLon
);

public record ParameterScores(
    double Light,
    double Temperature,
    double DissolvedOxygen,
    double Salinity,
    double Depth,
    double Substrate,
    double WaveExposure,
    double? SeedSupply
);

public record SeabedGridCell(
    string CellId,
    double CenterLat,
    double CenterLon,
    double HalfSizeLat,
    double HalfSizeLon,
    double BathymetryDepthM,
    string Substrate,
    double WaveExposureIndex,     // 0 (sheltered) to 1 (exposed)
    double DistanceToNearestSensorM,
    string NearestSensorId,
    double InterpolatedParUmol,
    double InterpolatedDailyLightMol,
    double InterpolatedDoLowMgL,
    double InterpolatedTempC,
    double InterpolatedSalinityPsu,
    double? SuitabilityScore,     // 0-100 relative ranking (null if HOLD or INSUFFICIENT_DATA)
    string Status,                // SCORED, HOLD, INSUFFICIENT_DATA
    string LimitingFactor,
    double ConfidencePct,         // 0-100
    string? HoldReason,
    ParameterScores ComponentScores
);

public record SimulationRequest(
    string Species = "zostera_marina",
    string Pathway = "assisted",
    double TempOffsetC = 0.0,
    double TurbidityFactor = 1.0,
    List<string>? OfflineStationIds = null
);

public record MapResponse(
    string Region,
    string Species,
    string Pathway,
    bool IsSyntheticData,
    string CalibrationNotice,
    DateTime GeneratedAtUtc,
    DateTime SimulatedUtc,
    long LiveTickCount,
    bool IsLiveStreaming,
    int ActiveStationCount,
    int TotalStationCount,
    double MeanInterStationSpacingM,
    MapSummaryStats Stats,
    List<SensorStation> Stations,
    List<SeabedGridCell> GridCells,
    List<DisturbanceZone> DisturbanceZones,
    List<ReferenceMeadow> ReferenceMeadows
);

public record LiveStatusResponse(
    bool IsRunning,
    long TickCount,
    DateTime SimulatedUtc,
    DateTime LastUpdatedUtc,
    int IntervalSeconds
);

public record MapSummaryStats(
    int TotalCells,
    int ScoredCells,
    int HighSuitabilityCells,     // >= 70
    int ModerateSuitabilityCells, // 40 - 69
    int LowSuitabilityCells,      // < 40
    int HoldCells,
    int InsufficientDataCells,
    double AverageScore,
    string TopLimitingFactor
);
