/* Shared RF station map (APRS weather + digimode grids). Used by the main desk and /map.html pop-out.
   Basemap is local Natural Earth land GeoJSON + Leaflet — no tile CDN, no OpenStreetMap. */
(function (global) {
  "use strict";

  const layerCache = new Map();

  function loadMapLayer(path) {
    if (!layerCache.has(path)) {
      layerCache.set(path, fetch(path, { cache: "force-cache" })
        .then((res) => {
          if (!res.ok) throw new Error(`${path} ${res.status}`);
          return res.json();
        })
        .catch((err) => {
          layerCache.delete(path);
          throw err;
        }));
    }
    return layerCache.get(path);
  }

  /** Great-circle distance in statute miles. */
  function distanceMiles(lat1, lon1, lat2, lon2) {
    const toRad = Math.PI / 180;
    const r = 3958.7613;
    const dLat = (lat2 - lat1) * toRad;
    const dLon = (lon2 - lon1) * toRad;
    const a = Math.sin(dLat / 2) ** 2
      + Math.cos(lat1 * toRad) * Math.cos(lat2 * toRad) * Math.sin(dLon / 2) ** 2;
    return 2 * r * Math.asin(Math.min(1, Math.sqrt(a)));
  }

  function formatDistance(miles) {
    if (!Number.isFinite(miles)) return "";
    if (miles < 0.1) return "<0.1 mi";
    if (miles < 10) return `${miles.toFixed(1)} mi`;
    if (miles < 100) return `${miles.toFixed(0)} mi`;
    return `${Math.round(miles)} mi`;
  }

  function escapeHtml(text) {
    return String(text ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function formatAge(iso) {
    const heard = iso ? new Date(iso) : null;
    if (!heard || !Number.isFinite(heard.getTime()))
      return "";
    const seconds = (Date.now() - heard.getTime()) / 1000;
    if (!Number.isFinite(seconds) || seconds < 0)
      return "";
    if (seconds < 60)
      return `${Math.max(1, Math.round(seconds))}s ago`;
    if (seconds < 3600)
      return `${Math.round(seconds / 60)}m ago`;
    if (seconds < 86400)
      return `${Math.round(seconds / 3600)}h ago`;
    return `${Math.round(seconds / 86400)}d ago`;
  }

  function sourceLabel(station) {
    const src = String(station.source || "").toLowerCase();
    if (station.isWeather || src === "weather") return "weather";
    if (src === "aprs") return "APRS";
    if (src) return src.toUpperCase();
    return station.isWeather === false ? "position" : "APRS";
  }

  function markerClass(station) {
    const src = String(station.source || "").toLowerCase();
    const wx = station.isWeather === true
      || src === "weather"
      || Number.isFinite(station.temperatureF)
      || Number.isFinite(station.humidityPercent)
      || Number.isFinite(station.windMph);
    if (wx) return "wx-marker";
    if (src && src !== "aprs") return "wx-marker mode";
    return "wx-marker digi";
  }

  /** Maidenhead 4-/6-character → square-center lat/lon (same rules as Dsp Maidenhead). */
  function maidenheadCenter(grid) {
    const text = String(grid || "").trim().toUpperCase();
    if (text.length !== 4 && text.length !== 6)
      return null;
    if (text[0] < "A" || text[0] > "R" || text[1] < "A" || text[1] > "R")
      return null;
    if (text[2] < "0" || text[2] > "9" || text[3] < "0" || text[3] > "9")
      return null;
    if (text.length === 6 && (text[4] < "A" || text[4] > "X" || text[5] < "A" || text[5] > "X"))
      return null;
    let lon = (text.charCodeAt(0) - 65) * 20 - 180;
    let lat = (text.charCodeAt(1) - 65) * 10 - 90;
    lon += (text.charCodeAt(2) - 48) * 2;
    lat += text.charCodeAt(3) - 48;
    if (text.length === 6) {
      lon += (text.charCodeAt(4) - 65) * (2 / 24) + 2 / 48;
      lat += (text.charCodeAt(5) - 65) * (1 / 24) + 1 / 48;
    } else {
      lon += 1;
      lat += 0.5;
    }
    return { lat, lon };
  }

  function stationCoords(station) {
    let lat = Number(station.latitude);
    let lon = Number(station.longitude);
    if (Number.isFinite(lat) && Number.isFinite(lon))
      return { lat, lon };
    const fromGrid = maidenheadCenter(station.grid);
    if (fromGrid)
      return fromGrid;
    const comment = String(station.comment || "");
    const match = comment.match(/\b([A-R]{2}\d{2}(?:[A-X]{2})?)\b/i);
    if (match)
      return maidenheadCenter(match[1]);
    return null;
  }

  function markerLabel(station) {
    const call = String(station.callsign || "?").toUpperCase();
    if (Number.isFinite(station.temperatureF))
      return `${call} ${Math.round(station.temperatureF)}°`;
    const grid = String(station.grid || "").trim().toUpperCase();
    if (grid)
      return `${call} ${grid}`;
    return call;
  }

  function markerIconSize(label) {
    // Pill width from character count; keep height fixed for anchor math.
    const width = Math.min(160, Math.max(44, 14 + String(label).length * 7.2));
    return [width, 28];
  }

  function looksLikeWxResidue(text) {
    const t = String(text || "").trim();
    if (!t) return false;
    // Unparsed APRS weather: ".../...g...t..." or "186/000g002t054..."
    return /^(?:\d{3}|\.{3})\/(?:\d{3}|\.{3})/.test(t)
      || /^[gtprPhb](?:-?\d{2,5}|\.{2,5})/.test(t);
  }

  function detailLines(station, homeLat, homeLon) {
    const bits = [];
    bits.push(sourceLabel(station));
    const grid = String(station.grid || "").trim().toUpperCase();
    if (grid)
      bits.push(`grid ${grid}`);
    const comment = String(station.comment || "").trim();
    if (comment && !looksLikeWxResidue(comment) && !(grid && comment.toUpperCase().includes(grid)))
      bits.push(comment);
    const coords = stationCoords(station);
    const lat = coords?.lat;
    const lon = coords?.lon;
    if (Number.isFinite(lat) && Number.isFinite(lon))
      bits.push(grid
        ? `${lat.toFixed(3)}°, ${lon.toFixed(3)}° (grid center)`
        : `${lat.toFixed(3)}°, ${lon.toFixed(3)}°`);
    if (Number.isFinite(homeLat) && Number.isFinite(homeLon) && Number.isFinite(lat) && Number.isFinite(lon)) {
      const miles = distanceMiles(homeLat, homeLon, lat, lon);
      const label = formatDistance(miles);
      if (label) bits.push(`${label} from home`);
    }
    if (Number.isFinite(station.temperatureF))
      bits.push(`temperature ${station.temperatureF} °F`);
    if (Number.isFinite(station.humidityPercent))
      bits.push(`humidity ${station.humidityPercent}%`);
    if (Number.isFinite(station.windMph)) {
      if (station.windMph === 0 && (!Number.isFinite(station.windDegrees) || station.windDegrees === 0))
        bits.push("wind calm");
      else if (station.windMph === 0 && Number.isFinite(station.windDegrees))
        bits.push(`wind from ${station.windDegrees}° (calm)`);
      else if (Number.isFinite(station.windDegrees))
        bits.push(`wind from ${station.windDegrees}° at ${station.windMph} mph`);
      else
        bits.push(`wind ${station.windMph} mph`);
    } else if (Number.isFinite(station.windDegrees)) {
      bits.push(`wind from ${station.windDegrees}°`);
    }
    if (Number.isFinite(station.gustMph) && station.gustMph > 0)
      bits.push(`gust ${station.gustMph} mph`);
    if (Number.isFinite(station.pressureMbar) && station.pressureMbar > 0)
      bits.push(`pressure ${Number(station.pressureMbar).toFixed(1)} mbar`);
    if (Number.isFinite(station.rainLastHourIn))
      bits.push(station.rainLastHourIn > 0
        ? `rain last hour ${station.rainLastHourIn} in`
        : "no rain last hour");
    if (Number.isFinite(station.rain24hIn) && station.rain24hIn > 0)
      bits.push(`rain 24h ${station.rain24hIn} in`);
    if (Number.isFinite(station.rainSinceMidnightIn) && station.rainSinceMidnightIn > 0)
      bits.push(`rain today ${station.rainSinceMidnightIn} in`);
    const age = formatAge(station.heardAt);
    if (age) bits.push(`heard ${age}`);
    return bits;
  }

  function detailHtml(station, homeLat, homeLon) {
    const bits = detailLines(station, homeLat, homeLon);
    return `<div class="wx-popup"><strong>${escapeHtml(station.callsign || "")}</strong><div class="wx-popup-body">${bits.map(escapeHtml).join("<br>")}</div></div>`;
  }

  function stationKey(station) {
    return String(station.callsign || "").toUpperCase();
  }

  function setKey(list) {
    return list.map(stationKey).filter(Boolean).sort().join("|");
  }

  function addGraticule(map) {
    const lines = [];
    for (let lon = -180; lon <= 180; lon += 30)
      lines.push([[-85, lon], [85, lon]]);
    for (let lat = -60; lat <= 60; lat += 30)
      lines.push([[lat, -180], [lat, 180]]);
    L.polyline(lines, {
      color: "#2a3844",
      weight: 1,
      opacity: 0.85,
      interactive: false,
      pane: "overlayPane"
    }).addTo(map);
  }

  function addNameLabels(map, path, className, minZoom) {
    loadMapLayer(path).then((geo) => {
      const layer = L.geoJSON(geo, {
        interactive: false,
        pointToLayer: (_feature, latlng) => L.marker(latlng, {
          interactive: false,
          keyboard: false,
          icon: L.divIcon({
            className: "",
            html: `<span class="map-label ${className}">${escapeHtml(_feature.properties?.name || "")}</span>`,
            iconSize: [0, 0],
            iconAnchor: [0, 0]
          })
        })
      });

      const sync = () => {
        const show = map.getZoom() >= minZoom;
        if (show && !map.hasLayer(layer))
          layer.addTo(map);
        else if (!show && map.hasLayer(layer))
          map.removeLayer(layer);
      };
      map.on("zoomend", sync);
      sync();
    }).catch(() => {
      console.warn("Offline label layer failed:", path);
    });
  }

  function formatPeakElev(props) {
    const ft = Number(props?.elevFt);
    const m = Number(props?.elevM);
    if (Number.isFinite(ft) && ft > 0) {
      if (ft >= 10000)
        return `${Math.round(ft / 100) / 10}k ft`;
      return `${ft.toLocaleString("en-US")} ft`;
    }
    if (Number.isFinite(m) && m > 0)
      return `${m} m`;
    return "";
  }

  function peakTier(props) {
    const elev = Number(props?.elevM) || 0;
    const rank = Number(props?.rank) || 9;
    // Sparse tiers so the map stays readable as a signal-barrier hint.
    if (elev >= 5000 || rank <= 2) return 1; // major: earlier zoom
    if (elev >= 3000 || rank <= 4) return 2;
    return 3;
  }

  function addPeakMarkers(map) {
    loadMapLayer("/map/peaks.geojson").then((geo) => {
      const groups = { 1: L.layerGroup(), 2: L.layerGroup(), 3: L.layerGroup() };
      for (const feature of geo.features || []) {
        const props = feature.properties || {};
        const coords = feature.geometry?.coordinates;
        if (!coords) continue;
        const elev = formatPeakElev(props);
        const name = String(props.name || "Peak");
        const short = name.length > 18 ? `${name.slice(0, 16)}…` : name;
        const tier = peakTier(props);
        // Anchor is a zero-size point at lon/lat; mark + label are absolutely offset
        // so long names (e.g. Mount Katahdin) don't drag the triangle off the summit.
        const html = `<span class="map-peak map-peak-t${tier}" title="${escapeHtml(name)} · ${escapeHtml(elev)} ASL">`
          + `<span class="map-peak-mark">▲</span>`
          + `<span class="map-peak-text">${escapeHtml(short)}`
          + (elev ? ` <span class="map-peak-elev">${escapeHtml(elev)}</span>` : "")
          + `</span></span>`;
        const lon = Number(coords[0]);
        const lat = Number(coords[1]);
        if (!Number.isFinite(lat) || !Number.isFinite(lon))
          continue;
        const marker = L.marker([lat, lon], {
          interactive: false,
          keyboard: false,
          icon: L.divIcon({
            className: "map-peak-icon",
            html,
            iconSize: [0, 0],
            iconAnchor: [0, 0]
          })
        });
        groups[tier].addLayer(marker);
      }

      const sync = () => {
        const z = map.getZoom();
        const show = {
          1: z >= 3,
          2: z >= 5,
          3: z >= 6
        };
        for (const [tier, layer] of Object.entries(groups)) {
          const on = show[Number(tier)];
          if (on && !map.hasLayer(layer))
            layer.addTo(map);
          else if (!on && map.hasLayer(layer))
            map.removeLayer(layer);
        }
      };
      map.on("zoomend", sync);
      sync();
    }).catch(() => {
      console.warn("Offline peaks layer failed.");
    });
  }

  /** Keep Leaflet’s SVG/canvas size matched to the real container (pop-out flex layout changes often). */
  function bindMapSize(map, el) {
    let timer = 0;
    const kick = () => {
      if (!map || !el) return;
      const w = el.clientWidth;
      const h = el.clientHeight;
      if (w < 2 || h < 2) return;
      const size = map.getSize();
      if (Math.abs(size.x - w) <= 1 && Math.abs(size.y - h) <= 1)
        return;
      map.invalidateSize({ animate: false, pan: false });
    };
    const schedule = () => {
      if (timer) return;
      timer = requestAnimationFrame(() => {
        timer = 0;
        kick();
        // Second pass after flex/header wrap settles.
        requestAnimationFrame(kick);
      });
    };

    schedule();
    setTimeout(schedule, 50);
    setTimeout(schedule, 200);
    setTimeout(schedule, 600);

    if (typeof ResizeObserver !== "undefined") {
      const ro = new ResizeObserver(schedule);
      ro.observe(el);
      if (el.parentElement)
        ro.observe(el.parentElement);
      map._agenticSizeObserver = ro;
    }

    window.addEventListener("resize", schedule);
    window.visualViewport?.addEventListener("resize", schedule);
    map.on("unload", () => {
      window.removeEventListener("resize", schedule);
      window.visualViewport?.removeEventListener("resize", schedule);
      try { map._agenticSizeObserver?.disconnect(); } catch (_) { /* ignore */ }
    });

    return schedule;
  }

  /**
   * Approximate subsolar point from UTC (NOAA-style Fourier series). Offline — no network.
   * Accurate to ~0.3° for a map terminator; good enough for HF day/night planning.
   */
  function subsolarPoint(date) {
    const ms = date instanceof Date ? date.getTime() : Date.now();
    const utc = new Date(ms);
    const start = Date.UTC(utc.getUTCFullYear(), 0, 0);
    const dayOfYear = (ms - start) / 86_400_000;
    const hour = utc.getUTCHours() + utc.getUTCMinutes() / 60 + utc.getUTCSeconds() / 3600;
    const gamma = (2 * Math.PI / 365) * (dayOfYear - 1 + (hour - 12) / 24);
    // Declination (radians).
    const decl = 0.006918
      - 0.399912 * Math.cos(gamma) + 0.070257 * Math.sin(gamma)
      - 0.006758 * Math.cos(2 * gamma) + 0.000907 * Math.sin(2 * gamma)
      - 0.002697 * Math.cos(3 * gamma) + 0.00148 * Math.sin(3 * gamma);
    // Equation of time (minutes).
    const eqTime = 229.18 * (
      0.000075
      + 0.001868 * Math.cos(gamma) - 0.032077 * Math.sin(gamma)
      - 0.014615 * Math.cos(2 * gamma) - 0.040849 * Math.sin(2 * gamma));
    let lon = -15 * (hour - 12) - eqTime / 4;
    while (lon < -180) lon += 360;
    while (lon > 180) lon -= 360;
    return { lat: decl * (180 / Math.PI), lon };
  }

  /** Latitude on the geometric terminator (° sun elevation = 0) at longitude lonDeg. */
  function terminatorLatitude(lonDeg, sunLatDeg, sunLonDeg) {
    const rad = Math.PI / 180;
    // Avoid tan(δ)→0 at equinox; a 0.05° nudge keeps the closed-form stable.
    const clamped = Math.abs(sunLatDeg) < 0.05
      ? (sunLatDeg >= 0 ? 0.05 : -0.05)
      : sunLatDeg;
    const delta = clamped * rad;
    const H = (lonDeg - sunLonDeg) * rad;
    return Math.atan(-Math.cos(H) / Math.tan(delta)) / rad;
  }

  function terminatorRing(sunLat, sunLon, step) {
    const terminator = [];
    for (let lon = -180; lon <= 180; lon += step)
      terminator.push([lon, terminatorLatitude(lon, sunLat, sunLon)]);
    const nightPole = sunLat >= 0 ? -90 : 90;
    const ring = terminator.map((p) => p.slice());
    ring.push([180, nightPole], [-180, nightPole]);
    ring.push(ring[0].slice());
    return ring;
  }

  /**
   * Night polygon from solar geometry (offline). Geometric terminator (sun elevation 0°);
   * refraction / civil twilight are ignored so the shade stays a clean estimate.
   */
  function nightPolygons(date) {
    const sun = subsolarPoint(date);
    const ring = terminatorRing(sun.lat, sun.lon, 3);
    return {
      sun,
      night: {
        type: "Feature",
        properties: { kind: "night" },
        geometry: { type: "Polygon", coordinates: [ring] }
      }
    };
  }

  const DAYNIGHT_PREF = "agentic-sdr-map-daynight";

  function dayNightPrefEnabled() {
    try {
      return localStorage.getItem(DAYNIGHT_PREF) === "1";
    } catch (_) {
      return false;
    }
  }

  function setDayNightPref(on) {
    try {
      localStorage.setItem(DAYNIGHT_PREF, on ? "1" : "0");
    } catch (_) { /* ignore */ }
  }

  /**
   * Optional day/night shade + sun marker. Pure solar geometry (offline).
   * @returns {{ setEnabled: (on: boolean) => void, enabled: () => boolean, destroy: () => void }}
   */
  function bindDayNightOverlay(map) {
    let layer = null;
    let sunMarker = null;
    let timer = 0;
    let enabled = false;

    function clear() {
      if (timer) {
        clearInterval(timer);
        timer = 0;
      }
      if (layer) {
        map.removeLayer(layer);
        layer = null;
      }
      if (sunMarker) {
        map.removeLayer(sunMarker);
        sunMarker = null;
      }
    }

    function paint() {
      if (!enabled || !map)
        return;
      const { sun, night } = nightPolygons(new Date());
      const group = L.layerGroup();
      L.geoJSON(night, {
        interactive: false,
        style: {
          fillColor: "#050a10",
          fillOpacity: 0.48,
          color: "#2a4050",
          weight: 1.2,
          opacity: 0.65
        }
      }).addTo(group);

      if (layer)
        map.removeLayer(layer);
      layer = group.addTo(map);

      const sunHtml = `<span class="map-sun" title="Subsolar point ${sun.lat.toFixed(1)}°, ${sun.lon.toFixed(1)}°">☀</span>`;
      if (sunMarker)
        map.removeLayer(sunMarker);
      sunMarker = L.marker([sun.lat, sun.lon], {
        interactive: false,
        keyboard: false,
        zIndexOffset: 200,
        icon: L.divIcon({
          className: "map-sun-icon",
          html: sunHtml,
          iconSize: [0, 0],
          iconAnchor: [0, 0]
        })
      }).addTo(map);
    }

    function setEnabled(on) {
      enabled = !!on;
      setDayNightPref(enabled);
      if (!enabled) {
        clear();
        return;
      }
      paint();
      if (timer)
        clearInterval(timer);
      // Terminator crawls ~2.5°/hour; 10 min is enough without busy redraws.
      timer = setInterval(paint, 10 * 60_000);
    }

    return {
      setEnabled,
      enabled: () => enabled,
      destroy: clear,
      refresh: () => { if (enabled) paint(); }
    };
  }

  function addOfflineBasemap(map, onLayerPainted) {
    map.getContainer().classList.add("offline-basemap");
    addGraticule(map);

    const layers = [
      {
        path: "/map/land.geojson",
        style: { fillColor: "#3d352c", fillOpacity: 1, color: "#5c5348", weight: 0.6, opacity: 1 }
      },
      {
        path: "/map/lakes.geojson",
        style: { fillColor: "#1a3548", fillOpacity: 0.95, color: "#2a4a5c", weight: 0.5, opacity: 0.9 }
      },
      {
        path: "/map/rivers.geojson",
        style: { color: "#3a5a6e", weight: 1.1, opacity: 0.75, fillOpacity: 0 }
      },
      {
        // Country borders (50m) — includes continuous US–Canada / US–Mexico lines.
        path: "/map/countries.geojson",
        style: { color: "#e2d2b4", weight: 1.8, opacity: 0.95, fillOpacity: 0 }
      },
      {
        path: "/map/states.geojson",
        style: { color: "#8a7a64", weight: 0.85, opacity: 0.55, fillOpacity: 0 }
      }
    ];

    for (const layer of layers) {
      loadMapLayer(layer.path).then((geo) => {
        L.geoJSON(geo, { interactive: false, style: layer.style }).addTo(map);
        if (typeof onLayerPainted === "function")
          onLayerPainted();
      }).catch(() => {
        console.warn("Offline map layer failed:", layer.path);
      });
    }

    // Country names from farther out; state/province names when zoomed in a bit.
    addNameLabels(map, "/map/country-labels.geojson", "map-label-country", 2);
    addNameLabels(map, "/map/state-labels.geojson", "map-label-state", 4);
    // Major peaks with elevation ASL — sparse tiers by height so RF barriers stay hint-like.
    addPeakMarkers(map);

    // Imperial first for US ops; metric stays as the shorter second bar.
    L.control.scale({ imperial: true, metric: true, maxWidth: 120, position: "bottomleft" }).addTo(map);
    map.attributionControl.setPrefix("");
    map.attributionControl.addAttribution("Natural Earth (local) · Leaflet · day/night solar estimate");
  }

  /**
   * @param {object} opts
   * @param {HTMLElement} opts.mapEl
   * @param {HTMLElement} [opts.emptyEl]
   * @param {HTMLElement} [opts.legendEl]
   * @param {HTMLElement} [opts.asideEl]
   * @param {HTMLElement} [opts.foldEl]
   * @param {HTMLInputElement} [opts.dayNightEl] checkbox to toggle day/night shade
   * @param {boolean} [opts.popout]
   */
  function create(opts) {
    const mapEl = opts.mapEl;
    const emptyEl = opts.emptyEl || null;
    const legendEl = opts.legendEl || null;
    const asideEl = opts.asideEl || null;
    const foldEl = opts.foldEl || null;
    const dayNightEl = opts.dayNightEl || null;
    const popout = !!opts.popout;

    let map = null;
    /** @type {ReturnType<typeof bindDayNightOverlay> | null} */
    let dayNight = null;
    const markers = new Map();
    /** @type {Map<string, string>} */
    const markerFingerprints = new Map();
    let homeMarker = null;
    let homeLat = null;
    let homeLon = null;
    let lastFitKey = "";
    let lastLegendKey = "";
    let userPanZoom = false;
    let suppressUserFlag = false;
    let interacting = false;
    let pendingStations = null;
    let pendingHome = null;
    let requestSize = null;

    function onUserView() {
      if (suppressUserFlag) return;
      userPanZoom = true;
      interacting = true;
    }

    function onUserViewEnd() {
      interacting = false;
      if (pendingStations) {
        const stations = pendingStations;
        const home = pendingHome;
        pendingStations = null;
        pendingHome = null;
        paint(stations, home?.lat, home?.lon);
      }
    }

    function ensureMap(centerLat, centerLon) {
      if (!mapEl || typeof L === "undefined")
        return null;
      if (map)
        return map;

      if (popout)
        mapEl.classList.add("station-map-popout");

      const lat = Number.isFinite(centerLat) ? centerLat : 39.5;
      const lon = Number.isFinite(centerLon) ? centerLon : -98.35;
      map = L.map(mapEl, {
        zoomControl: true,
        attributionControl: true,
        worldCopyJump: true,
        minZoom: 1,
        maxBounds: [[-85, -180], [85, 180]],
        maxBoundsViscosity: 0.75
      }).setView([lat, lon], Number.isFinite(centerLat) ? 5 : 2);

      requestSize = bindMapSize(map, mapEl);
      addOfflineBasemap(map, () => requestSize?.());
      dayNight = bindDayNightOverlay(map);
      const preferDayNight = dayNightEl ? dayNightEl.checked : dayNightPrefEnabled();
      if (dayNightEl) {
        dayNightEl.checked = preferDayNight;
        dayNightEl.addEventListener("change", () => {
          dayNight?.setEnabled(dayNightEl.checked);
        });
      }
      dayNight.setEnabled(preferDayNight);

      map.on("dragstart", onUserView);
      map.on("zoomstart", onUserView);
      map.on("dragend", onUserViewEnd);
      map.on("zoomend", onUserViewEnd);

      if (foldEl && !foldEl.dataset.mapSized) {
        foldEl.dataset.mapSized = "1";
        foldEl.addEventListener("toggle", () => {
          if (!foldEl.open || !map) return;
          requestAnimationFrame(() => {
            requestSize?.();
            map.invalidateSize({ animate: false, pan: false });
            fitAll(true);
            dayNight?.refresh();
          });
        });
      }

      return map;
    }

    /** Keep wheel scroll inside the tip body; don't zoom the map while reading details. */
    function onStationTooltipOpen(ev) {
      const root = ev.tooltip?.getElement?.();
      if (!root || root.dataset.scrollBound)
        return;
      root.dataset.scrollBound = "1";
      const body = root.querySelector(".wx-popup-body");
      const stopMapWheel = (e) => {
        e.stopPropagation();
      };
      root.addEventListener("wheel", stopMapWheel, { passive: true });
      body?.addEventListener("wheel", stopMapWheel, { passive: true });
    }

    function stationFingerprint(station) {
      return [
        station.callsign,
        station.grid,
        Number(station.latitude).toFixed(5),
        Number(station.longitude).toFixed(5),
        station.source,
        station.isWeather,
        station.comment,
        station.temperatureF,
        station.humidityPercent,
        station.windMph,
        station.windDegrees,
        station.gustMph,
        station.pressureMbar,
        station.rainLastHourIn,
        station.raw,
        station.heardAt,
        homeLat,
        homeLon
      ].join("\u001f");
    }

    function legendFingerprint(list) {
      // Age text changes every second — keep scroll stable by bucketing to 30s.
      return list.map((station) => {
        const heard = station.heardAt ? Date.parse(station.heardAt) : 0;
        const bucket = Number.isFinite(heard) ? Math.floor(heard / 30000) : 0;
        return [
          stationKey(station),
          sourceLabel(station),
          station.temperatureF,
          bucket,
          homeLat,
          homeLon
        ].join("\u001f");
      }).join("|");
    }

    function syncHomeMarker(lat, lon) {
      homeLat = Number.isFinite(lat) ? lat : null;
      homeLon = Number.isFinite(lon) ? lon : null;
      if (!map) return;
      if (homeLat == null || homeLon == null) {
        if (homeMarker) {
          map.removeLayer(homeMarker);
          homeMarker = null;
        }
        return;
      }

      const icon = L.divIcon({
        className: "wx-icon",
        html: '<div class="wx-marker home" title="Home">⌂</div>',
        iconSize: [30, 30],
        iconAnchor: [15, 15]
      });
      if (!homeMarker) {
        homeMarker = L.marker([homeLat, homeLon], {
          icon,
          zIndexOffset: 1000,
          riseOnHover: true
        }).addTo(map);
      } else {
        homeMarker.setLatLng([homeLat, homeLon]);
        homeMarker.setIcon(icon);
      }
      homeMarker.bindTooltip("Home", {
        direction: "top",
        offset: [0, -14],
        opacity: 0.95,
        className: "wx-tooltip",
        sticky: true
      });
      homeMarker.bindPopup('<div class="wx-popup"><strong>Home</strong><div class="wx-popup-body">'
        + escapeHtml(`${homeLat.toFixed(5)}°, ${homeLon.toFixed(5)}°`)
        + "</div></div>");
    }

    function fitAll(force) {
      if (!map) return;
      const bounds = [];
      for (const marker of markers.values())
        bounds.push(marker.getLatLng());
      if (homeMarker)
        bounds.push(homeMarker.getLatLng());
      if (bounds.length === 0) return;

      suppressUserFlag = true;
      try {
        if (bounds.length === 1)
          map.setView(bounds[0], Math.min(Math.max(map.getZoom(), 5), 9));
        else
          map.fitBounds(L.latLngBounds(bounds), { padding: [40, 40], maxZoom: 8 });
        lastFitKey = [...markers.keys()].sort().join("|") + `|h:${homeLat},${homeLon}`;
        if (force)
          userPanZoom = false;
      } finally {
        requestAnimationFrame(() => { suppressUserFlag = false; });
      }
    }

    function paintLegend(list) {
      if (!legendEl) return;
      legendEl.hidden = list.length === 0;
      legendEl.replaceChildren(...list.map((station) => {
        const li = document.createElement("li");
        const call = document.createElement("span");
        call.className = "call";
        call.textContent = station.callsign || "";
        li.append(call);
        const role = document.createElement("span");
        role.textContent = sourceLabel(station);
        li.append(role);
        if (Number.isFinite(homeLat) && Number.isFinite(homeLon)) {
          const coords = stationCoords(station);
          const miles = coords
            ? distanceMiles(homeLat, homeLon, coords.lat, coords.lon)
            : NaN;
          const dist = formatDistance(miles);
          if (dist) {
            const distEl = document.createElement("span");
            distEl.className = "dist";
            distEl.textContent = dist;
            li.append(distEl);
          }
        }
        if (Number.isFinite(station.temperatureF)) {
          const temp = document.createElement("span");
          temp.className = "temp";
          temp.textContent = `${station.temperatureF} °F`;
          li.append(temp);
        }
        const age = formatAge(station.heardAt);
        if (age) {
          const ageEl = document.createElement("span");
          ageEl.textContent = age;
          li.append(ageEl);
        }
        li.addEventListener("click", () => {
          const marker = markers.get(stationKey(station));
          if (!marker || !map) return;
          suppressUserFlag = true;
          map.setView(marker.getLatLng(), Math.max(map.getZoom(), 7));
          marker.openPopup();
          requestAnimationFrame(() => { suppressUserFlag = false; });
        });
        return li;
      }));
    }

    function paint(stations, mapLatitude, mapLongitude) {
      if (interacting) {
        pendingStations = stations;
        pendingHome = { lat: mapLatitude, lon: mapLongitude };
        return;
      }

      const list = Array.isArray(stations)
        ? stations.filter((s) => stationCoords(s) != null)
        : [];

      if (asideEl)
        asideEl.textContent = list.length === 0 ? "" : `${list.length} station${list.length === 1 ? "" : "s"}`;

      if (emptyEl) {
        emptyEl.hidden = list.length > 0;
        emptyEl.textContent = list.length > 0
          ? ""
          : "Listening for APRS weather, positions, and digimode grids (FT8, JS8, WSPR, …) on RF.";
      }

      const live = ensureMap(mapLatitude, mapLongitude);
      if (!live) {
        if (emptyEl) {
          emptyEl.hidden = false;
          emptyEl.textContent = "Map library did not load. Confirm /lib/leaflet is present, then refresh.";
        }
        if (legendEl) legendEl.hidden = true;
        return;
      }

      const foldOpen = !foldEl || foldEl.open;
      // Catch cases where the container grew after Leaflet measured it (header wrap, pop-out).
      requestSize?.();
      syncHomeMarker(mapLatitude, mapLongitude);

      const seen = new Set();
      for (const station of list) {
        const key = stationKey(station);
        if (!key) continue;
        seen.add(key);
        const fingerprint = stationFingerprint(station);
        const existing = markers.get(key);
        if (existing && markerFingerprints.get(key) === fingerprint)
          continue;

        const coords = stationCoords(station);
        if (!coords)
          continue;
        const lat = coords.lat;
        const lon = coords.lon;
        const label = markerLabel(station);
        const size = markerIconSize(label);
        const html = `<div class="${markerClass(station)}">${escapeHtml(label)}</div>`;
        const icon = L.divIcon({
          className: "wx-icon",
          html,
          iconSize: size,
          iconAnchor: [size[0] / 2, size[1] / 2]
        });
        const detail = detailHtml(station, homeLat, homeLon);
        let marker = existing;
        if (!marker) {
          marker = L.marker([lat, lon], { icon, riseOnHover: true }).addTo(live);
          markers.set(key, marker);
        } else {
          marker.setLatLng([lat, lon]);
          marker.setIcon(icon);
        }
        marker.bindPopup(detail);
        // Non-sticky + interactive: move onto the tip (and its scrollbar) without it vanishing.
        // Sticky tips follow the cursor and use pointer-events:none, so scrollbars are unreachable.
        marker.bindTooltip(detail, {
          direction: "top",
          offset: [0, -Math.ceil(size[1] / 2) - 2],
          opacity: 0.96,
          className: "wx-tooltip",
          sticky: false,
          interactive: true
        });
        marker.off("tooltipopen", onStationTooltipOpen);
        marker.on("tooltipopen", onStationTooltipOpen);
        markerFingerprints.set(key, fingerprint);
      }

      for (const [key, marker] of [...markers.entries()]) {
        if (seen.has(key)) continue;
        live.removeLayer(marker);
        markers.delete(key);
        markerFingerprints.delete(key);
      }

      const key = setKey(list);
      const shouldAutoFit = (foldOpen || popout)
        && list.length > 0
        && (key !== lastFitKey)
        && (!userPanZoom || lastFitKey === "");
      if (shouldAutoFit)
        fitAll(false);

      const legendKey = legendFingerprint(list);
      if (legendKey !== lastLegendKey) {
        lastLegendKey = legendKey;
        paintLegend(list);
      } else if (legendEl)
        legendEl.hidden = list.length === 0;
    }

    return {
      paint,
      fitAll: () => fitAll(true),
      invalidate: () => {
        requestSize?.();
        if (map) map.invalidateSize({ animate: false, pan: false });
        dayNight?.refresh();
      },
      setDayNight: (on) => dayNight?.setEnabled(!!on),
      get map() { return map; }
    };
  }

  function openPopout() {
    const features = "noopener,noreferrer,width=1200,height=900";
    const win = global.open("/map.html", "agentic-sdr-station-map", features);
    if (win) {
      try { win.focus(); } catch (_) { /* cross-window focus can throw */ }
    }
    return win;
  }

  global.StationMap = { create, openPopout };
})(typeof window !== "undefined" ? window : globalThis);
