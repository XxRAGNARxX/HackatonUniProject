using SeagrassMonitor.Api.Models;

namespace SeagrassMonitor.Api.Services;

/// <summary>
/// Implements the spatial downscaling & limiting-condition suitability model described
/// in Black_Sea_Seagrass_Technical_Proposal (1).pdf (p. 3):
///  - Fine seabed grid over Sozopol Bay water polygon
///  - IDW interpolation of water-column properties across ~600 m sensor nodes
///  - Bathymetry-informed bottom PAR downscaling (Beer-Lambert)
///  - 0-100 scoring per requirement -> provisional score = MIN(requirements)
///  - Explicit HOLD (unresolved disturbance) and INSUFFICIENT_DATA flags + spatial confidence
///  - Separate models for Zostera marina vs. Zostera noltei and Assisted vs. Natural recovery
/// </summary>
public class SuitabilityEngine
{
    private readonly SyntheticDataGenerator _data;

    // Max reliable interpolation radius from nearest active 600 m sensor node
    private const double MaxInterpolationRadiusM = 950.0;

    // Grid cell step (~85 m x 85 m at 42.43°N for crisp interactive rendering)
    private const double CellStepLat = 0.00076;
    private const double CellStepLon = 0.00102;

    public SuitabilityEngine(SyntheticDataGenerator data)
    {
        _data = data;
    }

    public MapResponse ComputeMap(SimulationRequest request)
    {
        var species = ParseSpecies(request.Species);
        var pathway = ParsePathway(request.Pathway);
        var offlineSet = new HashSet<string>(request.OfflineStationIds ?? [], StringComparer.OrdinalIgnoreCase);

        double tempOffset = Math.Clamp(request.TempOffsetC, -5.0, 8.0);
        double turbidityFactor = Math.Clamp(request.TurbidityFactor, 0.5, 2.5);

        // Apply scenario overrides to stations
        var allStations = _data.GetStations().Select(st =>
        {
            bool isOnline = !offlineSet.Contains(st.StationId);
            double adjKd = Math.Round(st.DailySummary.EstimatedKd * turbidityFactor, 3);
            double adjTemp = Math.Round(st.DailySummary.MeanTempC + tempOffset, 2);
            double adjMaxTemp = Math.Round(st.DailySummary.MaxTempC + tempOffset, 2);
            double adjDoLow = Math.Round(Math.Max(1.5, st.DailySummary.OvernightDoLowMgL - Math.Max(0, tempOffset) * 0.22), 2);
            double parScale = Math.Exp(-(adjKd - st.DailySummary.EstimatedKd) * Math.Max(0.5, st.SeabedDepthM - st.SensorHeightAboveSeabedM));
            double adjPar = Math.Round(st.DailySummary.MeanDaytimePar * parScale, 1);
            double adjDli = Math.Round(st.DailySummary.DailyLightIntegralMol * parScale, 2);

            return st with
            {
                IsOnline = isOnline,
                DailySummary = st.DailySummary with
                {
                    EstimatedKd = adjKd,
                    MeanTempC = adjTemp,
                    MaxTempC = adjMaxTemp,
                    OvernightDoLowMgL = adjDoLow,
                    MeanDaytimePar = adjPar,
                    DailyLightIntegralMol = adjDli
                }
            };
        }).ToList();

        var activeStations = allStations.Where(s => s.IsOnline).ToList();
        var disturbanceZones = _data.GetDisturbanceZones().ToList();
        var meadows = _data.GetReferenceMeadows().ToList();

        // Compute mean nearest-neighbor spacing across stations
        double meanSpacingM = ComputeMeanNearestNeighborM(allStations);

        // Build seabed grid across Sozopol Bay water polygon
        var cells = new List<SeabedGridCell>();
        int cellCounter = 1;

        for (double lat = 42.4198; lat <= 42.4426; lat += CellStepLat)
        {
            for (double lon = 27.6525; lon <= 27.6942; lon += CellStepLon)
            {
                if (!IsPointInPolygon(lat, lon, SyntheticDataGenerator.SozopolBayWaterPolygon))
                    continue;

                var cell = EvaluateGridCell(
                    $"CELL-{cellCounter++:D4}",
                    lat,
                    lon,
                    activeStations,
                    disturbanceZones,
                    meadows,
                    species,
                    pathway
                );
                cells.Add(cell);
            }
        }

        // Summary stats
        var scored = cells.Where(c => c.Status == "SCORED" && c.SuitabilityScore.HasValue).ToList();
        string topLimiting = scored.Count > 0
            ? scored.GroupBy(c => c.LimitingFactor).OrderByDescending(g => g.Count()).First().Key
            : "None";

        var stats = new MapSummaryStats(
            TotalCells: cells.Count,
            ScoredCells: scored.Count,
            HighSuitabilityCells: scored.Count(c => c.SuitabilityScore >= 70),
            ModerateSuitabilityCells: scored.Count(c => c.SuitabilityScore >= 40 && c.SuitabilityScore < 70),
            LowSuitabilityCells: scored.Count(c => c.SuitabilityScore < 40),
            HoldCells: cells.Count(c => c.Status == "HOLD"),
            InsufficientDataCells: cells.Count(c => c.Status == "INSUFFICIENT_DATA"),
            AverageScore: scored.Count > 0 ? Math.Round(scored.Average(c => c.SuitabilityScore!.Value), 1) : 0,
            TopLimitingFactor: topLimiting
        );

        var live = _data.GetLiveStatus();

        return new MapResponse(
            Region: "Sozopol Bay, Bulgarian Black Sea",
            Species: species == SeagrassSpecies.ZosteraMarina ? "Zostera marina (Eelgrass)" : "Zostera noltei (Dwarf Eelgrass)",
            Pathway: pathway == RecoveryPathway.AssistedRestoration ? "Assisted Restoration" : "Natural Recovery",
            IsSyntheticData: true,
            CalibrationNotice: "PROVISIONAL RELATIVE RANKING (0–100) using synthetic 600 m sensor data. Calibrated probabilities require field restoration outcomes (Technical Proposal, p. 3).",
            GeneratedAtUtc: DateTime.UtcNow,
            SimulatedUtc: live.SimulatedUtc,
            LiveTickCount: live.TickCount,
            IsLiveStreaming: live.IsRunning,
            ActiveStationCount: activeStations.Count,
            TotalStationCount: allStations.Count,
            MeanInterStationSpacingM: Math.Round(meanSpacingM, 0),
            Stats: stats,
            Stations: allStations,
            GridCells: cells,
            DisturbanceZones: disturbanceZones,
            ReferenceMeadows: meadows
        );
    }

    private SeabedGridCell EvaluateGridCell(
        string cellId,
        double lat,
        double lon,
        List<SensorStation> activeStations,
        List<DisturbanceZone> disturbanceZones,
        List<ReferenceMeadow> meadows,
        SeagrassSpecies species,
        RecoveryPathway pathway)
    {
        // 1. Static / surveyed seabed covariates (synthetic bathymetry, substrate, wave exposure)
        double depthM = EstimateSyntheticBathymetryM(lat, lon);
        string substrate = EstimateSyntheticSubstrate(lat, lon, depthM);
        double waveExposure = EstimateSyntheticWaveExposure(lat, lon);

        // 2. Check disturbance zones (HOLD flag per Technical Proposal p. 3)
        var holdZone = disturbanceZones.FirstOrDefault(z => IsPointInPolygon(lat, lon, z.PolygonLatLon));

        // 3. Find nearest active sensor & IDW interpolate water-column properties
        if (activeStations.Count == 0)
        {
            return new SeabedGridCell(
                cellId, Math.Round(lat, 6), Math.Round(lon, 6), CellStepLat / 2.0, CellStepLon / 2.0,
                depthM, substrate, waveExposure, double.PositiveInfinity, "NONE",
                0, 0, 0, 0, 0,
                null, "INSUFFICIENT_DATA", "No Active Sensors", 0, null,
                new ParameterScores(0, 0, 0, 0, 0, 0, 0, pathway == RecoveryPathway.NaturalRecovery ? 0 : null)
            );
        }

        double nearestDistM = double.MaxValue;
        string nearestId = "";
        double wSum = 0, kdSum = 0, tempSum = 0, doSum = 0, salSum = 0;

        foreach (var st in activeStations)
        {
            double distM = HaversineDistanceM(lat, lon, st.SeabedLat, st.SeabedLon);
            if (distM < nearestDistM)
            {
                nearestDistM = distM;
                nearestId = st.StationId;
            }

            // IDW power = 2 with small epsilon to avoid div-by-zero at sensor node
            double w = 1.0 / Math.Pow(Math.Max(distM, 15.0), 2.0);
            wSum += w;
            kdSum += w * st.DailySummary.EstimatedKd;
            tempSum += w * st.DailySummary.MeanTempC;
            doSum += w * st.DailySummary.OvernightDoLowMgL;
            salSum += w * st.DailySummary.MeanSalinityPsu;
        }

        double interpKd = kdSum / wSum;
        double interpTemp = Math.Round(tempSum / wSum, 2);
        double interpDoLow = Math.Round(doSum / wSum, 2);
        double interpSal = Math.Round(salSum / wSum, 2);

        // Physics-informed bottom PAR downscaling using cell bathymetry:
        // Mean daytime surface PAR ~780 µmol/m²/s, daily surface integral ~42 mol/m²/day
        double effDepth = Math.Max(0.4, depthM - 0.35);
        double interpPar = Math.Round(780.0 * Math.Exp(-interpKd * effDepth), 1);
        double interpDli = Math.Round(42.0 * Math.Exp(-interpKd * effDepth), 2);

        // Spatial confidence decays smoothly with distance from nearest 600 m sensor node
        double confidence = Math.Round(Math.Clamp((1.0 - nearestDistM / 1150.0) * 100.0, 0.0, 100.0), 0);

        // 4. Provisional 0–100 scoring bands per species
        // [PROVISIONAL / SYNTHETIC — not provided in PDFs; differentiated by depth/light/temp]
        double lightScore = ScoreLight(interpDli, species);
        double tempScore = ScoreTemperature(interpTemp, species);
        double doScore = ScoreDissolvedOxygen(interpDoLow);
        double salScore = ScoreSalinity(interpSal);
        double depthScore = ScoreDepth(depthM, species);
        double substrateScore = ScoreSubstrate(substrate);
        double waveScore = ScoreWaveExposure(waveExposure, species);
        double? seedScore = pathway == RecoveryPathway.NaturalRecovery
            ? ScoreSeedSupply(lat, lon, meadows)
            : null;

        var scores = new ParameterScores(
            Math.Round(lightScore, 1),
            Math.Round(tempScore, 1),
            Math.Round(doScore, 1),
            Math.Round(salScore, 1),
            Math.Round(depthScore, 1),
            Math.Round(substrateScore, 1),
            Math.Round(waveScore, 1),
            seedScore.HasValue ? Math.Round(seedScore.Value, 1) : null
        );

        // 5. Apply HOLD or INSUFFICIENT_DATA flags first
        if (holdZone != null)
        {
            return new SeabedGridCell(
                cellId, Math.Round(lat, 6), Math.Round(lon, 6), CellStepLat / 2.0, CellStepLon / 2.0,
                depthM, substrate, waveExposure, Math.Round(nearestDistM, 0), nearestId,
                interpPar, interpDli, interpDoLow, interpTemp, interpSal,
                null, "HOLD", $"Disturbance: {holdZone.Name}", confidence, holdZone.Type, scores
            );
        }

        if (nearestDistM > MaxInterpolationRadiusM)
        {
            return new SeabedGridCell(
                cellId, Math.Round(lat, 6), Math.Round(lon, 6), CellStepLat / 2.0, CellStepLon / 2.0,
                depthM, substrate, waveExposure, Math.Round(nearestDistM, 0), nearestId,
                interpPar, interpDli, interpDoLow, interpTemp, interpSal,
                null, "INSUFFICIENT_DATA", "Insufficient Sensor Coverage (>950 m)", confidence, null, scores
            );
        }

        // 6. Limiting-Condition Rule (Technical Proposal p. 3): Score = MIN(all requirements)
        var components = new List<(string Name, double Score)>
        {
            ("Bottom Light (PAR)", lightScore),
            ("Temperature", tempScore),
            ("Dissolved Oxygen", doScore),
            ("Salinity", salScore),
            ("Depth", depthScore),
            ("Substrate", substrateScore),
            ("Wave Exposure", waveScore)
        };
        if (seedScore.HasValue)
            components.Add(("Seed Supply / Dispersal", seedScore.Value));

        var limiting = components.OrderBy(c => c.Score).First();

        return new SeabedGridCell(
            cellId, Math.Round(lat, 6), Math.Round(lon, 6), CellStepLat / 2.0, CellStepLon / 2.0,
            depthM, substrate, waveExposure, Math.Round(nearestDistM, 0), nearestId,
            interpPar, interpDli, interpDoLow, interpTemp, interpSal,
            Math.Round(limiting.Score, 1), "SCORED", limiting.Name, confidence, null, scores
        );
    }

    // --- Provisional 0-100 Scoring Curves (Synthetic for Hackathon MVP) ---

    private static double ScoreLight(double dliMol, SeagrassSpecies species)
    {
        // Z. noltei typically needs higher light in shallower zones; Z. marina tolerates slightly lower light
        double optMin = species == SeagrassSpecies.ZosteraMarina ? 7.0 : 9.0;
        double zeroAt = species == SeagrassSpecies.ZosteraMarina ? 1.8 : 2.5;
        if (dliMol >= optMin) return 100.0;
        if (dliMol <= zeroAt) return 0.0;
        return (dliMol - zeroAt) / (optMin - zeroAt) * 100.0;
    }

    private static double ScoreTemperature(double tempC, SeagrassSpecies species)
    {
        // Z. noltei tolerates slightly warmer shallow waters than Z. marina
        double optHigh = species == SeagrassSpecies.ZosteraMarina ? 23.0 : 24.5;
        double lethalHigh = species == SeagrassSpecies.ZosteraMarina ? 28.5 : 30.0;
        if (tempC >= 12.0 && tempC <= optHigh) return 100.0;
        if (tempC > optHigh)
        {
            if (tempC >= lethalHigh) return 0.0;
            return (1.0 - (tempC - optHigh) / (lethalHigh - optHigh)) * 100.0;
        }
        return Math.Clamp((tempC - 4.0) / 8.0 * 100.0, 0.0, 100.0);
    }

    private static double ScoreDissolvedOxygen(double doLowMgL)
    {
        if (doLowMgL >= 6.5) return 100.0;
        if (doLowMgL <= 3.0) return 0.0;
        return (doLowMgL - 3.0) / 3.5 * 100.0;
    }

    private static double ScoreSalinity(double salPsu)
    {
        // Black Sea brackish coastal range (~15-19 PSU)
        if (salPsu >= 15.0 && salPsu <= 20.0) return 100.0;
        if (salPsu < 10.0 || salPsu > 28.0) return 20.0;
        return 75.0;
    }

    private static double ScoreDepth(double depthM, SeagrassSpecies species)
    {
        if (species == SeagrassSpecies.ZosteraMarina)
        {
            // Optimal ~1.5 m - 6.0 m, tapers down to 10.5 m
            if (depthM >= 1.5 && depthM <= 6.0) return 100.0;
            if (depthM < 1.5) return Math.Clamp(depthM / 1.5 * 100.0, 20.0, 100.0);
            if (depthM >= 10.5) return 0.0;
            return (1.0 - (depthM - 6.0) / 4.5) * 100.0;
        }
        else
        {
            // Zostera noltei (dwarf eelgrass): shallower infralittoral, optimal ~0.5 m - 3.5 m, tapers to 6.0 m
            if (depthM >= 0.5 && depthM <= 3.5) return 100.0;
            if (depthM >= 6.0) return 0.0;
            return (1.0 - (depthM - 3.5) / 2.5) * 100.0;
        }
    }

    private static double ScoreSubstrate(string substrate) => substrate switch
    {
        "Sand" => 96.0,
        "MuddySand" => 84.0,
        "Silt" => 42.0,
        "Rock" => 10.0,
        _ => 50.0
    };

    private static double ScoreWaveExposure(double exposure, SeagrassSpecies species)
    {
        double maxOpt = species == SeagrassSpecies.ZosteraMarina ? 0.45 : 0.35;
        if (exposure <= maxOpt) return 100.0;
        if (exposure >= 0.92) return 10.0;
        return 100.0 - (exposure - maxOpt) / (0.92 - maxOpt) * 90.0;
    }

    private static double ScoreSeedSupply(double lat, double lon, List<ReferenceMeadow> meadows)
    {
        double minDistM = meadows.Min(m => Math.Max(0, HaversineDistanceM(lat, lon, m.CenterLat, m.CenterLon) - m.RadiusM));
        if (minDistM <= 150.0) return 100.0;
        if (minDistM >= 1400.0) return 12.0;
        return 100.0 - (minDistM - 150.0) / 1250.0 * 88.0;
    }

    // --- Synthetic Coastal Covariates for Sozopol Bay ---

    private static double EstimateSyntheticBathymetryM(double lat, double lon)
    {
        // Shallow near west/south coast & St. Ivan SW shelf; deeper toward open NE bay entrance
        double distWestShore = Math.Max(0, (lon - 27.6525) * 82000.0);
        double distSouthShore = Math.Max(0, (lat - 42.4198) * 111000.0);
        double distToShore = Math.Min(distWestShore, distSouthShore);

        // St. Ivan Island shelf shallowing
        double distStIvan = HaversineDistanceM(lat, lon, 42.4385, 27.6905);
        double stIvanShallow = distStIvan < 650 ? (1.0 - distStIvan / 650.0) * 5.2 : 0.0;

        // Old Town headland shallowing
        double distOldTown = HaversineDistanceM(lat, lon, 42.4265, 27.6945);
        double oldTownShallow = distOldTown < 550 ? (1.0 - distOldTown / 550.0) * 3.5 : 0.0;

        double rawDepth = 1.6 + Math.Pow(distToShore / 1600.0, 0.85) * 9.8 - stIvanShallow - oldTownShallow;
        return Math.Round(Math.Clamp(rawDepth, 1.4, 13.5), 1);
    }

    private static string EstimateSyntheticSubstrate(double lat, double lon, double depthM)
    {
        // Harbor / marina silt
        if (lon > 27.677 && lat < 42.4265) return "Silt";
        // Rocky fringe near exposed St. Ivan / Old Town channel
        if (lon > 27.691 && lat > 42.430 && lat < 42.435) return "Rock";
        if (depthM <= 4.8) return "Sand";
        if (depthM <= 8.5) return "MuddySand";
        return "Silt";
    }

    private static double EstimateSyntheticWaveExposure(double lat, double lon)
    {
        // Sheltered in Gradina / Zlatna Ribka & St. Ivan SW; exposed toward open NE Black Sea entrance
        double northFactor = Math.Clamp((lat - 42.421) / 0.022, 0.0, 1.0);
        double eastFactor = Math.Clamp((lon - 27.654) / 0.038, 0.0, 1.0);
        double exposure = 0.15 + 0.65 * northFactor * (0.45 + 0.55 * eastFactor);

        // Shelter directly behind St. Ivan Island SW
        if (HaversineDistanceM(lat, lon, 42.4368, 27.6880) < 420)
            exposure *= 0.45;

        return Math.Round(Math.Clamp(exposure, 0.1, 0.95), 2);
    }

    private static SeagrassSpecies ParseSpecies(string s) =>
        s.Equals("zostera_noltei", StringComparison.OrdinalIgnoreCase)
            ? SeagrassSpecies.ZosteraNoltei
            : SeagrassSpecies.ZosteraMarina;

    private static RecoveryPathway ParsePathway(string p) =>
        p.Equals("natural", StringComparison.OrdinalIgnoreCase)
            ? RecoveryPathway.NaturalRecovery
            : RecoveryPathway.AssistedRestoration;

    private static double ComputeMeanNearestNeighborM(List<SensorStation> stations)
    {
        if (stations.Count < 2) return 0;
        double total = 0;
        foreach (var a in stations)
        {
            double minD = stations
                .Where(b => b.StationId != a.StationId)
                .Min(b => HaversineDistanceM(a.SeabedLat, a.SeabedLon, b.SeabedLat, b.SeabedLon));
            total += minD;
        }
        return total / stations.Count;
    }

    public static double HaversineDistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static bool IsPointInPolygon(double lat, double lon, double[][] poly)
    {
        bool inside = false;
        int n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double yi = poly[i][0], xi = poly[i][1];
            double yj = poly[j][0], xj = poly[j][1];
            bool intersect = ((yi > lat) != (yj > lat)) &&
                             (lon < (xj - xi) * (lat - yi) / (yj - yi + 1e-12) + xi);
            if (intersect) inside = !inside;
        }
        return inside;
    }
}
