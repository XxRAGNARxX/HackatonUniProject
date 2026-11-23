// Sozopol Bay Seagrass Suitability Interactive Web Map
const state = {
  species: 'zostera_marina',
  pathway: 'assisted',
  layer: 'suitability',
  tempOffsetC: 0.0,
  turbidityFactor: 1.0,
  offlineStationIds: new Set(),
  lastData: null,
  selectedCellId: null,
  selectedStationId: null,
  chartInstance: null,
  chartStationId: null,
  isLiveStreaming: true,
  isFetching: false,
  pollTimerId: null
};

// Initialize Leaflet map centered on Sozopol Bay, Bulgaria
const map = L.map('map', {
  center: [42.4305, 27.6735],
  zoom: 14,
  zoomControl: true
});

const darkBasemap = L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
  attribution: '&copy; OpenStreetMap &copy; CARTO',
  maxZoom: 19
}).addTo(map);

const satelliteBasemap = L.tileLayer(
  'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
  {
    attribution: 'Tiles &copy; Esri',
    maxZoom: 19
  }
);

const gridLayerGroup = L.layerGroup().addTo(map);
const meadowLayerGroup = L.layerGroup().addTo(map);
const disturbanceLayerGroup = L.layerGroup().addTo(map);
const sensorLayerGroup = L.layerGroup().addTo(map);

// --- Color Scales ---
function getSuitabilityColor(cell) {
  if (cell.status === 'HOLD') return '#ef4444';
  if (cell.status === 'INSUFFICIENT_DATA') return '#64748b';
  const s = cell.suitabilityScore ?? 0;
  if (s >= 80) return '#10b981';
  if (s >= 65) return '#34d399';
  if (s >= 50) return '#eab308';
  if (s >= 30) return '#f97316';
  return '#dc2626';
}

function getLayerColor(cell, layer) {
  if (layer === 'suitability') return getSuitabilityColor(cell);
  if (layer === 'par') {
    const v = cell.interpolatedDailyLightMol;
    if (v >= 14) return '#fef08a';
    if (v >= 9) return '#facc15';
    if (v >= 5) return '#ca8a04';
    if (v >= 2.5) return '#854d0e';
    return '#3b2407';
  }
  if (layer === 'do') {
    const v = cell.interpolatedDoLowMgL;
    if (v >= 7.0) return '#06b6d4';
    if (v >= 6.0) return '#22d3ee';
    if (v >= 5.0) return '#f59e0b';
    return '#ef4444';
  }
  if (layer === 'temp') {
    const v = cell.interpolatedTempC;
    if (v >= 26) return '#dc2626';
    if (v >= 24) return '#f97316';
    if (v >= 22.5) return '#eab308';
    return '#38bdf8';
  }
  if (layer === 'depth') {
    const d = cell.bathymetryDepthM;
    if (d <= 3.0) return '#67e8f9';
    if (d <= 5.5) return '#0ea5e9';
    if (d <= 8.5) return '#1d4ed8';
    return '#1e1b4b';
  }
  if (layer === 'confidence') {
    const c = cell.confidencePct;
    if (c >= 75) return '#10b981';
    if (c >= 45) return '#eab308';
    if (c >= 20) return '#f97316';
    return '#64748b';
  }
  return '#38bdf8';
}

function scoreBarColor(val) {
  if (val >= 75) return '#10b981';
  if (val >= 50) return '#eab308';
  if (val >= 30) return '#f97316';
  return '#ef4444';
}

// --- API Fetch & Render ---
async function fetchAndRender(isBackgroundPoll = false) {
  if (state.isFetching) return;
  state.isFetching = true;

  try {
    const payload = {
      species: state.species,
      pathway: state.pathway,
      tempOffsetC: state.tempOffsetC,
      turbidityFactor: state.turbidityFactor,
      offlineStationIds: Array.from(state.offlineStationIds)
    };

    const res = await fetch('/api/map/simulate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    if (!res.ok) {
      console.error('Failed to load map data:', res.status);
      return;
    }

    const data = await res.json();
    state.lastData = data;
    state.isLiveStreaming = data.isLiveStreaming;
    renderKPIs(data);
    renderMap(data);
    renderLegend();

    // Refresh open inspector smoothly without losing user focus
    if (state.selectedCellId) {
      const cell = data.gridCells.find(c => c.cellId === state.selectedCellId);
      if (cell) showCellInspector(cell);
    } else if (state.selectedStationId) {
      const st = data.stations.find(s => s.stationId === state.selectedStationId);
      if (st) await showStationInspector(st, isBackgroundPoll);
    }
  } finally {
    state.isFetching = false;
  }
}

function renderKPIs(data) {
  document.getElementById('kpiBuoys').textContent = `${data.activeStationCount}/${data.totalStationCount}`;
  document.getElementById('kpiSpacing').textContent = `${data.meanInterStationSpacingM} m`;
  document.getElementById('kpiHigh').textContent = `${data.stats.highSuitabilityCells} / ${data.stats.totalCells}`;
  document.getElementById('kpiHold').textContent = `${data.stats.holdCells} / ${data.stats.insufficientDataCells}`;
  document.getElementById('kpiLimiting').textContent = data.stats.topLimitingFactor;
  document.getElementById('calibrationNotice').textContent = data.calibrationNotice;

  // Update live monitoring clock & status pill
  const simTime = data.simulatedUtc ? new Date(data.simulatedUtc).toISOString().substring(11, 16) : '--:--';
  document.getElementById('liveClock').textContent = `Sim ${simTime} UTC • #${data.liveTickCount ?? 0}`;
  const pill = document.getElementById('livePill');
  const statusText = document.getElementById('liveStatusText');
  const toggleBtn = document.getElementById('toggleLiveBtn');
  if (data.isLiveStreaming) {
    pill.classList.remove('paused');
    statusText.textContent = 'LIVE';
    toggleBtn.textContent = 'Pause';
  } else {
    pill.classList.add('paused');
    statusText.textContent = 'PAUSED';
    toggleBtn.textContent = 'Resume';
  }
}

function renderMap(data) {
  gridLayerGroup.clearLayers();
  meadowLayerGroup.clearLayers();
  disturbanceLayerGroup.clearLayers();
  sensorLayerGroup.clearLayers();

  // 1. Seabed Suitability Grid Cells
  for (const cell of data.gridCells) {
    const bounds = [
      [cell.centerLat - cell.halfSizeLat, cell.centerLon - cell.halfSizeLon],
      [cell.centerLat + cell.halfSizeLat, cell.centerLon + cell.halfSizeLon]
    ];

    const isSelected = cell.cellId === state.selectedCellId;
    const fillColor = getLayerColor(cell, state.layer);
    const fillOpacity =
      state.layer === 'suitability' && cell.status === 'INSUFFICIENT_DATA'
        ? 0.42
        : state.layer === 'suitability' && cell.status === 'HOLD'
          ? 0.52
          : 0.68;

    const rect = L.rectangle(bounds, {
      color: isSelected ? '#ffffff' : cell.status === 'HOLD' ? '#f87171' : '#0b1c2c',
      weight: isSelected ? 2.2 : cell.status === 'HOLD' ? 1.1 : 0.4,
      fillColor,
      fillOpacity
    });

    const labelScore =
      cell.status === 'SCORED' ? `Score: ${cell.suitabilityScore}/100` : cell.status;
    rect.bindTooltip(
      `<strong>${cell.cellId}</strong> • ${labelScore}<br/>Limiter: ${cell.limitingFactor}<br/>Depth: ${cell.bathymetryDepthM} m | Conf: ${cell.confidencePct}%`,
      { sticky: true }
    );

    rect.on('click', () => {
      state.selectedCellId = cell.cellId;
      state.selectedStationId = null;
      showCellInspector(cell);
      renderMap(state.lastData);
    });

    rect.addTo(gridLayerGroup);
  }

  // 2. Reference Meadows
  if (document.getElementById('toggleMeadows').checked) {
    for (const m of data.referenceMeadows) {
      L.polygon(m.polygonLatLon, {
        color: '#34d399',
        weight: 2,
        dashArray: '5,4',
        fillColor: '#10b981',
        fillOpacity: 0.12
      })
        .bindTooltip(`<strong>${m.name}</strong><br/>${m.species}`)
        .addTo(meadowLayerGroup);
    }
  }

  // 3. Disturbance Zones (HOLD)
  if (document.getElementById('toggleDisturbance').checked) {
    for (const z of data.disturbanceZones) {
      L.polygon(z.polygonLatLon, {
        color: '#f87171',
        weight: 2,
        dashArray: '6,4',
        fillColor: '#ef4444',
        fillOpacity: 0.14
      })
        .bindTooltip(`<strong>HOLD: ${z.name}</strong><br/>${z.type}`)
        .addTo(disturbanceLayerGroup);
    }
  }

  // 4. 600 m Coastal LoRa Sensor Nodes
  if (document.getElementById('toggleSensors').checked) {
    for (const st of data.stations) {
      const isSelected = st.stationId === state.selectedStationId;
      const color = !st.isOnline
        ? '#94a3b8'
        : st.qualityFlag === 'FOULING_WARN'
          ? '#f59e0b'
          : '#22d3ee';

      // Surface buoy GPS offset tether line (PDF: LoRa buoy network p. 1)
      L.polyline(
        [
          [st.seabedLat, st.seabedLon],
          [st.buoyGpsLat, st.buoyGpsLon]
        ],
        { color: '#94a3b8', weight: 1, dashArray: '2,3' }
      ).addTo(sensorLayerGroup);

      // Surveyed seabed frame marker
      const marker = L.circleMarker([st.seabedLat, st.seabedLon], {
        radius: isSelected ? 9 : 7,
        color: isSelected ? '#ffffff' : '#07131e',
        weight: isSelected ? 2.5 : 2,
        fillColor: color,
        fillOpacity: st.isOnline ? 0.95 : 0.45
      });

      marker.bindTooltip(
        `<strong>${st.stationId}</strong> — ${st.name}<br/>Status: ${st.isOnline ? 'ONLINE' : 'OFFLINE'} | LoRa Hops: ${st.loraHopCount}<br/>PAR: ${st.dailySummary.dailyLightIntegralMol} mol/m²/d | DO Low: ${st.dailySummary.overnightDoLowMgL} mg/L | T: ${st.dailySummary.meanTempC} °C`,
        { direction: 'top' }
      );

      marker.on('click', (e) => {
        L.DomEvent.stopPropagation(e);
        state.selectedStationId = st.stationId;
        state.selectedCellId = null;
        showStationInspector(st);
        renderMap(state.lastData);
      });

      marker.addTo(sensorLayerGroup);
    }
  }
}

function renderLegend() {
  const el = document.getElementById('mapLegend');
  const legends = {
    suitability: {
      title: 'Suitability (0–100 Limiting)',
      items: [
        ['#10b981', '80–100 High Suitability'],
        ['#34d399', '65–79 Good Suitability'],
        ['#eab308', '50–64 Moderate'],
        ['#f97316', '30–49 Marginal'],
        ['#dc2626', '0–29 Poor'],
        ['#ef4444', 'HOLD (Disturbance)'],
        ['#64748b', 'INSUFFICIENT_DATA (>950 m)']
      ]
    },
    par: {
      title: 'Bottom Light (mol/m²/day)',
      items: [
        ['#fef08a', '≥ 14 (High Light)'],
        ['#facc15', '9 – 14'],
        ['#ca8a04', '5 – 9'],
        ['#854d0e', '2.5 – 5'],
        ['#3b2407', '< 2.5 (Light Limited)']
      ]
    },
    do: {
      title: 'Overnight DO Low (mg/L)',
      items: [
        ['#06b6d4', '≥ 7.0 mg/L (Healthy)'],
        ['#22d3ee', '6.0 – 7.0 mg/L'],
        ['#f59e0b', '5.0 – 6.0 mg/L (Stress)'],
        ['#ef4444', '< 5.0 mg/L (Hypoxic Risk)']
      ]
    },
    temp: {
      title: 'Water Temperature (°C)',
      items: [
        ['#38bdf8', '< 22.5 °C'],
        ['#eab308', '22.5 – 24.0 °C'],
        ['#f97316', '24.0 – 26.0 °C (Heat Stress)'],
        ['#dc2626', '≥ 26.0 °C (Severe Heat)']
      ]
    },
    depth: {
      title: 'Bathymetry Depth (m)',
      items: [
        ['#67e8f9', '≤ 3.0 m (Shallow)'],
        ['#0ea5e9', '3.0 – 5.5 m'],
        ['#1d4ed8', '5.5 – 8.5 m'],
        ['#1e1b4b', '> 8.5 m (Deep Bay)']
      ]
    },
    confidence: {
      title: 'Spatial Confidence (%)',
      items: [
        ['#10b981', '75–100% (Near Buoy)'],
        ['#eab308', '45–74% (Mid-Span)'],
        ['#f97316', '20–44% (Edge)'],
        ['#64748b', '< 20% (Insufficient)']
      ]
    }
  };

  const cfg = legends[state.layer];
  el.innerHTML =
    `<div class="legend-title">${cfg.title}</div>` +
    cfg.items
      .map(
        ([color, text]) =>
          `<div class="legend-item"><span class="legend-swatch" style="background:${color}"></span><span>${text}</span></div>`
      )
      .join('');
}

// --- Right Inspector Panels ---
function showCellInspector(cell) {
  document.getElementById('inspectorEmpty').classList.add('hidden');
  document.getElementById('stationInspector').classList.add('hidden');
  const el = document.getElementById('cellInspector');
  el.classList.remove('hidden');

  const statusBadge =
    cell.status === 'SCORED'
      ? '<span class="badge badge-ok">SCORED</span>'
      : cell.status === 'HOLD'
        ? '<span class="badge badge-danger">HOLD</span>'
        : '<span class="badge badge-gray">INSUFFICIENT_DATA</span>';

  const scoreDisplay = cell.status === 'SCORED' ? `${cell.suitabilityScore}` : '—';
  const scoreColor = getSuitabilityColor(cell);

  const s = cell.componentScores;
  const rows = [
    ['Bottom Light (PAR)', s.light],
    ['Temperature', s.temperature],
    ['Dissolved Oxygen', s.dissolvedOxygen],
    ['Salinity', s.salinity],
    ['Depth', s.depth],
    ['Substrate', s.substrate],
    ['Wave Exposure', s.waveExposure]
  ];
  if (s.seedSupply !== null && s.seedSupply !== undefined) {
    rows.push(['Seed Supply / Dispersal', s.seedSupply]);
  }

  el.innerHTML = `
    <div class="card">
      <div class="insp-header">
        <div>
          <div class="insp-title">${cell.cellId}</div>
          <div class="insp-sub">${cell.centerLat.toFixed(5)}°N, ${cell.centerLon.toFixed(5)}°E</div>
        </div>
        ${statusBadge}
      </div>

      <div class="score-hero">
        <div>
          <div class="kv-k">Provisional Suitability</div>
          <div class="score-big" style="color:${scoreColor}">${scoreDisplay}<span style="font-size:0.85rem;color:#8fa9c2"> / 100</span></div>
        </div>
        <div style="text-align:right">
          <div class="kv-k">Spatial Confidence</div>
          <div class="kv-v mono">${cell.confidencePct}%</div>
          <div class="insp-sub">Nearest: ${cell.nearestSensorId} (${cell.distanceToNearestSensorM} m)</div>
        </div>
      </div>

      <div class="kv-box" style="margin-bottom:10px;border-color:rgba(245,158,11,0.45)">
        <div class="kv-k">Limiting Factor (Bottleneck)</div>
        <div class="kv-v" style="color:#fbbf24">${cell.limitingFactor}</div>
        ${cell.holdReason ? `<div class="insp-sub">${cell.holdReason}</div>` : ''}
      </div>

      <div class="kv-grid">
        <div class="kv-box"><div class="kv-k">Depth / Substrate</div><div class="kv-v">${cell.bathymetryDepthM} m • ${cell.substrate}</div></div>
        <div class="kv-box"><div class="kv-k">Wave Exposure</div><div class="kv-v">${(cell.waveExposureIndex * 100).toFixed(0)}%</div></div>
        <div class="kv-box"><div class="kv-k">Bottom Light (DLI)</div><div class="kv-v">${cell.interpolatedDailyLightMol} mol/m²/d</div></div>
        <div class="kv-box"><div class="kv-k">Overnight DO Low</div><div class="kv-v">${cell.interpolatedDoLowMgL} mg/L</div></div>
        <div class="kv-box"><div class="kv-k">Mean Temp</div><div class="kv-v">${cell.interpolatedTempC} °C</div></div>
        <div class="kv-box"><div class="kv-k">Salinity (Synth.)</div><div class="kv-v">${cell.interpolatedSalinityPsu} PSU</div></div>
      </div>

      <h2>Limiting-Condition Breakdown (0–100)</h2>
      <div class="bar-list">
        ${rows
          .map(([name, val]) => {
            const isLim = cell.limitingFactor === name;
            return `
            <div>
              <div class="bar-row-head ${isLim ? 'is-limiter' : ''}">
                <span>${name}${isLim ? ' ★' : ''}</span>
                <span class="mono">${val}</span>
              </div>
              <div class="bar-track">
                <div class="bar-fill" style="width:${val}%;background:${scoreBarColor(val)}"></div>
              </div>
            </div>`;
          })
          .join('')}
      </div>
    </div>
  `;
}

async function showStationInspector(st, isLiveRefresh = false) {
  document.getElementById('inspectorEmpty').classList.add('hidden');
  document.getElementById('cellInspector').classList.add('hidden');
  const el = document.getElementById('stationInspector');
  el.classList.remove('hidden');

  const canUpdateInPlace = isLiveRefresh && state.chartInstance && state.chartStationId === st.stationId && document.getElementById('stationChart');

  if (!canUpdateInPlace) {
    const statusBadge = st.isOnline
      ? '<span class="badge badge-ok">ONLINE</span>'
      : '<span class="badge badge-gray">OFFLINE (SIMULATED)</span>';

    el.innerHTML = `
      <div class="card">
        <div class="insp-header">
          <div>
            <div class="insp-title">${st.stationId} • ${st.name}</div>
            <div class="insp-sub">Zone: ${st.zoneType} | Quality: ${st.qualityFlag}</div>
          </div>
          ${statusBadge}
        </div>

        <button id="toggleStationOnlineBtn" class="btn ${st.isOnline ? 'btn-danger' : 'btn-success'}" style="margin-bottom:10px">
          ${st.isOnline ? 'Simulate Sensor Outage (Take Offline)' : 'Restore Sensor Online'}
        </button>

        <div class="kv-grid">
          <div class="kv-box"><div class="kv-k">Seabed Coords</div><div class="kv-v mono">${st.seabedLat.toFixed(4)}, ${st.seabedLon.toFixed(4)}</div></div>
          <div class="kv-box"><div class="kv-k">Surface Buoy GPS</div><div class="kv-v mono" id="stBuoyGps">${st.buoyGpsLat.toFixed(4)}, ${st.buoyGpsLon.toFixed(4)}</div></div>
          <div class="kv-box"><div class="kv-k">Depth / Height</div><div class="kv-v">${st.seabedDepthM} m / +${st.sensorHeightAboveSeabedM} m</div></div>
          <div class="kv-box"><div class="kv-k">868 MHz LoRa</div><div class="kv-v">${st.loraHopCount === 1 ? 'Direct (1 hop)' : `Relay via ${st.relayStationId} (2 hops)`}</div></div>
          <div class="kv-box"><div class="kv-k">Daily Light (DLI)</div><div class="kv-v" id="stDli">${st.dailySummary.dailyLightIntegralMol} mol/m²/d</div></div>
          <div class="kv-box"><div class="kv-k">Overnight DO Low</div><div class="kv-v" id="stDoLow">${st.dailySummary.overnightDoLowMgL} mg/L</div></div>
          <div class="kv-box"><div class="kv-k">Mean / Max Temp</div><div class="kv-v" id="stTemp">${st.dailySummary.meanTempC} / ${st.dailySummary.maxTempC} °C</div></div>
          <div class="kv-box"><div class="kv-k">Battery / K<sub>d</sub></div><div class="kv-v" id="stBattKd">${st.batteryV} V • K<sub>d</sub> ${st.dailySummary.estimatedKd}</div></div>
        </div>

        <h2>24h Telemetry (10-Min Intervals • Live Rolling)</h2>
        <div class="chart-wrap">
          <canvas id="stationChart"></canvas>
        </div>
      </div>
    `;

    document.getElementById('toggleStationOnlineBtn').addEventListener('click', () => {
      if (state.offlineStationIds.has(st.stationId)) {
        state.offlineStationIds.delete(st.stationId);
      } else {
        state.offlineStationIds.add(st.stationId);
      }
      state.chartStationId = null;
      fetchAndRender();
    });
  } else {
    // Update live numeric readouts in place without destroying the chart canvas
    document.getElementById('stBuoyGps').textContent = `${st.buoyGpsLat.toFixed(4)}, ${st.buoyGpsLon.toFixed(4)}`;
    document.getElementById('stDli').textContent = `${st.dailySummary.dailyLightIntegralMol} mol/m²/d`;
    document.getElementById('stDoLow').textContent = `${st.dailySummary.overnightDoLowMgL} mg/L`;
    document.getElementById('stTemp').textContent = `${st.dailySummary.meanTempC} / ${st.dailySummary.maxTempC} °C`;
    document.getElementById('stBattKd').innerHTML = `${st.batteryV} V • K<sub>d</sub> ${st.dailySummary.estimatedKd}`;
  }

  // Load 10-minute telemetry from C# API
  const res = await fetch(`/api/stations/${st.stationId}/readings`);
  if (!res.ok) return;
  const data = await res.json();

  const labels = data.readings.map((r, idx) => {
    if (idx % 12 !== 0) return '';
    return new Date(r.utcTime).toISOString().substring(11, 16);
  });
  const parVals = data.readings.map(r => r.parUmol);
  const doVals = data.readings.map(r => r.doMgL);
  const tempVals = data.readings.map(r => r.tempC);

  if (canUpdateInPlace) {
    state.chartInstance.data.labels = labels;
    state.chartInstance.data.datasets[0].data = parVals;
    state.chartInstance.data.datasets[1].data = doVals;
    state.chartInstance.data.datasets[2].data = tempVals;
    state.chartInstance.update('none');
    return;
  }

  if (state.chartInstance) state.chartInstance.destroy();
  const ctx = document.getElementById('stationChart').getContext('2d');
  state.chartStationId = st.stationId;
  state.chartInstance = new Chart(ctx, {
    type: 'line',
    data: {
      labels,
      datasets: [
        {
          label: 'PAR (µmol/m²/s)',
          data: parVals,
          borderColor: '#facc15',
          backgroundColor: 'rgba(250, 204, 21, 0.12)',
          yAxisID: 'yPar',
          borderWidth: 1.8,
          pointRadius: 0,
          tension: 0.3
        },
        {
          label: 'DO (mg/L)',
          data: doVals,
          borderColor: '#22d3ee',
          yAxisID: 'yDo',
          borderWidth: 1.8,
          pointRadius: 0,
          tension: 0.3
        },
        {
          label: 'Temp (°C)',
          data: tempVals,
          borderColor: '#f97316',
          yAxisID: 'yDo',
          borderWidth: 1.5,
          pointRadius: 0,
          tension: 0.3
        }
      ]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      plugins: {
        legend: { labels: { color: '#cbd5e1', font: { size: 10 }, boxWidth: 10 } }
      },
      scales: {
        x: { ticks: { color: '#8fa9c2', font: { size: 9 } }, grid: { display: false } },
        yPar: {
          position: 'left',
          ticks: { color: '#facc15', font: { size: 9 } },
          grid: { color: 'rgba(255,255,255,0.06)' }
        },
        yDo: {
          position: 'right',
          ticks: { color: '#22d3ee', font: { size: 9 } },
          grid: { display: false }
        }
      }
    }
  });
}

// --- Event Listeners ---
document.querySelectorAll('#speciesControl .seg-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('#speciesControl .seg-btn').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    state.species = btn.dataset.value;
    fetchAndRender();
  });
});

document.querySelectorAll('#pathwayControl .seg-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('#pathwayControl .seg-btn').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    state.pathway = btn.dataset.value;
    fetchAndRender();
  });
});

document.getElementById('layerSelect').addEventListener('change', e => {
  state.layer = e.target.value;
  if (state.lastData) {
    renderMap(state.lastData);
    renderLegend();
  }
});

['toggleSensors', 'toggleMeadows', 'toggleDisturbance'].forEach(id => {
  document.getElementById(id).addEventListener('change', () => {
    if (state.lastData) renderMap(state.lastData);
  });
});

document.getElementById('toggleSatellite').addEventListener('change', e => {
  if (e.target.checked) {
    map.removeLayer(darkBasemap);
    satelliteBasemap.addTo(map);
  } else {
    map.removeLayer(satelliteBasemap);
    darkBasemap.addTo(map);
  }
});

document.getElementById('tempSlider').addEventListener('input', e => {
  state.tempOffsetC = parseFloat(e.target.value);
  document.getElementById('tempVal').textContent = `${state.tempOffsetC >= 0 ? '+' : ''}${state.tempOffsetC.toFixed(1)} °C`;
  fetchAndRender();
});

document.getElementById('turbSlider').addEventListener('input', e => {
  state.turbidityFactor = parseFloat(e.target.value);
  document.getElementById('turbVal').textContent = `${state.turbidityFactor.toFixed(1)}×`;
  fetchAndRender();
});

document.getElementById('resetSimBtn').addEventListener('click', () => {
  state.tempOffsetC = 0.0;
  state.turbidityFactor = 1.0;
  state.offlineStationIds.clear();
  document.getElementById('tempSlider').value = '0';
  document.getElementById('tempVal').textContent = '0.0 °C';
  document.getElementById('turbSlider').value = '1.0';
  document.getElementById('turbVal').textContent = '1.0×';
  fetchAndRender();
});

document.getElementById('toggleLiveBtn').addEventListener('click', async () => {
  const res = await fetch('/api/live/toggle', { method: 'POST' });
  if (res.ok) {
    const live = await res.json();
    state.isLiveStreaming = live.isRunning;
    fetchAndRender(true);
  }
});

// Initial load + periodic 3-second real-time polling
fetchAndRender(false);
state.pollTimerId = setInterval(() => {
  if (state.isLiveStreaming) {
    fetchAndRender(true);
  }
}, 3000);
