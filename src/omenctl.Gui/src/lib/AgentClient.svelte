<script>
  import WarningBanner from './WarningBanner.svelte';
  import SensorCard from './SensorCard.svelte';
  import FanPanel from './FanPanel.svelte';
  import ControlButtons from './ControlButtons.svelte';
  import RawJsonPanel from './RawJsonPanel.svelte';
  import { agentStatus, snapshot, isWriting, lastError, invokeTauri } from '../stores/agent.svelte.js';

  let statusMessage = $state('Not started');
  let showStart = $derived(agentStatus.value === 'stopped');
  let showStop = $derived(agentStatus.value === 'running');
  let pollTimer = null;

  async function handleStart() {
    statusMessage = 'Starting...';
    try {
      await invokeTauri('start_agent');
      agentStatus.value = 'running';
      statusMessage = 'Running';
      await refreshSnapshot();
      startPolling();
    } catch (e) {
      agentStatus.value = 'error';
      statusMessage = 'Start failed';
      lastError.message = String(e);
    }
  }

  async function handleStop() {
    stopPolling();
    statusMessage = 'Stopping...';
    try {
      await invokeTauri('stop_agent');
      agentStatus.value = 'stopped';
      statusMessage = 'Stopped';
    } catch (e) {
      statusMessage = 'Stop failed';
      lastError.message = String(e);
    }
  }

  async function refreshSnapshot() {
    try {
      const raw = await invokeTauri('send_command', { command: '{"cmd":"snapshot"}' });
      const resp = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (resp.ok) {
        snapshot.data = resp.data;
        lastError.code = '';
        lastError.message = '';
      } else {
        lastError.code = resp.error?.code ?? '';
        lastError.message = resp.error?.message ?? 'Unknown error';
      }
    } catch (e) {
      lastError.code = 'connection_error';
      lastError.message = String(e);
      agentStatus.value = 'error';
      statusMessage = 'Agent connection lost';
      stopPolling();
    }
  }

  function startPolling() {
    stopPolling();
    pollTimer = setInterval(refreshSnapshot, 3000);
  }

  function stopPolling() {
    if (pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }

  async function copyJson() {
    if (!snapshot.data) return;
    try {
      await navigator.clipboard.writeText(JSON.stringify(snapshot.data, null, 2));
      statusMessage = 'Copied!';
      setTimeout(() => { statusMessage = agentStatus.value === 'running' ? 'Running' : 'Stopped'; }, 1500);
    } catch (_) {
      statusMessage = 'Copy failed';
    }
  }

  let deviceName = $derived(snapshot.data?.product ?? '--');
  let profileId = $derived(snapshot.data?.deviceProfile?.id ?? '--');
</script>

<div class="min-h-screen bg-[#0d1117] flex flex-col">
  <!-- Header -->
  <header class="border-b border-[#30363d] px-6 py-3 flex items-center gap-4 shrink-0">
    <h1 class="font-mono text-xl font-bold text-[#58a6ff] tracking-tight">omenctl</h1>
    <span class="inline-flex items-center gap-2 text-sm">
      <span class="inline-block w-2.5 h-2.5 rounded-full"
        class:bg-[#3fb950]={agentStatus.value === 'running'}
        class:bg-[#d29922]={agentStatus.value === 'starting'}
        class:bg-[#f85149]={agentStatus.value === 'error'}
        class:bg-[#484f58]={agentStatus.value === 'stopped'}
      ></span>
      {statusMessage}
    </span>
    <span class="text-[#8b949e] text-xs ml-4">Device: {deviceName} | Profile: {profileId}</span>
    <div class="ml-auto flex gap-2">
      {#if showStart}
        <button onclick={handleStart}
          class="bg-[#238636] hover:bg-[#2ea043] text-white text-sm px-4 py-1.5 rounded-md transition-colors">
          Start Agent
        </button>
      {/if}
      {#if showStop}
        <button onclick={handleStop}
          class="bg-[#da3633] hover:bg-[#f85149] text-white text-sm px-4 py-1.5 rounded-md transition-colors">
          Stop Agent
        </button>
      {/if}
      <button onclick={copyJson}
        class="bg-[#21262d] hover:bg-[#30363d] text-[#c9d1d9] border border-[#30363d] text-sm px-4 py-1.5 rounded-md transition-colors">
        Copy JSON
      </button>
    </div>
  </header>

  <!-- Error Banner -->
  {#if lastError.message}
    <div class="bg-[#da3633]/20 border-b border-[#f85149]/30 px-6 py-2 text-sm text-[#f85149]">
      <span class="font-semibold">{lastError.code || 'Error'}:</span> {lastError.message}
    </div>
  {/if}

  <!-- Warning Banner -->
  <WarningBanner warnings={snapshot.data?.warnings ?? []} />

  <!-- Main Content -->
  <main class="flex-1 overflow-y-auto p-6">
    {#if snapshot.data}
      <!-- Sensors -->
      <section class="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
        <SensorCard
          label="CPU Temperature" value={snapshot.data.temps?.cpu?.value}
          unit={snapshot.data.temps?.cpu?.unit} source={snapshot.data.temps?.cpu?.source}
          trusted={snapshot.data.temps?.cpu?.trusted} suspect={snapshot.data.temps?.cpu?.suspect}
          kind="temperature"
        />
        <SensorCard
          label="GPU Temperature" value={snapshot.data.temps?.gpu?.value}
          unit={snapshot.data.temps?.gpu?.unit} source={snapshot.data.temps?.gpu?.source}
          trusted={snapshot.data.temps?.gpu?.trusted} suspect={snapshot.data.temps?.gpu?.suspect}
          kind="temperature"
        />
        <SensorCard
          label="CPU Load" value={snapshot.data.loads?.cpu?.value}
          unit={snapshot.data.loads?.cpu?.unit} source={snapshot.data.loads?.cpu?.source}
          trusted={snapshot.data.loads?.cpu?.trusted} suspect={snapshot.data.loads?.cpu?.suspect}
        />
        <SensorCard
          label="GPU Load" value={snapshot.data.loads?.gpu?.value}
          unit={snapshot.data.loads?.gpu?.unit} source={snapshot.data.loads?.gpu?.source}
          trusted={snapshot.data.loads?.gpu?.trusted} suspect={snapshot.data.loads?.gpu?.suspect}
        />
      </section>

      <section class="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
        {#if snapshot.data.loads?.memory}
          <SensorCard
            label="System Memory" value={snapshot.data.loads.memory.value}
            unit={snapshot.data.loads.memory.unit} source={snapshot.data.loads.memory.source}
            trusted={snapshot.data.loads.memory.trusted} suspect={snapshot.data.loads.memory.suspect}
          />
        {/if}
        {#if snapshot.data.raw?.sensors?.gpuPowerW}
          <SensorCard
            label="GPU Power" value={snapshot.data.raw.sensors.gpuPowerW.value}
            unit={snapshot.data.raw.sensors.gpuPowerW.unit}
            source={snapshot.data.raw.sensors.gpuPowerW.source}
            trusted={snapshot.data.raw.sensors.gpuPowerW.trusted}
            suspect={snapshot.data.raw.sensors.gpuPowerW.suspect}
          />
        {/if}
        {#if snapshot.data.raw?.sensors?.gpuCoreClockMHz}
          <SensorCard
            label="GPU Core Clock" value={snapshot.data.raw.sensors.gpuCoreClockMHz.value}
            unit={snapshot.data.raw.sensors.gpuCoreClockMHz.unit}
            source={snapshot.data.raw.sensors.gpuCoreClockMHz.source}
            trusted={snapshot.data.raw.sensors.gpuCoreClockMHz.trusted}
            suspect={snapshot.data.raw.sensors.gpuCoreClockMHz.suspect}
          />
        {/if}
        {#if snapshot.data.raw?.sensors?.gpuMemoryUsedMiB}
          {@const mem = snapshot.data.raw.sensors.gpuMemoryUsedMiB}
          {@const tot = snapshot.data.raw.sensors.gpuMemoryTotalMiB}
          <SensorCard
            label="GPU Memory" value={mem.value}
            unit={mem.unit}
            source={`${(mem.value / tot.value * 100).toFixed(1)}% of ${tot.value} MiB`}
            trusted={mem.trusted} suspect={mem.suspect}
          />
        {/if}
      </section>

      <!-- Fans -->
      <section class="grid grid-cols-2 gap-4 mb-4">
        <FanPanel
          label="CPU Fan"
          fan={snapshot.data.fans?.cpu}
          biosLevel={snapshot.data.biosFan?.level}
        />
        <FanPanel
          label="GPU Fan"
          fan={snapshot.data.fans?.gpu}
          biosLevel={snapshot.data.biosFan?.level}
        />
      </section>

      <!-- Controls + Raw JSON -->
      <section class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <ControlButtons />
        <RawJsonPanel json={snapshot.data} />
      </section>
    {:else}
      <div class="flex items-center justify-center h-64 text-[#8b949e] text-lg">
        <div class="text-center">
          <div class="font-mono text-6xl mb-4">◈</div>
          <p>Start the agent to view hardware status</p>
        </div>
      </div>
    {/if}
  </main>
</div>
