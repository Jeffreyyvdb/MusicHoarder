<script lang="ts">
  import { CircleCheck, Pause, Play, Plus, X } from '@lucide/svelte';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import { catalogAdds } from '#lib/catalog-adds.svelte.ts';
  import { catalogPreview } from '#lib/catalog-preview.svelte.ts';
  import { cn } from '#lib/utils.ts';

  // The track being previewed from a catalog search: play/pause, Add, the switch to the full song
  // once it has been found, and how far in it is. It sits outside the result list on purpose: the
  // list takes Enter for its highlighted row, so a button inside it could not be pressed by key.

  let { class: className }: { class?: string } = $props();

  const current = $derived(catalogPreview.track);
  const track = $derived(current ? catalogAdds.track(current.deezerId) : undefined);
  const label = $derived(track ? catalogAdds.labelOf(track) : null);
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
          {:else if catalogPreview.source === 'clip' && full.status === 'resolving'}
            {current.artist} · 30 s preview · finding the full song…
          {:else}
            {current.artist} · {catalogPreview.source === 'full' ? 'Full song' : '30 s preview'}
          {/if}
        </p>
      </div>
      {#if catalogPreview.source === 'clip' && full.status === 'ready'}
        <Button
          variant="gray"
          class="h-11 shrink-0 rounded-full px-4 md:h-8"
          onclick={() => catalogPreview.playFull()}
        >
          Full song
        </Button>
      {/if}
      {#if track}
        {#if catalogAdds.isAdding(track.deezerId)}
          <span class="flex size-11 shrink-0 items-center justify-center md:size-8" role="status">
            <Spinner class="size-4" />
            <span class="sr-only">Adding {track.title}</span>
          </span>
        {:else if label}
          <span
            class="text-muted-foreground text-footnote flex shrink-0 items-center gap-1 md:text-xs"
          >
            <CircleCheck class="text-primary size-4" aria-hidden="true" />
            {label}
          </span>
        {:else}
          <Button
            variant="tinted"
            class="h-11 shrink-0 rounded-full px-4 md:h-8"
            onclick={() => void catalogAdds.add(track)}
          >
            <Plus class="size-4" aria-hidden="true" />
            Add
          </Button>
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
