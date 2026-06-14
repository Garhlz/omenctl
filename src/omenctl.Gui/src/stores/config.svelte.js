import { load } from '@tauri-apps/plugin-store';

const CONFIG_VERSION = 1;

let store = null;

async function getStore() {
  if (!store) store = await load('omenctl-settings.json', { autoSave: true });
  return store;
}

const DEFAULTS = {
  manualPresets: [
    { cpu: 35, gpu: 35 },
    { cpu: 45, gpu: 45 },
    { cpu: 52, gpu: 52 },
    { cpu: 64, gpu: 64 },
  ],
  curvePoints: [
    { temp: 45, cpuLevel: 35, gpuLevel: 35 },
    { temp: 55, cpuLevel: 44, gpuLevel: 44 },
    { temp: 65, cpuLevel: 52, gpuLevel: 52 },
    { temp: 75, cpuLevel: 58, gpuLevel: 58 },
    { temp: 85, cpuLevel: 64, gpuLevel: 64 },
  ],
  refreshInterval: 3,
  showRawJson: true,
};

// --- validation ---
function validPreset(p) {
  return p && typeof p.cpu === 'number' && typeof p.gpu === 'number'
    && p.cpu >= 0 && p.cpu <= 64 && p.gpu >= 0 && p.gpu <= 64;
}

function validCurvePoint(pt) {
  return pt && typeof pt.temp === 'number' && typeof pt.cpuLevel === 'number' && typeof pt.gpuLevel === 'number'
    && pt.cpuLevel >= 0 && pt.cpuLevel <= 64 && pt.gpuLevel >= 0 && pt.gpuLevel <= 64;
}

function validCurvePoints(pts) {
  if (!Array.isArray(pts) || pts.length === 0) return false;
  const sorted = [...pts].sort((a, b) => a.temp - b.temp);
  // Temps must be strictly ascending
  for (let i = 1; i < sorted.length; i++) {
    if (sorted[i].temp <= sorted[i - 1].temp) return false;
  }
  return pts.every(validCurvePoint);
}

// --- reactive state ---
export const manualPresets = $state({ value: [...DEFAULTS.manualPresets] });
export const curvePoints = $state({ value: [...DEFAULTS.curvePoints] });
export const refreshInterval = $state({ value: DEFAULTS.refreshInterval });
export const showRawJson = $state({ value: DEFAULTS.showRawJson });

// --- load from disk with validation ---
export async function loadConfig() {
  const s = await getStore();
  try {
    const version = await s.get('configVersion');
    if (version !== CONFIG_VERSION) {
      await resetToDefaults(s);
      return;
    }

    const presets = await s.get('manualPresets');
    if (Array.isArray(presets) && presets.length > 0 && presets.every(validPreset)) {
      manualPresets.value = presets;
    } else {
      manualPresets.value = [...DEFAULTS.manualPresets];
    }

    const curves = await s.get('curvePoints');
    if (validCurvePoints(curves)) {
      curvePoints.value = curves;
    } else {
      curvePoints.value = [...DEFAULTS.curvePoints];
    }

    const interval = await s.get('refreshInterval');
    refreshInterval.value = (typeof interval === 'number' && interval >= 1 && interval <= 30)
      ? interval : DEFAULTS.refreshInterval;

    const show = await s.get('showRawJson');
    showRawJson.value = (typeof show === 'boolean') ? show : DEFAULTS.showRawJson;
  } catch (_) {
    // Corrupted store — reset
    await resetToDefaults(s);
  }
}

async function resetToDefaults(s) {
  await s.set('configVersion', CONFIG_VERSION);
  await s.set('manualPresets', DEFAULTS.manualPresets);
  await s.set('curvePoints', DEFAULTS.curvePoints);
  await s.set('refreshInterval', DEFAULTS.refreshInterval);
  await s.set('showRawJson', DEFAULTS.showRawJson);
  await s.save();
  manualPresets.value = [...DEFAULTS.manualPresets];
  curvePoints.value = [...DEFAULTS.curvePoints];
  refreshInterval.value = DEFAULTS.refreshInterval;
  showRawJson.value = DEFAULTS.showRawJson;
}

// --- save helpers ---
export async function saveManualPresets() {
  const s = await getStore();
  await s.set('configVersion', CONFIG_VERSION);
  await s.set('manualPresets', manualPresets.value);
  await s.save();
}

export async function saveCurvePoints() {
  const s = await getStore();
  await s.set('configVersion', CONFIG_VERSION);
  await s.set('curvePoints', curvePoints.value);
  await s.save();
}

export async function saveRefreshInterval() {
  const s = await getStore();
  await s.set('configVersion', CONFIG_VERSION);
  await s.set('refreshInterval', refreshInterval.value);
  await s.save();
}

export async function saveShowRawJson() {
  const s = await getStore();
  await s.set('configVersion', CONFIG_VERSION);
  await s.set('showRawJson', showRawJson.value);
  await s.save();
}
