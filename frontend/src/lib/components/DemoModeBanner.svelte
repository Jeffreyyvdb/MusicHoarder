<script lang="ts">
  import { isDemoMode } from '$lib/app-mode';
</script>

{#if isDemoMode}
  <div
    role="status"
    aria-label="Demo mode"
    class="pointer-events-none fixed bottom-3 left-3 z-[60] flex items-center gap-1.5 rounded-full border border-amber-500/40 bg-amber-500/15 px-3 py-1 text-[11px] font-medium text-amber-700 shadow-sm backdrop-blur dark:text-amber-400"
  >
    <span class="size-1.5 rounded-full bg-amber-500" aria-hidden="true"></span>
    <span>Demo mode — sample data</span>
  </div>
{/if}
