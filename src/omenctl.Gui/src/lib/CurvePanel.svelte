<script>
  import {
    curveStatus,
    getCurveRunning,
    isWriting,
    lastError,
    snapshot,
    invokeTauri,
  } from "../stores/agent.svelte.js";
  import { curvePoints, saveCurvePoints } from "../stores/config.svelte.js";

  let curveRunning = $derived(getCurveRunning());

  let defaultPoints = $derived(
    snapshot.data?.deviceProfile?.recommendedCurve ?? curvePoints.value
  );
  const INTERVAL = 5;
  const HYSTERESIS = 2;

  let statusText = $state("");

  async function startCurve() {
    if (isWriting.value) return;
    isWriting.value = true;
    statusText = "Starting...";
    try {
      const raw = await invokeTauri("send_command", {
        command: JSON.stringify({
          cmd: "startCurve",
          intervalSeconds: INTERVAL,
          hysteresisC: HYSTERESIS,
          points: defaultPoints,
        }),
      });
      const resp = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (resp.ok) {
        curvePoints.value = [...defaultPoints];
        saveCurvePoints();
        curveStatus.data = resp.data;
        statusText = "Started";
      } else {
        statusText = `Error: ${resp.error?.code ?? "unknown"}`;
      }
    } catch (e) {
      statusText = `Failed: ${e}`;
    } finally {
      isWriting.value = false;
      setTimeout(() => { statusText = ""; }, 2000);
    }
  }

  async function stopCurve() {
    if (isWriting.value) return;
    isWriting.value = true;
    statusText = "Stopping...";
    try {
      const raw = await invokeTauri("send_command", {
        command: '{"cmd":"stopCurve"}',
      });
      const resp = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (resp.ok) {
        curveStatus.data = resp.data;
        statusText = "Stopped";
      } else {
        statusText = `Error: ${resp.error?.code ?? "unknown"}`;
      }
    } catch (e) {
      statusText = `Failed: ${e}`;
    } finally {
      isWriting.value = false;
      setTimeout(() => { statusText = ""; }, 2000);
    }
  }

  async function refreshCurve() {
    try {
      const raw = await invokeTauri("send_command", {
        command: '{"cmd":"curveStatus"}',
      });
      const resp = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (resp.ok) curveStatus.data = resp.data;
    } catch (e) {
      lastError.message = `curveStatus failed: ${e}`;
    }
  }

  let lastSource = $derived(curveStatus.data?.lastTemperatureSource);
  let tickCount = $derived(curveStatus.data?.tickCount ?? 0);
  let applyCount = $derived(curveStatus.data?.applyCount ?? 0);
  let lastErr = $derived(curveStatus.data?.lastError);

  let pointsText = $derived(defaultPoints.map(p => `${p.temp}°→${p.cpuLevel}/${p.gpuLevel}`).join(' · '));
</script>

<div class="bg-[#161b22] border border-[#30363d] rounded-lg p-4">
  <!-- Header -->
  <div class="flex items-center justify-between mb-3">
    <div class="flex items-center gap-2">
      <span class="text-[#8b949e] text-xs uppercase tracking-wide">Fan Curve</span>
      <span class="inline-block w-2 h-2 rounded-full"
        class:bg-[#3fb950]={curveRunning} class:bg-[#484f58]={!curveRunning}
      ></span>
    </div>
    <div class="flex gap-1.5">
      <button onclick={refreshCurve}
        class="bg-[#21262d] hover:bg-[#30363d] text-[#8b949e] border border-[#30363d] text-xs px-2 py-1 rounded transition-colors">
        Refresh
      </button>
      {#if curveRunning}
        <button onclick={stopCurve} disabled={isWriting.value}
          class="bg-[#da3633] hover:bg-[#f85149] disabled:opacity-40 text-white text-xs px-3 py-1 rounded transition-colors font-medium">
          Stop
        </button>
      {:else}
        <button onclick={startCurve} disabled={isWriting.value}
          class="bg-[#238636] hover:bg-[#2ea043] disabled:opacity-40 text-white text-xs px-3 py-1 rounded transition-colors font-medium">
          Start
        </button>
      {/if}
    </div>
  </div>

  <!-- Curve points -->
  <div class="text-[#8b949e]/60 text-[11px] font-mono mb-3">{pointsText}</div>

  <!-- Running status -->
  {#if curveStatus.data?.running}
    <div class="border-t border-[#30363d] pt-2 space-y-1.5">
      <div class="flex items-center gap-3">
        <span class="text-[#3fb950] text-xs font-medium">Running</span>
        <span class="text-[#8b949e]/50 text-xs">{INTERVAL}s · {HYSTERESIS}°C hyst</span>
        <span class="text-[#8b949e]/40 text-xs">{tickCount}t · {applyCount}w</span>
      </div>
      {#if lastSource}
        <div class="text-[#8b949e] text-xs">{lastSource}</div>
      {/if}
    </div>
  {:else if curveStatus.data}
    <div class="text-xs text-[#8b949e]/50">Curve stopped</div>
  {/if}

  {#if lastErr}
    <div class="mt-2 text-xs text-[#f85149] bg-[#da3633]/10 border border-[#f85149]/20 rounded px-2 py-1">{lastErr}</div>
  {/if}
  {#if statusText}
    <div class="text-xs mt-2" class:text-[#3fb950]={statusText === "Started" || statusText === "Stopped"} class:text-[#f85149]={statusText.startsWith("Error")} class:text-[#d29922]={statusText.endsWith("...")}>
      {statusText}
    </div>
  {/if}
</div>
