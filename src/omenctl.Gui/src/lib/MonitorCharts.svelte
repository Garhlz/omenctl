<script>
  import { onMount, onDestroy } from 'svelte';
  import { Chart, registerables } from 'chart.js';
  import 'chartjs-adapter-date-fns';
  import { snapshot } from '../stores/agent.svelte.js';

  Chart.register(...registerables);

  const MAX_POINTS = 60; // ~3 min at 3s intervals
  let history = [];
  let cpuCanvas;
  let gpuCanvas;
  let cpuChart;
  let gpuChart;

  function pushSnapshot() {
    const s = snapshot.data;
    if (!s?.temps) return;
    const now = Date.now();
    history.push({
      time: now,
      cpuTemp: s.temps.cpu?.value ?? null,
      gpuTemp: s.temps.gpu?.value ?? null,
      cpuLevel: s.fans?.cpu?.level ?? null,
      gpuLevel: s.fans?.gpu?.level ?? null,
    });
    if (history.length > MAX_POINTS) history.shift();
    if (cpuChart) updateCharts();
  }

  function updateCharts() {
    const labels = history.map(p => p.time);
    // CPU chart
    cpuChart.data.labels = labels;
    cpuChart.data.datasets[0].data = history.map(p => p.cpuTemp);
    cpuChart.data.datasets[1].data = history.map(p => p.cpuLevel);
    cpuChart.update('none');

    // GPU chart
    gpuChart.data.labels = labels;
    gpuChart.data.datasets[0].data = history.map(p => p.gpuTemp);
    gpuChart.data.datasets[1].data = history.map(p => p.gpuLevel);
    gpuChart.update('none');
  }

  $effect(() => {
    // Trigger on new snapshot data
    if (snapshot.data) pushSnapshot();
  });

  let levelMax = $derived(snapshot.data?.deviceProfile?.manualFanLevelMax ?? 64);

  function updateLevelMax() {
    const max = levelMax;
    [cpuChart, gpuChart].forEach(c => {
      if (c) c.options.scales.yLevel.max = max;
    });
  }

  function createChart(canvas, label, tempColor, levelColor) {
    const max = snapshot.data?.deviceProfile?.manualFanLevelMax ?? 64;
    return new Chart(canvas, {
      type: 'line',
      data: {
        labels: [],
        datasets: [
          {
            label: `${label} Temp`,
            data: [],
            borderColor: tempColor,
            backgroundColor: 'transparent',
            borderWidth: 1.5,
            pointRadius: 0,
            tension: 0.2,
            yAxisID: 'yTemp',
          },
          {
            label: `${label} Level`,
            data: [],
            borderColor: levelColor,
            backgroundColor: 'transparent',
            borderWidth: 1.5,
            pointRadius: 0,
            tension: 0.2,
            yAxisID: 'yLevel',
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: { intersect: false, mode: 'nearest' },
        scales: {
          x: {
            type: 'time',
            time: { unit: 'minute', displayFormats: { minute: 'HH:mm' } },
            ticks: { color: '#8b949e', font: { size: 9 }, maxTicksLimit: 6 },
            grid: { color: '#30363d33' },
          },
          yTemp: {
            position: 'left',
            min: 25,
            title: { display: true, text: '°C', color: '#8b949e', font: { size: 10 } },
            ticks: { color: '#8b949e', font: { size: 9 }, stepSize: 10 },
            grid: { color: '#30363d33' },
          },
          yLevel: {
            position: 'right',
            min: 0,
            max,
            title: { display: true, text: 'Level', color: '#8b949e', font: { size: 10 } },
            ticks: { color: '#8b949e', font: { size: 9 }, stepSize: Math.ceil(max / 4) },
            grid: { display: false },
          },
        },
        plugins: {
          legend: {
            labels: { color: '#8b949e', font: { size: 9 }, boxWidth: 10, boxHeight: 10, padding: 6 },
          },
        },
      },
    });
  }

  onMount(() => {
    cpuChart = createChart(cpuCanvas, 'CPU', '#f85149', '#3fb950');
    gpuChart = createChart(gpuCanvas, 'GPU', '#d29922', '#58a6ff');
    // Prime with any existing history
    if (history.length > 0) updateCharts();
  });

  onDestroy(() => {
    if (cpuChart) cpuChart.destroy();
    if (gpuChart) gpuChart.destroy();
  });
</script>

<div class="grid grid-cols-2 gap-4 mb-4">
  <div class="bg-[#161b22] border border-[#30363d] rounded-lg p-3">
    <div class="text-[#8b949e] text-xs uppercase tracking-wide mb-1">CPU</div>
    <div class="relative h-36">
      <canvas bind:this={cpuCanvas}></canvas>
    </div>
  </div>
  <div class="bg-[#161b22] border border-[#30363d] rounded-lg p-3">
    <div class="text-[#8b949e] text-xs uppercase tracking-wide mb-1">GPU</div>
    <div class="relative h-36">
      <canvas bind:this={gpuCanvas}></canvas>
    </div>
  </div>
</div>
