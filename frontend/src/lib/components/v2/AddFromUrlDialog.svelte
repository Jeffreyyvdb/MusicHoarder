<script lang="ts">
  import { AlertTriangle, CircleCheck, Clapperboard, Loader2, Music } from '@lucide/svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Switch } from '$lib/components/ui/switch';
  import { importTrack, resolveImportUrl, type ImportResolveResult } from '$lib/api-client';

  let { open = $bindable(false) }: { open?: boolean } = $props();

  // One BottomSheet at every width: a bottom sheet with a grabber below md (four fields in a
  // vertically-centred box used to end up under the iOS keyboard with nowhere to scroll), a centred
  // dialog above it. iOS sheet conventions: Cancel leading, the one prominent action — Add —
  // trailing, and the form as inset-grouped cells with 16px fields so iOS never zooms on focus.
  const blurb = 'Paste a Spotify track or YouTube link to download it and add it to your library.';

  let url = $state('');
  let resolving = $state(false);
  let resolved = $state<ImportResolveResult | null>(null);
  let title = $state('');
  let artist = $state('');
  let album = $state('');
  let error = $state<string | null>(null);
  let submitting = $state(false);
  let done = $state<string | null>(null);
  let downloadVideo = $state(false);
  let urlInput = $state<HTMLInputElement | null>(null);

  // Clear transient state whenever the sheet closes so it reopens fresh. Depends only on `open`.
  $effect(() => {
    if (!open) {
      url = '';
      resolved = null;
      title = '';
      artist = '';
      album = '';
      error = null;
      done = null;
      resolving = false;
      submitting = false;
    }
  });

  async function onResolve() {
    const u = url.trim();
    if (!u) return;
    resolving = true;
    error = null;
    resolved = null;
    done = null;
    try {
      const r = await resolveImportUrl(u);
      resolved = r;
      title = r.title;
      artist = r.artist;
      // Pre-filled with the release the source knows, or (for a plain YouTube video) the single name
      // the track would be filed under — so the album folder is never a surprise "Unknown Album".
      album = r.album ?? r.title;
      // A pasted YouTube link IS the video — default the clip download on there; Spotify
      // imports would fetch a video by search, so leave that an explicit opt-in.
      downloadVideo = r.source === 'youtube';
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not resolve that link.';
    } finally {
      resolving = false;
    }
  }

  async function onConfirm() {
    const r = resolved;
    if (!r) return;
    if (!title.trim()) {
      error = 'A title is required.';
      return;
    }
    submitting = true;
    error = null;
    try {
      const res = await importTrack({
        source: r.source,
        title: title.trim(),
        artist: artist.trim(),
        album: album.trim() || undefined,
        durationMs: r.durationMs,
        coverUrl: r.coverUrl,
        spotifyTrackId: r.spotifyTrackId,
        isrc: r.isrc,
        sourceUrl: r.sourceUrl,
        downloadMusicVideo: downloadVideo
      });
      done = res.jobStarted
        ? 'Downloading now — it’ll appear in your library once processed.'
        : 'Queued — it’ll download on the next sweep.';
      // Reset the preview so another link starts clean.
      resolved = null;
      url = '';
      title = '';
      artist = '';
      album = '';
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not queue the download.';
    } finally {
      submitting = false;
    }
  }

  function onKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !resolving && url.trim()) {
      e.preventDefault();
      void onResolve();
    }
  }

  function formatDuration(ms: number): string | null {
    if (!ms || ms <= 0) return null;
    const total = Math.round(ms / 1000);
    const m = Math.floor(total / 60);
    const s = total % 60;
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  const duration = $derived(resolved ? formatDuration(resolved.durationMs) : null);
  const canAdd = $derived(resolved != null && !submitting && title.trim().length > 0);

  // A borderless field that fills its cell, as in an iOS form; the caret is its focus indicator.
  const cellInput =
    'placeholder:text-muted-foreground text-body min-w-0 flex-1 bg-transparent py-2.5 outline-none disabled:opacity-50 md:py-1.5 md:text-sm';
  const cellLabel = 'text-body w-16 shrink-0 md:text-sm';
</script>

{#snippet status()}
  {#if error}
    <span role="alert" class="text-destructive-text flex items-start gap-1.5">
      <AlertTriangle class="mt-px size-4 shrink-0" />
      <span>{error}</span>
    </span>
  {:else if done}
    <span role="status" class="text-foreground flex items-start gap-1.5">
      <CircleCheck class="text-primary mt-px size-4 shrink-0" />
      <span>{done}</span>
    </span>
  {/if}
{/snippet}

<BottomSheet.Root
  bind:open
  title="Add from link"
  description={blurb}
  onOpenAutoFocus={(e) => {
    // Straight into the link field: the whole point of the sheet is pasting one.
    e.preventDefault();
    urlInput?.focus();
  }}
>
  {#snippet leading()}
    <BottomSheet.Action onclick={() => (open = false)}
      >{done ? 'Close' : 'Cancel'}</BottomSheet.Action
    >
  {/snippet}
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={onConfirm} disabled={!canAdd}>
      {#if submitting}
        <Loader2 class="size-4 animate-spin" /> Adding…
      {:else}
        Add
      {/if}
    </BottomSheet.Action>
  {/snippet}

  <div class="flex flex-col gap-7 pt-1">
    <GroupedList.Section footer={error || done ? status : undefined}>
      <GroupedList.Row>
        <span class="flex items-center gap-2">
          <input
            bind:this={urlInput}
            bind:value={url}
            type="url"
            inputmode="url"
            autocapitalize="off"
            autocorrect="off"
            spellcheck={false}
            enterkeyhint="go"
            class={cellInput}
            placeholder="Spotify or YouTube link"
            onkeydown={onKeydown}
            disabled={resolving}
            aria-label="Track link"
          />
          <BottomSheet.Action
            onclick={onResolve}
            disabled={resolving || !url.trim()}
            class="shrink-0"
          >
            {#if resolving}
              <Loader2 class="size-4 animate-spin" />
              <span class="sr-only">Resolving…</span>
            {:else}
              Resolve
            {/if}
          </BottomSheet.Action>
        </span>
      </GroupedList.Row>
    </GroupedList.Section>

    {#if resolved}
      <GroupedList.Section
        header="Details"
        footer="The album names the folder and its cover. Left blank, the track is filed as a single named after itself."
      >
        <GroupedList.Row>
          {#snippet leading()}
            {#if resolved?.coverUrl}
              <img
                src={resolved.coverUrl}
                alt=""
                class="size-14 shrink-0 rounded-md object-cover"
                referrerpolicy="no-referrer"
              />
            {:else}
              <span class="bg-muted flex size-14 shrink-0 items-center justify-center rounded-md">
                <Music class="text-muted-foreground size-6" />
              </span>
            {/if}
          {/snippet}
          <span class="text-subheadline text-muted-foreground flex items-center gap-1.5 md:text-sm">
            {#if resolved.source === 'youtube'}
              <Clapperboard class="size-4" /> YouTube
            {:else}
              <Music class="size-4" /> Spotify
            {/if}
            {#if duration}<span class="tabular-nums">· {duration}</span>{/if}
          </span>
        </GroupedList.Row>
        <GroupedList.Row>
          <span class="flex items-center gap-3">
            <label for="import-title" class={cellLabel}>Title</label>
            <input
              id="import-title"
              class={cellInput}
              bind:value={title}
              disabled={submitting}
              autocorrect="off"
              spellcheck={false}
            />
          </span>
        </GroupedList.Row>
        <GroupedList.Row>
          <span class="flex items-center gap-3">
            <label for="import-artist" class={cellLabel}>Artist</label>
            <input
              id="import-artist"
              class={cellInput}
              bind:value={artist}
              disabled={submitting}
              autocorrect="off"
              spellcheck={false}
            />
          </span>
        </GroupedList.Row>
        <GroupedList.Row>
          <span class="flex items-center gap-3">
            <label for="import-album" class={cellLabel}>Album</label>
            <input
              id="import-album"
              class={cellInput}
              bind:value={album}
              placeholder={title || 'Album'}
              disabled={submitting}
              autocorrect="off"
              spellcheck={false}
            />
          </span>
        </GroupedList.Row>
      </GroupedList.Section>

      <GroupedList.Section>
        <GroupedList.Row
          label="Also download the music video"
          sublabel="Plays muted behind the full-screen player, synced to the song."
        >
          {#snippet trailing()}
            <Switch
              checked={downloadVideo}
              onCheckedChange={(v: boolean) => (downloadVideo = v)}
              disabled={submitting}
              aria-label="Also download the music video"
            />
          {/snippet}
        </GroupedList.Row>
      </GroupedList.Section>
    {/if}
  </div>
</BottomSheet.Root>
