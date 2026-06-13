<script>
  import { isWriting, invokeTauri } from "../stores/agent.svelte.js";

  let { curveRunning = false } = $props();

  let statusText = $state("");

  async function sendCmd(cmdObj, confirmNeeded = false) {
    if (isWriting.value) return;
    if (confirmNeeded && curveRunning) {
      if (
        !window.confirm(
          "Fan curve is currently running. Stop the curve before using manual controls?",
        )
      )
        return;
      isWriting.value = true;
      statusText = "Stopping curve...";
      try {
        await invokeTauri("send_command", { command: '{"cmd":"stopCurve"}' });
      } catch (e) {
        statusText = `Failed to stop curve: ${e}`;
        isWriting.value = false;
        return;
      }
    }
    isWriting.value = true;
    statusText = "Writing...";
    try {
      const raw = await invokeTauri("send_command", {
        command: JSON.stringify(cmdObj),
      });
      const resp = typeof raw === "string" ? JSON.parse(raw) : raw;
      if (!resp.ok) {
        statusText = `Error: ${resp.error?.code ?? "unknown"}`;
      } else {
        statusText = "OK";
      }
    } catch (e) {
      statusText = `Failed: ${e}`;
    } finally {
      isWriting.value = false;
      setTimeout(() => {
        statusText = "";
      }, 2000);
    }
  }
</script>

<div class="bg-[#161b22] border border-[#30363d] rounded-lg p-4">
  <div class="text-[#8b949e] text-xs uppercase tracking-wide mb-3">
    Fan Controls
  </div>
  <div class="flex flex-wrap gap-2">
    <button
      onclick={() =>
        sendCmd({ cmd: "setManual", cpuLevel: 35, gpuLevel: 35 }, true)}
      disabled={isWriting.value}
      class="bg-[#21262d] hover:bg-[#30363d] disabled:opacity-40 disabled:cursor-not-allowed text-[#c9d1d9] border border-[#30363d] text-sm px-4 py-2 rounded-md transition-colors"
    >
      Manual 35/35
    </button>
    <button
      onclick={() =>
        sendCmd({ cmd: "setManual", cpuLevel: 45, gpuLevel: 45 }, true)}
      disabled={isWriting.value}
      class="bg-[#21262d] hover:bg-[#30363d] disabled:opacity-40 disabled:cursor-not-allowed text-[#c9d1d9] border border-[#30363d] text-sm px-4 py-2 rounded-md transition-colors"
    >
      Manual 45/45
    </button>
    <button
      onclick={() =>
        sendCmd({ cmd: "setManual", cpuLevel: 50, gpuLevel: 50 }, true)}
      disabled={isWriting.value}
      class="bg-[#21262d] hover:bg-[#30363d] disabled:opacity-40 disabled:cursor-not-allowed text-[#c9d1d9] border border-[#30363d] text-sm px-4 py-2 rounded-md transition-colors"
    >
      Manual 50/50
    </button>
    <button
      onclick={() => sendCmd({ cmd: "setMax" }, true)}
      disabled={isWriting.value}
      class="bg-[#da3633]/20 hover:bg-[#da3633]/40 disabled:opacity-40 disabled:cursor-not-allowed text-[#f85149] border border-[#f85149]/30 text-sm px-4 py-2 rounded-md transition-colors font-semibold"
    >
      Max
    </button>
    <button
      onclick={() => sendCmd({ cmd: "setProgram", name: "Power" })}
      disabled={isWriting.value}
      class="bg-[#1f6feb]/20 hover:bg-[#1f6feb]/40 disabled:opacity-40 disabled:cursor-not-allowed text-[#58a6ff] border border-[#58a6ff]/30 text-sm px-4 py-2 rounded-md transition-colors"
    >
      Power
    </button>
    <button
      onclick={() => sendCmd({ cmd: "setProgram", name: "Silent" })}
      disabled={isWriting.value}
      class="bg-[#1f6feb]/20 hover:bg-[#1f6feb]/40 disabled:opacity-40 disabled:cursor-not-allowed text-[#58a6ff] border border-[#58a6ff]/30 text-sm px-4 py-2 rounded-md transition-colors"
    >
      Silent
    </button>
  </div>
  {#if statusText}
    <div
      class="text-xs mt-2"
      class:text-[#3fb950]={statusText === "OK"}
      class:text-[#f85149]={statusText.startsWith("Error") ||
        statusText.startsWith("Failed")}
      class:text-[#d29922]={statusText === "Writing..."}
    >
      {statusText}
    </div>
  {/if}
</div>
