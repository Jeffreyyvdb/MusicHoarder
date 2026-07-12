<script lang="ts">
  import { AlertTriangle, Check, Clapperboard, Loader2, Music } from '@lucide/svelte';
  import * as Dialog from '$lib/components/ui/dialog';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import { importTrack, resolveImportUrl, type ImportResolveResult } from '$lib/api-client';

  let { open = $bindable(false) }: { open?: boolean } = $props();

  let url = $state('');
  let resolving = $state(false);
  let resolved = $state<ImportResolveResult | null>(null);
  let title = $state('');
  let artist = $state('');
  let error = $state<string | null>(null);
  let submitting = $state(false);
  let done = $state<string | null>(null);

  // Clear transient state whenever the dialog closes so it reopens fresh. Depends only on `open`.
  $effect(() => {
    if (!open) {
      url = '';
      resolved = null;
      title = '';
      artist = '';
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
        album: r.album,
        durationMs: r.durationMs,
        coverUrl: r.coverUrl,
        spotifyTrackId: r.spotifyTrackId,
        isrc: r.isrc,
        sourceUrl: r.sourceUrl
      });
      done = res.jobStarted
        ? 'Downloading now — it’ll appear in your library once processed.'
        : 'Queued — it’ll download on the next sweep.';
      // Reset the preview so "Add another" starts clean.
      resolved = null;
      url = '';
      title = '';
      artist = '';
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
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="sm:max-w-md">
    <Dialog.Header>
      <Dialog.Title>Add from URL</Dialog.Title>
      <Dialog.Description>
        Paste a Spotify track or YouTube link to download it and add it to your library.
      </Dialog.Description>
    </Dialog.Header>

    <div class="flex flex-col gap-3">
      <div class="flex items-center gap-2">
        <Input
          bind:value={url}
          placeholder="https://open.spotify.com/track/… or youtu.be/…"
          onkeydown={onKeydown}
          disabled={resolving}
          aria-label="Track URL"
        />
        <Button variant="outline" onclick={onResolve} disabled={resolving || !url.trim()}>
          {#if resolving}
            <Loader2 class="size-4 animate-spin" />
          {:else}
            Resolve
          {/if}
        </Button>
      </div>

      {#if error}
        <div
          class="flex items-start gap-2 rounded-lg border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-[12.5px] text-amber-700 dark:text-amber-300"
        >
          <AlertTriangle class="mt-0.5 size-4 shrink-0" />
          <span>{error}</span>
        </div>
      {/if}

      {#if done}
        <div
          class="flex items-start gap-2 rounded-lg border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-[12.5px] text-emerald-700 dark:text-emerald-300"
        >
          <Check class="mt-0.5 size-4 shrink-0" />
          <span>{done}</span>
        </div>
      {/if}

      {#if resolved}
        <div class="border-border bg-card flex gap-3 rounded-lg border p-3">
          {#if resolved.coverUrl}
            <img
              src={resolved.coverUrl}
              alt=""
              class="size-16 shrink-0 rounded-md object-cover"
              referrerpolicy="no-referrer"
            />
          {:else}
            <div class="bg-muted flex size-16 shrink-0 items-center justify-center rounded-md">
              <Music class="text-muted-foreground size-6" />
            </div>
          {/if}
          <div class="flex min-w-0 flex-1 flex-col gap-2">
            <span
              class="text-muted-foreground inline-flex w-fit items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium"
            >
              {#if resolved.source === 'youtube'}
                <Clapperboard class="size-3" /> YouTube
              {:else}
                <Music class="size-3" /> Spotify
              {/if}
              {#if duration}<span class="text-muted-foreground/70">· {duration}</span>{/if}
            </span>
            <div class="flex flex-col gap-1.5">
              <Label for="import-title" class="text-[11px]">Title</Label>
              <Input id="import-title" bind:value={title} disabled={submitting} />
            </div>
            <div class="flex flex-col gap-1.5">
              <Label for="import-artist" class="text-[11px]">Artist</Label>
              <Input id="import-artist" bind:value={artist} disabled={submitting} />
            </div>
            {#if resolved.album}
              <p class="text-muted-foreground truncate text-[12px]">Album: {resolved.album}</p>
            {/if}
          </div>
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      {#if resolved}
        <Button variant="ghost" onclick={() => (resolved = null)} disabled={submitting}>Back</Button>
        <Button onclick={onConfirm} disabled={submitting || !title.trim()}>
          {#if submitting}
            <Loader2 class="size-4 animate-spin" /> Adding…
          {:else}
            Add &amp; download
          {/if}
        </Button>
      {:else}
        <Button variant="outline" onclick={() => (open = false)}>Close</Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
