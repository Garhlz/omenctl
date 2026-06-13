<script>
  let { json } = $props();
  let formatted = $derived(json ? JSON.stringify(json, null, 2) : "No data");
</script>

<div class="bg-[#0d1117] border border-[#30363d] rounded-lg p-4">
  <div class="text-[#8b949e] text-xs uppercase tracking-wide mb-2">
    Raw Response
  </div>
  <pre
    class="text-xs font-mono text-[#c9d1d9] max-h-80 overflow-y-auto whitespace-pre-wrap">{formatted}</pre>
</div>
