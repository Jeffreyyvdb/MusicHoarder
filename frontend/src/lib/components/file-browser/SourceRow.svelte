<script lang="ts">
  import { CheckCircle2, ExternalLink } from '@lucide/svelte';
  import { cn } from '$lib/utils';

  type Props = {
    name: string;
    connected?: boolean;
    url: string;
    label?: string;
  };
  const { name, connected, url, label }: Props = $props();
</script>

<div class="bg-secondary/50 flex items-center justify-between gap-2 rounded-lg px-3 py-2">
  <div class="flex min-w-0 items-center gap-2">
    {#if connected}
      <CheckCircle2 class="text-primary size-4 shrink-0" />
    {:else}
      <div class="border-muted-foreground/30 size-4 shrink-0 rounded-full border-2"></div>
    {/if}
    <span class="shrink-0 text-sm">{name}</span>
  </div>
  <a
    href={url}
    target="_blank"
    rel="noopener noreferrer"
    class={cn(
      'flex min-w-0 items-center gap-1.5 text-xs transition-colors',
      connected ? 'text-primary hover:text-primary/80' : 'text-muted-foreground hover:text-foreground'
    )}
    title={url}
  >
    {#if label}<span class="truncate">{label}</span>{/if}
    <ExternalLink class="size-3 shrink-0" />
  </a>
</div>
