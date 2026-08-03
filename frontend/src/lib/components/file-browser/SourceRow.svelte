<script lang="ts">
  import { CheckCircle2, ExternalLink } from '@lucide/svelte';
  import { cn } from '$lib/utils';

  type Props = {
    name: string;
    connected?: boolean;
    url?: string;
    label?: string;
  };
  const { name, connected, url, label }: Props = $props();
</script>

<div class="bg-secondary/50 flex items-center justify-between gap-2 rounded-lg px-3 py-2">
  <!-- Both halves may shrink: a non-shrinking name (e.g. "MusicBrainz Recording")
       next to a long URL label used to run straight over the link on a phone. -->
  <div class="flex min-w-0 flex-1 items-center gap-2">
    {#if connected}
      <CheckCircle2 class="text-primary size-4 shrink-0" />
    {:else}
      <div class="border-muted-foreground/30 size-4 shrink-0 rounded-full border-2"></div>
    {/if}
    <span class="truncate text-sm">{name}</span>
  </div>
  {#if url}
    <a
      href={url}
      target="_blank"
      rel="noopener noreferrer"
      class={cn(
        'flex min-w-0 max-w-[50%] items-center gap-1.5 text-xs transition-colors',
        connected ? 'text-primary hover:text-primary/80' : 'text-muted-foreground hover:text-foreground'
      )}
      title={url}
    >
      {#if label}<span class="truncate">{label}</span>{/if}
      <ExternalLink class="size-3 shrink-0" />
    </a>
  {/if}
</div>
