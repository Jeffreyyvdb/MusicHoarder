<script lang="ts">
  import { Sparkles } from '@lucide/svelte';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import { cn } from '$lib/utils';

  type Props = { class?: string };
  const { class: className = '' }: Props = $props();

  const isV2 = $derived(uiVersion.isV2);
</script>

<button
  type="button"
  onclick={() => uiVersion.toggle()}
  aria-pressed={isV2}
  title={isV2 ? 'Switch back to the classic design' : 'Try the new design'}
  class={cn(
    'flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] transition-colors',
    isV2
      ? 'border-transparent bg-primary/15 text-primary'
      : 'bg-surface-sunken border-border text-muted-foreground hover:text-foreground',
    className
  )}
>
  <Sparkles class="size-3.5" />
  <span class="hidden sm:inline">{isV2 ? 'New design' : 'Classic'}</span>
</button>
