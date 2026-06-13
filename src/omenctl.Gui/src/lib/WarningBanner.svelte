<script>
  let { warnings = [] } = $props();
  import { warningLabel } from '../stores/agent.svelte.js';
</script>

{#if warnings.length > 0}
  <div class="bg-[#d29922]/10 border-b border-[#d29922]/30 px-6 py-2 flex flex-wrap gap-2">
    <span class="text-[#d29922] text-xs font-semibold uppercase tracking-wide">Warnings:</span>
    {#each warnings as w}
      <span class="text-[#d29922] border border-[#d29922]/30 bg-[#d29922]/10 text-xs px-2 py-0.5 rounded">
        {warningLabel(w)}
      </span>
    {/each}
  </div>
{/if}
