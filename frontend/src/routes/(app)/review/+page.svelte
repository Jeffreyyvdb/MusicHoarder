<script lang="ts">
  import { Card, CardContent } from '$lib/components/ui/card';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tabs from '$lib/components/ui/tabs';
  import { Label } from '$lib/components/ui/label';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import TrackListItem from '$lib/components/review/TrackListItem.svelte';
  import SourceLinks from '$lib/components/review/SourceLinks.svelte';
  import CompareRow from '$lib/components/review/CompareRow.svelte';
  import {
    Check,
    ChevronLeft,
    ChevronRight,
    AlertTriangle,
    Fingerprint,
    SkipForward,
    Trash2,
    Save,
    RefreshCw,
    Loader2,
    CheckCheck,
    X,
    Play,
    Pause
  } from '@lucide/svelte';
  import type { ApiSong } from '$lib/api-client';
  import {
    fetchReviewTracks,
    submitManualReview,
    softDeleteSong,
    bulkApprove,
    getSongStreamUrl
  } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';

  type MetadataEdits = {
    artist?: string;
    albumArtist?: string;
    album?: string;
    title?: string;
    year?: number;
    trackNumber?: number;
  };

  let tracks = $state<ApiSong[]>([]);
  let selectedIndex = $state(0);
  let editedMetadata = $state<Record<number, MetadataEdits>>({});
  let loading = $state(true);
  let actionLoading = $state(false);
  let error = $state<string | null>(null);
  let rejectReason = $state('');
  let bulkApproveMinConfidence = $state(0.75);
  let bulkApproveResult = $state<{ count: number } | null>(null);

  async function loadTracks() {
    try {
      loading = true;
      error = null;
      const songs = await fetchReviewTracks();
      tracks = songs;
      selectedIndex = 0;
      editedMetadata = {};
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load tracks';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void loadTracks();
  });

  const selectedTrack = $derived(tracks[selectedIndex]);
  const currentEdits = $derived<MetadataEdits>(
    selectedTrack ? (editedMetadata[selectedTrack.id] ?? {}) : {}
  );

  function handleNext() {
    if (selectedIndex < tracks.length - 1) {
      selectedIndex += 1;
      rejectReason = '';
    }
  }

  function handlePrev() {
    if (selectedIndex > 0) {
      selectedIndex -= 1;
      rejectReason = '';
    }
  }

  function handleTrackSelect(index: number) {
    selectedIndex = index;
    rejectReason = '';
  }

  function handleAcceptOriginal() {
    if (!selectedTrack?.originalMetadataCaptured) return;
    editedMetadata = {
      ...editedMetadata,
      [selectedTrack.id]: {
        ...editedMetadata[selectedTrack.id],
        artist: selectedTrack.originalArtist ?? undefined,
        albumArtist: selectedTrack.originalAlbumArtist ?? undefined,
        album: selectedTrack.originalAlbum ?? undefined,
        title: selectedTrack.originalTitle ?? undefined,
        year: selectedTrack.originalYear ?? undefined,
        trackNumber: selectedTrack.originalTrackNumber ?? undefined
      }
    };
  }

  function handleFieldChange(field: keyof MetadataEdits, value: string | number) {
    const trackId = selectedTrack?.id;
    if (trackId == null) return;
    editedMetadata = {
      ...editedMetadata,
      [trackId]: {
        ...editedMetadata[trackId],
        [field]: value
      }
    };
  }

  function getDisplayValue(field: keyof MetadataEdits): string | number {
    if (!selectedTrack) return '';
    const editValue = currentEdits[field];
    if (editValue !== undefined) return editValue;
    const songValue = selectedTrack[field as keyof ApiSong];
    if (songValue !== undefined && songValue !== null) return songValue as string | number;
    return '';
  }

  function buildMetadataOverrides(): Partial<MetadataEdits> {
    if (!selectedTrack) return {};
    const edits = editedMetadata[selectedTrack.id];
    if (!edits) return {};
    const out: Partial<MetadataEdits> = {};
    if (edits.artist !== undefined) out.artist = edits.artist;
    if (edits.albumArtist !== undefined) out.albumArtist = edits.albumArtist;
    if (edits.album !== undefined) out.album = edits.album;
    if (edits.title !== undefined) out.title = edits.title;
    if (edits.year !== undefined) out.year = edits.year;
    if (edits.trackNumber !== undefined) out.trackNumber = edits.trackNumber;
    return out;
  }

  async function handleApprove() {
    if (!selectedTrack || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      const overrides = buildMetadataOverrides();
      await submitManualReview(selectedTrack.id, { decision: 'approve', ...overrides });
      const removedId = selectedTrack.id;
      const next = tracks.filter((t) => t.id !== removedId);
      tracks = next;
      if (selectedIndex >= next.length && selectedIndex > 0) selectedIndex = next.length - 1;
      rejectReason = '';
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to approve track';
    } finally {
      actionLoading = false;
    }
  }

  async function handleReject() {
    if (!selectedTrack || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await submitManualReview(selectedTrack.id, {
        decision: 'reject',
        rejectReason: rejectReason || undefined
      });
      const rejectedId = selectedTrack.id;
      const reason = rejectReason || 'Manually rejected';
      tracks = tracks.map((t) =>
        t.id === rejectedId
          ? {
              ...t,
              matchedBy: null,
              matchConfidence: null,
              matchWarnings: null,
              enrichmentError: reason
            }
          : t
      );
      if (selectedIndex < tracks.length - 1) selectedIndex += 1;
      rejectReason = '';
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to reject track';
    } finally {
      actionLoading = false;
    }
  }

  const isPlayingSelected = $derived(
    !!selectedTrack && playerStore.currentSong?.id === selectedTrack.id && playerStore.isPlaying
  );

  function handlePlayPause() {
    if (!selectedTrack) return;
    if (playerStore.currentSong?.id === selectedTrack.id) {
      playerStore.togglePlay();
      return;
    }
    void playerStore.playSong({
      id: selectedTrack.id,
      title: (selectedTrack.title ?? selectedTrack.fileName ?? 'Unknown').trim() || 'Unknown',
      artist: (selectedTrack.artist ?? 'Unknown Artist').trim() || 'Unknown Artist',
      streamUrl: getSongStreamUrl(selectedTrack.id)
    });
  }

  async function handleDelete() {
    if (!selectedTrack || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await softDeleteSong(selectedTrack.id);
      const removedId = selectedTrack.id;
      const next = tracks.filter((t) => t.id !== removedId);
      tracks = next;
      if (selectedIndex >= next.length && selectedIndex > 0) selectedIndex = next.length - 1;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to delete track';
    } finally {
      actionLoading = false;
    }
  }

  async function handleBulkApprove() {
    if (actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      bulkApproveResult = null;
      const result = await bulkApprove(bulkApproveMinConfidence);
      bulkApproveResult = { count: result.approvedCount };
      if (result.approvedCount > 0) await loadTracks();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to bulk approve';
    } finally {
      actionLoading = false;
    }
  }

  const eligibleForBulk = $derived(
    tracks.filter(
      (t) => t.matchConfidence != null && t.matchConfidence >= bulkApproveMinConfidence
    ).length
  );
</script>

{#if loading}
  <main class="flex flex-1 items-center justify-center p-4">
    <div class="flex flex-col items-center gap-4">
      <Loader2 class="text-primary size-8 animate-spin" />
      <p class="text-muted-foreground">Loading tracks for review...</p>
    </div>
  </main>
{:else if error && tracks.length === 0}
  <main class="flex flex-1 items-center justify-center p-4">
    <Card class="max-w-md text-center">
      <CardContent class="p-8">
        <div
          class="bg-destructive/10 mx-auto mb-4 flex size-16 items-center justify-center rounded-full"
        >
          <X class="text-destructive size-8" />
        </div>
        <h2 class="mb-2 text-xl font-semibold">Error</h2>
        <p class="text-muted-foreground mb-4">{error}</p>
        <Button onclick={loadTracks}>Retry</Button>
      </CardContent>
    </Card>
  </main>
{:else if tracks.length === 0}
  <main class="flex flex-1 items-center justify-center p-4">
    <Card class="max-w-md text-center">
      <CardContent class="p-8">
        <div
          class="bg-primary/10 mx-auto mb-4 flex size-16 items-center justify-center rounded-full"
        >
          <Check class="text-primary size-8" />
        </div>
        <h2 class="mb-2 text-xl font-semibold">All Done!</h2>
        <p class="text-muted-foreground mb-4">No more tracks need review. Great job!</p>
        <div class="flex justify-center gap-2">
          <Button variant="outline" onclick={loadTracks}>
            <RefreshCw class="mr-2 size-4" />
            Refresh
          </Button>
          <Button href="/overview">Back to Overview</Button>
        </div>
      </CardContent>
    </Card>
  </main>
{:else}
  <main class="flex flex-1 flex-col overflow-hidden">
    <div class="mx-auto flex w-full max-w-7xl flex-1 flex-col overflow-hidden px-4 md:px-6">
      <div class="flex flex-col gap-4 py-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h1 class="text-2xl font-bold md:text-3xl">Manual Review</h1>
          <p class="text-muted-foreground">Review and correct track metadata</p>
        </div>
        <div class="flex items-center gap-2">
          <AlertDialog.Root>
            <AlertDialog.Trigger>
              {#snippet child({ props })}
                <Button {...props} variant="outline" size="sm" class="gap-2">
                  <CheckCheck class="size-4" />
                  Bulk Approve
                </Button>
              {/snippet}
            </AlertDialog.Trigger>
            <AlertDialog.Content>
              <AlertDialog.Header>
                <AlertDialog.Title>Bulk Approve Tracks</AlertDialog.Title>
                <AlertDialog.Description>
                  Approve all tracks with match confidence at or above the threshold.
                  {#if eligibleForBulk > 0}
                    {' '}{eligibleForBulk} track{eligibleForBulk !== 1 ? 's' : ''} eligible.
                  {:else}
                    {' '}No tracks are currently eligible at this threshold.
                  {/if}
                </AlertDialog.Description>
              </AlertDialog.Header>
              <div class="py-2">
                <Label for="minConfidence" class="text-sm">Minimum Confidence</Label>
                <Input
                  id="minConfidence"
                  type="number"
                  min={0}
                  max={1}
                  step={0.05}
                  value={bulkApproveMinConfidence}
                  oninput={(e) => {
                    const v = parseFloat((e.target as HTMLInputElement).value);
                    bulkApproveMinConfidence = Number.isFinite(v) ? v : 0.75;
                  }}
                  class="mt-1"
                />
              </div>
              {#if bulkApproveResult}
                <p class="text-muted-foreground text-sm">
                  Approved {bulkApproveResult.count} track{bulkApproveResult.count !== 1 ? 's' : ''}.
                </p>
              {/if}
              <AlertDialog.Footer>
                <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                <AlertDialog.Action onclick={handleBulkApprove} disabled={actionLoading}>
                  {#if actionLoading}<Loader2 class="mr-2 size-4 animate-spin" />{/if}
                  Approve {eligibleForBulk} Track{eligibleForBulk !== 1 ? 's' : ''}
                </AlertDialog.Action>
              </AlertDialog.Footer>
            </AlertDialog.Content>
          </AlertDialog.Root>
          <Button variant="outline" size="icon" onclick={loadTracks} title="Refresh">
            <RefreshCw class="size-4" />
          </Button>
          <span class="text-muted-foreground text-sm">
            {selectedIndex + 1} of {tracks.length}
          </span>
          <div class="flex gap-1">
            <Button
              variant="outline"
              size="icon"
              onclick={handlePrev}
              disabled={selectedIndex === 0}
            >
              <ChevronLeft class="size-4" />
            </Button>
            <Button
              variant="outline"
              size="icon"
              onclick={handleNext}
              disabled={selectedIndex === tracks.length - 1}
            >
              <ChevronRight class="size-4" />
            </Button>
          </div>
        </div>
      </div>

      {#if error}
        <div
          class="border-destructive/50 bg-destructive/10 text-destructive mb-2 rounded-lg border p-3 text-sm"
        >
          {error}
          <Button variant="ghost" size="sm" class="ml-2" onclick={() => (error = null)}>
            Dismiss
          </Button>
        </div>
      {/if}

      <div
        class="grid min-h-0 min-w-0 flex-1 gap-4 pb-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]"
      >
        <div class="flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
          <Card class="flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
            <div class="border-border shrink-0 border-b p-3">
              <h2 class="font-medium">Pending Review ({tracks.length})</h2>
            </div>
            <div
              class="min-h-0 min-w-0 flex-1 overflow-x-hidden overflow-y-auto overscroll-contain"
            >
              <div class="w-full min-w-0 space-y-1 p-2">
                {#each tracks as track, index (track.id)}
                  <TrackListItem
                    {track}
                    isSelected={index === selectedIndex}
                    onSelect={() => handleTrackSelect(index)}
                  />
                {/each}
              </div>
            </div>
          </Card>
        </div>

        <div class="flex min-h-0 flex-col gap-4">
          <Card class="flex min-h-0 flex-1 flex-col overflow-hidden">
            <Tabs.Root value="edit" class="flex min-h-0 flex-1 flex-col overflow-hidden">
              <div class="border-border shrink-0 border-b px-3">
                <Tabs.List class="h-12 w-full justify-start rounded-none border-0 bg-transparent p-0">
                  <Tabs.Trigger
                    value="edit"
                    class="data-[state=active]:border-b-primary/50 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-t-transparent data-[state=active]:border-r-transparent data-[state=active]:border-l-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
                  >
                    Edit Metadata
                  </Tabs.Trigger>
                  <Tabs.Trigger
                    value="compare"
                    class="data-[state=active]:border-b-primary/50 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-t-transparent data-[state=active]:border-r-transparent data-[state=active]:border-l-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
                  >
                    Compare
                  </Tabs.Trigger>
                  <Tabs.Trigger
                    value="issues"
                    class="data-[state=active]:border-b-primary/50 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-t-transparent data-[state=active]:border-r-transparent data-[state=active]:border-l-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
                  >
                    Issues
                  </Tabs.Trigger>
                </Tabs.List>
              </div>

              <Tabs.Content value="edit" class="m-0 min-h-0 flex-1 overflow-hidden">
                <ScrollArea class="h-full min-h-0">
                  <div class="space-y-4 p-4">
                    <div class="space-y-2">
                      <div>
                        <Label for="title" class="text-muted-foreground text-xs">Title</Label>
                        <Input
                          id="title"
                          value={getDisplayValue('title')}
                          oninput={(e) =>
                            handleFieldChange('title', (e.target as HTMLInputElement).value)}
                          class="h-8"
                        />
                      </div>
                      <div>
                        <Label for="artist" class="text-muted-foreground text-xs">Artist</Label>
                        <Input
                          id="artist"
                          value={getDisplayValue('artist')}
                          oninput={(e) =>
                            handleFieldChange('artist', (e.target as HTMLInputElement).value)}
                          class="h-8"
                        />
                      </div>
                    </div>

                    <div class="grid gap-4 sm:grid-cols-2">
                      <div>
                        <Label for="album" class="text-muted-foreground text-xs">Album</Label>
                        <Input
                          id="album"
                          value={getDisplayValue('album')}
                          oninput={(e) =>
                            handleFieldChange('album', (e.target as HTMLInputElement).value)}
                          class="h-8"
                        />
                      </div>
                      <div>
                        <Label for="albumArtist" class="text-muted-foreground text-xs">
                          Album Artist
                        </Label>
                        <Input
                          id="albumArtist"
                          value={getDisplayValue('albumArtist')}
                          oninput={(e) =>
                            handleFieldChange(
                              'albumArtist',
                              (e.target as HTMLInputElement).value
                            )}
                          class="h-8"
                        />
                      </div>
                      <div>
                        <Label for="year" class="text-muted-foreground text-xs">Year</Label>
                        <Input
                          id="year"
                          type="number"
                          value={getDisplayValue('year')}
                          oninput={(e) => {
                            const n = parseInt((e.target as HTMLInputElement).value);
                            handleFieldChange('year', Number.isFinite(n) ? n : 0);
                          }}
                          class="h-8"
                        />
                      </div>
                      <div>
                        <Label for="trackNumber" class="text-muted-foreground text-xs">
                          Track Number
                        </Label>
                        <Input
                          id="trackNumber"
                          type="number"
                          value={getDisplayValue('trackNumber')}
                          oninput={(e) => {
                            const n = parseInt((e.target as HTMLInputElement).value);
                            handleFieldChange('trackNumber', Number.isFinite(n) ? n : 0);
                          }}
                          class="h-8"
                        />
                      </div>
                      <div>
                        <Label class="text-muted-foreground text-xs">Format</Label>
                        <Input
                          value={selectedTrack?.extension?.replace(/^\./, '').toUpperCase() || ''}
                          disabled
                          class="bg-secondary h-8"
                        />
                      </div>
                      <div>
                        <Label class="text-muted-foreground text-xs">Matched By</Label>
                        <Input
                          value={selectedTrack?.matchedBy || '—'}
                          disabled
                          class="bg-secondary h-8"
                        />
                      </div>
                    </div>

                    {#if selectedTrack?.matchConfidence != null}
                      {@const pct = Math.round(selectedTrack.matchConfidence * 100)}
                      <div class="bg-secondary/50 rounded-lg p-3">
                        <p class="text-muted-foreground mb-1 text-xs font-medium">
                          Match Confidence
                        </p>
                        <div class="flex items-center gap-2">
                          <div class="bg-secondary h-2 flex-1 rounded-full">
                            <div
                              class="h-2 rounded-full {selectedTrack.matchConfidence >= 0.8
                                ? 'bg-green-500'
                                : selectedTrack.matchConfidence >= 0.6
                                  ? 'bg-amber-500'
                                  : 'bg-red-500'}"
                              style="width: {pct}%"
                            ></div>
                          </div>
                          <span class="text-sm font-medium">{pct}%</span>
                        </div>
                      </div>
                    {/if}

                    <div class="bg-secondary/50 rounded-lg p-3">
                      <p class="text-muted-foreground mb-2 text-xs font-medium">Original File</p>
                      <p class="text-sm break-all">{selectedTrack?.sourcePath}</p>
                    </div>

                    {#if selectedTrack?.fingerprint}
                      <div class="bg-secondary/50 flex items-center gap-2 rounded-lg p-3">
                        <Fingerprint class="text-muted-foreground size-4" />
                        <code class="text-muted-foreground text-xs break-all">
                          {selectedTrack.fingerprint.length > 60
                            ? `${selectedTrack.fingerprint.slice(0, 60)}...`
                            : selectedTrack.fingerprint}
                        </code>
                      </div>
                    {/if}

                    {#if selectedTrack?.enrichmentError}
                      <div class="rounded-lg bg-amber-400/10 p-3">
                        <p class="mb-1 text-xs font-medium text-amber-400">Enrichment Note</p>
                        <p class="text-sm">{selectedTrack.enrichmentError}</p>
                      </div>
                    {/if}

                    <SourceLinks track={selectedTrack} />
                  </div>
                </ScrollArea>
              </Tabs.Content>

              <Tabs.Content value="compare" class="m-0 min-h-0 flex-1 overflow-hidden">
                <ScrollArea class="h-full min-h-0">
                  <div class="p-4">
                    {#if selectedTrack?.originalMetadataCaptured}
                      <div class="space-y-4">
                        <div class="flex items-center justify-between">
                          <h3 class="font-medium">Current vs Original Metadata</h3>
                          <Button size="sm" onclick={handleAcceptOriginal} class="gap-2">
                            <RefreshCw class="size-4" />
                            Restore Original
                          </Button>
                        </div>

                        <div class="space-y-3">
                          <CompareRow
                            label="Title"
                            current={selectedTrack.title}
                            original={selectedTrack.originalTitle}
                          />
                          <CompareRow
                            label="Artist"
                            current={selectedTrack.artist}
                            original={selectedTrack.originalArtist}
                          />
                          <CompareRow
                            label="Album"
                            current={selectedTrack.album}
                            original={selectedTrack.originalAlbum}
                          />
                          <CompareRow
                            label="Album Artist"
                            current={selectedTrack.albumArtist}
                            original={selectedTrack.originalAlbumArtist}
                          />
                          <CompareRow
                            label="Year"
                            current={selectedTrack.year?.toString()}
                            original={selectedTrack.originalYear?.toString()}
                          />
                          <CompareRow
                            label="Track Number"
                            current={selectedTrack.trackNumber?.toString()}
                            original={selectedTrack.originalTrackNumber?.toString()}
                          />
                        </div>
                      </div>
                    {:else}
                      <div class="flex flex-col items-center justify-center py-12 text-center">
                        <RefreshCw class="text-muted-foreground mb-4 size-12" />
                        <h3 class="font-medium">No Original Metadata</h3>
                        <p class="text-muted-foreground text-sm">
                          Original metadata was not captured for this track
                        </p>
                      </div>
                    {/if}
                  </div>
                </ScrollArea>
              </Tabs.Content>

              <Tabs.Content value="issues" class="m-0 min-h-0 flex-1 overflow-hidden">
                <ScrollArea class="h-full min-h-0">
                  <div class="p-4">
                    {#if selectedTrack?.matchWarnings && selectedTrack.matchWarnings.length > 0}
                      <div class="space-y-2">
                        {#each selectedTrack.matchWarnings as warning, i (i)}
                          <div class="flex items-start gap-3 rounded-lg bg-amber-400/10 p-3">
                            <AlertTriangle class="mt-0.5 size-4 shrink-0 text-amber-400" />
                            <p class="text-sm">{warning}</p>
                          </div>
                        {/each}
                      </div>
                    {:else}
                      <div class="flex flex-col items-center justify-center py-12 text-center">
                        <Check class="text-primary mb-4 size-12" />
                        <h3 class="font-medium">No Warnings</h3>
                        <p class="text-muted-foreground text-sm">
                          This track has no match warnings
                        </p>
                      </div>
                    {/if}
                  </div>
                </ScrollArea>
              </Tabs.Content>
            </Tabs.Root>
          </Card>

          <Card class="shrink-0">
            <div class="flex flex-wrap items-center gap-2 p-3">
              <Button
                variant="outline"
                onclick={handlePlayPause}
                disabled={!selectedTrack}
                class="gap-2"
                title={isPlayingSelected ? 'Pause' : 'Play'}
              >
                {#if isPlayingSelected}
                  <Pause class="size-4" />
                  Pause
                {:else}
                  <Play class="size-4" />
                  Play
                {/if}
              </Button>
              <Button onclick={handleApprove} disabled={actionLoading} class="gap-2">
                {#if actionLoading}
                  <Loader2 class="size-4 animate-spin" />
                {:else}
                  <Save class="size-4" />
                {/if}
                Approve
              </Button>
              <Button
                variant="outline"
                onclick={handleReject}
                disabled={actionLoading}
                class="gap-2"
              >
                {#if actionLoading}
                  <Loader2 class="size-4 animate-spin" />
                {:else}
                  <X class="size-4" />
                {/if}
                Reject
              </Button>
              <Button variant="outline" onclick={handleNext} class="gap-2">
                <SkipForward class="size-4" />
                Skip
              </Button>
              <AlertDialog.Root>
                <AlertDialog.Trigger>
                  {#snippet child({ props })}
                    <Button
                      {...props}
                      variant="outline"
                      class="text-destructive hover:text-destructive gap-2"
                    >
                      <Trash2 class="size-4" />
                      Delete
                    </Button>
                  {/snippet}
                </AlertDialog.Trigger>
                <AlertDialog.Content>
                  <AlertDialog.Header>
                    <AlertDialog.Title>Delete this track?</AlertDialog.Title>
                    <AlertDialog.Description>
                      This will soft-delete the track so it is excluded from review and library
                      build. The original file will not be deleted.
                    </AlertDialog.Description>
                  </AlertDialog.Header>
                  <AlertDialog.Footer>
                    <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                    <AlertDialog.Action onclick={handleDelete}>Delete</AlertDialog.Action>
                  </AlertDialog.Footer>
                </AlertDialog.Content>
              </AlertDialog.Root>
              <div class="ml-auto flex items-center gap-2">
                <Input
                  bind:value={rejectReason}
                  placeholder="Reject reason (optional)"
                  class="h-8 w-48"
                />
              </div>
            </div>
          </Card>
        </div>
      </div>
    </div>
  </main>
{/if}
