import { invoke } from '@tauri-apps/api/core';

export const agentStatus = $state({ value: 'stopped' });
export const snapshot = $state({ data: null });
export const curveStatus = $state({ data: null });
export const isWriting = $state({ value: false });
export const lastError = $state({ code: '', message: '' });

export function getCurveRunning() {
  return curveStatus.data?.running ?? false;
}

export async function invokeTauri(cmd, args = {}) {
  try {
    return await invoke(cmd, args);
  } catch (e) {
    if (typeof e === 'string') {
      try { return JSON.parse(e); } catch (_) { throw new Error(e); }
    }
    throw e;
  }
}

const WARNING_LABELS = {
  'bios_unavailable': 'BIOS 不可用',
  'bios_error': 'BIOS 错误',
  'product_unavailable': '无法读取产品信息',
  'bios_fan_level_unavailable': 'BIOS 风扇 level 不可用',
  'bios_fan_count_unavailable': 'BIOS 风扇数量不可用',
  'bios_fan_type_unavailable': 'BIOS 风扇类型不可用',
  'bios_max_fan_unavailable': 'BIOS 最大风扇信息不可用',
  'bios_temperature_unavailable': 'BIOS 温度不可用',
  'ec_unavailable': 'EC 不可用',
  'hardware_controller_not_wired': '硬件控制器未连接',
  'cpu_temp_unavailable': 'CPU 温度不可用',
  'cpu_temp_bios_fallback': 'CPU 温度来自 BIOS 回退',
  'cpu_temp_suspect': 'CPU 温度存疑',
  'gpu_temp_unavailable': 'GPU 温度不可用',
  'gpu_temp_suspect': 'GPU 温度存疑',
  'rpm_unavailable': 'RPM 不可用',
  'lhm_unavailable': 'LibreHardwareMonitor 不可用',
  'nvml_unavailable': 'NVML 不可用',
  'nvml_no_device': '未检测到 NVIDIA GPU',
};

export function warningLabel(code) {
  if (code.startsWith('bios_error:')) return `BIOS 错误: ${code.slice(10)}`;
  if (code.startsWith('ec_error:')) return `EC 错误: ${code.slice(9)}`;
  if (code.startsWith('ec_read_error:')) return `EC 读取错误: ${code.slice(14)}`;
  if (code.startsWith('product_error:')) return `产品信息错误: ${code.slice(14)}`;
  return WARNING_LABELS[code] ?? code;
}

export function tempColor(value) {
  if (value == null) return '#8b949e';
  if (value > 85) return '#f85149';
  if (value > 75) return '#f0883e';
  if (value > 60) return '#d29922';
  if (value > 45) return '#7ee787';
  return '#3fb950';
}
