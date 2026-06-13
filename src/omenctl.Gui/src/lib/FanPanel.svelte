<script>
  let { label, fan, biosLevel } = $props();
  let level = $derived(fan?.level);
  let levelTrusted = $derived(fan?.levelTrusted ?? false);
  let rpm = $derived(fan?.rpm);
  let rpmTrusted = $derived(fan?.rpmTrusted ?? false);
  let rate = $derived(fan?.rate);
</script>

<div class="bg-[#161b22] border border-[#30363d] rounded-lg p-4">
  <div class="text-[#8b949e] text-xs uppercase tracking-wide mb-2">{label}</div>
  <div class="flex items-end gap-4">
    <div>
      <div class="text-[#8b949e] text-[10px] uppercase">Level</div>
      <div
        class="font-mono text-4xl font-bold"
        class:text-[#3fb950]={levelTrusted}
        class:text-[#d29922]={!levelTrusted}
      >
        {level != null ? level : "--"}
      </div>
      {#if !levelTrusted}
        <span
          class="text-[#8b949e] border border-[#8b949e] text-[10px] px-1.5 py-0.5 rounded uppercase mt-1 inline-block"
          >untrusted</span
        >
      {/if}
    </div>
    <div class="flex-1">
      <div class="text-[#8b949e] text-[10px] uppercase">RPM</div>
      {#if rpmTrusted && rpm != null}
        <div class="font-mono text-2xl text-[#e6edf3]">{rpm}</div>
      {:else}
        <div class="text-[#8b949e] text-sm italic">RPM unavailable</div>
      {/if}
      {#if rate != null}
        <div class="text-[#8b949e] text-[10px] mt-1">
          EC rate: {rate} <span class="text-[#8b949e]/60">(raw)</span>
        </div>
      {/if}
    </div>
  </div>
</div>
