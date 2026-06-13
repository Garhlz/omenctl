<script>
  let {
    label,
    value,
    unit = "",
    source = "",
    trusted = true,
    suspect = false,
    kind = "default",
  } = $props();
  let colorValue = $derived(
    kind === "temperature" ? tempColor(value) : "#e6edf3",
  );
  import { tempColor } from "../stores/agent.svelte.js";
</script>

<div class="bg-[#161b22] border border-[#30363d] rounded-lg p-4">
  <div class="text-[#8b949e] text-xs uppercase tracking-wide mb-1">{label}</div>
  <div class="font-mono text-4xl font-bold" style="color: {colorValue}">
    {value != null ? value.toFixed(1) : "--"}
  </div>
  <div class="text-[#8b949e] text-xs mt-1">
    {unit}{source ? " · " + source : ""}
  </div>
  <div class="flex gap-1 mt-1.5">
    {#if suspect}
      <span
        class="text-[#d29922] border border-[#d29922] text-[10px] px-1.5 py-0.5 rounded uppercase tracking-wide font-semibold"
        >suspect</span
      >
    {/if}
    {#if !trusted}
      <span
        class="text-[#8b949e] border border-[#8b949e] text-[10px] px-1.5 py-0.5 rounded uppercase tracking-wide font-semibold"
        >untrusted</span
      >
    {/if}
  </div>
</div>
