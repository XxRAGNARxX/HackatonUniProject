"""
Sozopol Bay Seagrass Suitability — Reference Python Spatial Modeling Pipeline
------------------------------------------------------------------------------
Upgrades the C# MVP's IDW interpolation to Ordinary Kriging (with Kriging variance
as spatial uncertainty/confidence) and exports GeoJSON for the ASP.NET Core API.

Usage (requires Python 3.10+ and `pip install -r modeling/requirements.txt`):
    python modeling/kriging_pipeline.py --api http://localhost:5000 --out modeling/sozopol_kriged.geojson
"""

import argparse
import json
import math
import urllib.request
import numpy as np

try:
    from pykrige.ok import OrdinaryKriging
except ImportError:
    OrdinaryKriging = None


def fetch_stations(api_base: str) -> list[dict]:
    with urllib.request.urlopen(f"{api_base.rstrip('/')}/api/stations") as resp:
        return json.loads(resp.read().decode("utf-8"))


def run_kriging(stations: list[dict], out_path: str) -> None:
    if OrdinaryKriging is None:
        raise RuntimeError("PyKrige is not installed. Run: pip install -r modeling/requirements.txt")

    lons = np.array([s["seabedLon"] for s in stations], dtype=float)
    lats = np.array([s["seabedLat"] for s in stations], dtype=float)
    kds = np.array([s["dailySummary"]["estimatedKd"] for s in stations], dtype=float)
    temps = np.array([s["dailySummary"]["meanTempC"] for s in stations], dtype=float)
    dos = np.array([s["dailySummary"]["overnightDoLowMgL"] for s in stations], dtype=float)

    grid_lon = np.linspace(27.6525, 27.6942, 45)
    grid_lat = np.linspace(42.4198, 42.4426, 32)

    ok_kd = OrdinaryKriging(lons, lats, kds, variogram_model="spherical", verbose=False)
    kd_z, kd_ss = ok_kd.execute("grid", grid_lon, grid_lat)

    ok_temp = OrdinaryKriging(lons, lats, temps, variogram_model="spherical", verbose=False)
    temp_z, _ = ok_temp.execute("grid", grid_lon, grid_lat)

    ok_do = OrdinaryKriging(lons, lats, dos, variogram_model="spherical", verbose=False)
    do_z, _ = ok_do.execute("grid", grid_lon, grid_lat)

    features = []
    for i, lat in enumerate(grid_lat):
        for j, lon in enumerate(grid_lon):
            features.append({
                "type": "Feature",
                "geometry": {"type": "Point", "coordinates": [round(float(lon), 6), round(float(lat), 6)]},
                "properties": {
                    "kd_kriged": round(float(kd_z[i, j]), 3),
                    "kd_variance": round(float(kd_ss[i, j]), 4),
                    "temp_c_kriged": round(float(temp_z[i, j]), 2),
                    "do_low_kriged": round(float(do_z[i, j]), 2),
                },
            })

    geojson = {"type": "FeatureCollection", "features": features}
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(geojson, f, indent=2)
    print(f"Wrote {len(features)} kriged points to {out_path}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--api", default="http://localhost:5000")
    parser.add_argument("--out", default="modeling/sozopol_kriged.geojson")
    args = parser.parse_args()
    st = fetch_stations(args.api)
    run_kriging(st, args.out)
