import { load } from '@tauri-apps/plugin-store';

let store = null;
let ready = false;

async function getStore() {
  if (!store) {
    store = await load('omenctl-settings.json', { autoSave: true });
    ready = true;
  }
  return store;
}

// --- defaults ---
const DEFAULTS = {
  manualPresets: [
    { cpu: 35, gpu: 35 },
    { cpu: 45, gpu: 45 },
    { cpu: 50, gpu: 50 },
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

// --- reactive state ---
export const manualPresets = $state({ value: [...DEFAULTS.manualPresets] });
export const curvePoints = $state({ value: [...DEFAULTS.curvePoints] });
export const refreshInterval = $state({ value: DEFAULTS.refreshInterval });
export const showRawJson = $state({ value: DEFAULTS.showRawJson });

// --- load from disk ---
export async function loadConfig() {
  const s = await getStore();
  manualPresets.value = (await s.get('manualPresets')) ?? [...DEFAULTS.manualPresets];
  curvePoints.value = (await s.get('curvePoints')) ?? [...DEFAULTS.curvePoints];
  refreshInterval.value = (await s.get('refreshInterval')) ?? DEFAULTS.refreshInterval;
  showRawJson.value = (await s.get('showRawJson')) ?? DEFAULTS.showRawJson;
}

// --- save helpers ---
export async function saveManualPresets() {
  const s = await getStore();
  await s.set('manualPresets', manualPresets.value);
  await s.save();
}

export async function saveCurvePoints() {
  const s = await getStore();
  await s.set('curvePoints', curvePoints.value);
  await s.save();
}

export async function saveRefreshInterval() {
  const s = await getStore();
  await s.set('refreshInterval', refreshInterval.value);
  await s.save();
}

export async function saveShowRawJson() {
  const s = await getStore();
  await s.set('showRawJson', showRawJson.value);
  await s.save();
}
