<script lang="ts">
  import { Pause, Play, X } from '@lucide/svelte';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import { catalogPreview } from '#lib/catalog-preview.svelte.ts';
  import { cn } from '#lib/utils.ts';

  // The track being previewed from a catalog search: play/pause, the switch to the full song once
  // it has been found, and how far in it is.

  let { class: className }: { class?: string } = $props();

  const current = $derived(catalogPreview.track);
  const full = $derived(catalogPreview.full);
  const progress = $derived(
    catalogPreview.duration > 0 ? catalogPreview.position / catalogPreview.duration : 0
  );
</script>

{#if current}
  <div role="region" aria-label="Preview" class={cn('flex flex-col gap-2', className)}>
    <div class="flex items-center gap-2">
      <Button
        variant="tinted"
        class="size-11 shrink-0 rounded-full p-0 md:size-9"
        aria-label={catalogPreview.state === 'playing' ? 'Pause preview' : 'Play preview'}
        onclick={() => catalogPreview.togglePause()}
      >
        {#if catalogPreview.state === 'playing'}
          <Pause class="size-5 md:size-4" aria-hidden="true" />
        {:else if catalogPreview.state === 'loading'}
          <Spinner class="size-5 md:size-4" />
        {:else}
          <Play class="size-5 md:size-4" aria-hidden="true" />
        {/if}
      </Button>
      <div class="min-w-0 flex-1">
        <p class="text-subheadline truncate font-medium md:text-sm">{current.title}</p>
        <p class="text-footnote text-muted-foreground truncate md:text-xs">
          {#if catalogPreview.state === 'error'}
            Could not play this {catalogPreview.source === 'full' ? 'song' : 'preview'}.
          {:else if catalogPreview.source === 'clip' && full.status === 'failed'}
            {current.artist} · 30 s preview · {full.reason}
          {:else}
            {current.artist} · {catalogPreview.source === 'full' ? 'Full song' : '30 s preview'}
          {/if}
        </p>
      </div>
      {#if catalogPreview.source === 'clip'}
        {#if full.status === 'ready'}
          <Button
            variant="gray"
            class="h-11 shrink-0 rounded-full px-4 md:h-8"
            onclick={() => catalogPreview.playFull()}
          >
            Full song
          </Button>
        {:else if full.status === 'resolving'}
          <span class="text-footnote text-muted-foreground shrink-0 md:text-xs">
            Finding the full song…
          </span>
        {/if}
      {/if}
      <Button
        variant="ghost"
        class="text-muted-foreground size-11 shrink-0 rounded-full p-0 md:size-8"
        aria-label="Close preview"
        onclick={() => catalogPreview.stop()}
      >
        <X class="size-5 md:size-4" aria-hidden="true" />
      </Button>
    </div>
    <progress
      class="accent-primary h-1 w-full rounded-full"
      value={progress}
      max="1"
      aria-label="Preview position"
    ></progress>
  </div>
{/if}
