<script lang="ts">
  import { untrack } from 'svelte';
  import { toast } from 'svelte-sonner';
  import { Check, ImageOff, Loader2, RotateCcw } from '@lucide/svelte';
  import * as BottomSheet from '#lib/components/ui/bottom-sheet/index.ts';
  import * as GroupedList from '#lib/components/ui/grouped-list/index.ts';
  import { Badge, type BadgeVariant } from '#lib/components/ui/badge/index.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import * as InputGroup from '#lib/components/ui/input-group/index.ts';
  import { Switch } from '#lib/components/ui/switch/index.ts';
  import {
    getSongVideoCandidates,
    getSongVideoCandidateThumbnailUrl,
    probeSongVideoCandidate,
    type SongVideoCandidate,
    type VideoMotion
  } from '#lib/api-client.ts';
  import { videoBackdropPrefs } from '#lib/stores/video-backdrop-prefs.svelte.ts';
  import { cn } from '#lib/utils.ts';
  import ActionSheet from './ActionSheet.svelte';
  import {
    formatVideoOffset,
    videoProblem,
    videoSyncLabel,
    type SongVideo
  } from './song-video.svelte';

  /**
   * "Manage video…" from Now Playing's ⋯ › Music video: everything the floating Film popover
   * used to hold, as a sheet — status, the backdrop switch, the sync nudge and reset, the
   * candidate picker, a URL fetch/refetch and Remove (destructive, confirmed). Admin-only; nested
   * over Now Playing and in its dark media appearance.
   */
  type Props = {
    open: boolean;
    video: SongVideo;
    title: string;
  };
  let { open = $bindable(false), video, title }: Props = $props();

  const info = $derived(video.info);
  const problem = $derived(videoProblem(info, video.infoUnavailable));
  // The status in words, under the row's label rather than as a trailing value: a sync label
  // ("Auto-aligned −9.0s · 92%") does not fit beside the label on a phone.
  const statusText = $derived(
    info?.status === 'Ready' && !info.fileMissing
      ? `Ready · ${videoSyncLabel(info)}`
      : problem
        ? problem.text
        : info
          ? info.status
          : 'None attached'
  );

  let urlInput = $state('');
  let confirmRemoveOpen = $state(false);
  // Candidate picker: what the search WOULD download, with each option's measured motion and size,
  // so a static album cover can be recognised and skipped before it costs any disk. The server
  // remembers each song's list (searched ahead of time for new songs), and the sheet asks for it
  // as soon as it opens, so "Choose video…" shows the list instead of a search.
  let pickerOpen = $state(false);
  let candidates = $state<SongVideoCandidate[] | null>(null);
  let candidatesLoading = $state(false);
  let candidatesError = $state<string | null>(null);
  let probing = $state<Set<string>>(new Set());
  /** The song whose list is loaded or loading; a new song starts over. */
  let listFor = $state<number | null>(null);
  /** Bumped by every load and by a song change, so a late answer for another song is dropped. */
  let listRequest = 0;

  // A new song starts the picker over (the sheet can stay mounted across songs).
  let pickerForSong: number | null = null;
  $effect(() => {
    const id = video.songId;
    if (pickerForSong === id) return;
    pickerForSong = id;
    listRequest += 1;
    listFor = null;
    pickerOpen = false;
    candidates = null;
    candidatesLoading = false;
    candidatesError = null;
    urlInput = '';
  });

  // Ask for the list when the sheet opens, before anyone taps "Choose video…".
  $effect(() => {
    const id = video.songId;
    if (!open || id === null || listFor === id || info?.status === 'Fetching') return;
    untrack(() => void loadCandidates(false));
  });

  async function loadCandidates(refresh: boolean) {
    const songId = video.songId;
    if (songId === null) return;
    const request = ++listRequest;
    listFor = songId;
    candidatesLoading = true;
    candidatesError = null;
    if (refresh) candidates = null;
    try {
      const result = await getSongVideoCandidates(songId, { refresh });
      if (request !== listRequest) return;
      candidates = result.candidates;
      // A remembered list arrives measured. Whatever is still unmeasured among the leading few is
      // a request per candidate: fire them in PARALLEL and let each row settle on its own, so one
      // slow video delays only its own verdict instead of the whole picker.
      void Promise.all(
        result.candidates
          .slice(0, result.probeLimit)
          .filter((c) => c.motion === 'Unknown')
          .map((c) => onCheck(c.videoId))
      );
    } catch {
      if (request === listRequest) candidatesError = 'Search failed — try again.';
    } finally {
      if (request === listRequest) candidatesLoading = false;
    }
  }

  function onBrowse() {
    pickerOpen = true;
    if (candidatesError || listFor !== video.songId) void loadCandidates(false);
  }

  async function onCheck(videoId: string) {
    const songId = video.songId;
    if (probing.has(videoId) || songId === null) return;
    probing = new Set([...probing, videoId]);
    try {
      const probed = await probeSongVideoCandidate(songId, videoId);
      if (video.songId !== songId) return;
      // Keep the list's own ranking; fill in what was measured, plus the title/channel/duration
      // for a pinned row the search itself never described.
      candidates =
        candidates?.map((c) =>
          c.videoId === videoId
            ? {
                ...c,
                motion: probed.motion,
                estimatedBytes: probed.estimatedBytes,
                squareSource: probed.squareSource,
                title: c.title || probed.title,
                channel: c.channel || probed.channel,
                durationSeconds: c.durationSeconds ?? probed.durationSeconds
              }
            : c
        ) ?? null;
    } catch {
      // Leave the row unmeasured and re-checkable. This must not clear the list: an unmeasured
      // candidate is still a perfectly valid thing to pick.
    } finally {
      probing = new Set([...probing].filter((id) => id !== videoId));
    }
  }

  /**
   * Start a download and get out of the way: the fetch runs on the server and takes a minute, so
   * the sheet closes and a toast says the video will appear by itself (the backdrop and Video mode
   * pick it up when it is ready) rather than holding the owner on a spinner.
   */
  async function startFetch(url?: string): Promise<boolean> {
    const ok = await video.fetch(url);
    if (!ok) {
      toast.error('Could not start the download', { description: 'Try again in a moment.' });
      return false;
    }
    open = false;
    // The list marks the attached video as current: ask again on the next open.
    listFor = null;
    toast("We'll get the video for you", {
      description: 'It shows up behind the player as soon as it has downloaded.'
    });
    return true;
  }

  async function onPick(videoId: string) {
    // An explicit pick is honored verbatim by the backend, motion verdict notwithstanding — the
    // owner looked at the measurement and chose anyway.
    if (await startFetch(`https://www.youtube.com/watch?v=${videoId}`)) pickerOpen = false;
  }

  // A fetch waits for the video status: without it the backend cannot tell a refetch from a
  // first fetch, so neither the automatic search nor a link may start until it is back.
  const fetchBlocked = $derived(video.busy || (video.infoUnavailable && !info));

  async function onFetchLink() {
    const url = urlInput.trim();
    if (!url) return;
    if (await startFetch(url)) urlInput = '';
  }

  function formatBytes(bytes: number | null): string {
    if (bytes == null) return 'Size unknown';
    return `${(bytes / 1024 / 1024).toFixed(0)} MB`;
  }

  function formatDuration(seconds: number | null): string {
    if (seconds == null || seconds <= 0) return '';
    return `${Math.floor(seconds / 60)}:${String(Math.round(seconds % 60)).padStart(2, '0')}`;
  }

  // The motion verdict as a word on a contrast-checked tint — never a hue alone.
  const MOTION_LABELS: Record<
    VideoMotion,
    { label: string; variant: BadgeVariant; title: string }
  > = {
    RealVideo: {
      label: 'Real video',
      variant: 'tinted',
      title: 'The picture moves throughout — an actual clip.'
    },
    LowMotion: {
      label: 'Low motion',
      variant: 'warning',
      title: 'Mostly still: lyric cards, a slideshow, or a looping visualizer.'
    },
    Static: {
      label: 'Still image',
      variant: 'destructive',
      title: 'One image for the whole song — an album cover or an audio-only upload.'
    },
    Unknown: {
      label: 'Not checked',
      variant: 'secondary',
      title: 'Not measured: only the top candidates are probed.'
    }
  };

  const NUDGES = [
    { ms: -1000, label: '−1s' },
    { ms: -100, label: '−0.1s' },
    { ms: 100, label: '+0.1s' },
    { ms: 1000, label: '+1s' }
  ];
</script>

<BottomSheet.Root bind:open nested class="dark" title="Music video" description={title}>
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={() => (open = false)}>Done</BottomSheet.Action>
  {/snippet}
  <div class="flex flex-col gap-6 pb-2">
    <GroupedList.Section
      footer={info?.status === 'Failed' && info.lastError
        ? info.lastError
        : info?.status === 'Ready' && info.fileMissing
          ? 'The video file is gone from disk — refetch to restore it.'
          : undefined}
    >
      <GroupedList.Row label="Status">
        <span
          class={cn(
            'text-subheadline md:text-xs',
            problem?.tone === 'destructive' ? 'text-destructive-text' : 'text-muted-foreground'
          )}
        >
          {statusText}
        </span>
        {#snippet trailing()}
          {#if problem?.busy}<Loader2 class="text-muted-foreground size-4 animate-spin" />{/if}
        {/snippet}
      </GroupedList.Row>
      {#if video.playable}
        <GroupedList.Row label="Show as background">
          {#snippet trailing()}
            <Switch
              checked={videoBackdropPrefs.enabled}
              onCheckedChange={(v: boolean) => videoBackdropPrefs.setEnabled(v)}
              aria-label="Show as background"
            />
          {/snippet}
        </GroupedList.Row>
      {/if}
    </GroupedList.Section>

    {#if video.playable}
      <GroupedList.Section
        header="Sync"
        footer="Positive means the clip has an intro before the song starts."
      >
        <GroupedList.Row label="Offset" value={formatVideoOffset(video.offsetMs)} />
        <div class="grid grid-cols-4 gap-2 px-4 py-2.5">
          {#each NUDGES as nudge (nudge.ms)}
            <Button
              variant="gray"
              class="h-11 rounded-full tabular-nums md:h-8"
              aria-label={`Nudge the video ${nudge.label}`}
              onclick={() => video.nudge(nudge.ms)}
            >
              {nudge.label}
            </Button>
          {/each}
        </div>
        <GroupedList.Row onclick={() => video.resetAuto()} icon={RotateCcw}>
          <span class="text-body text-primary md:text-sm">Reset to automatic alignment</span>
        </GroupedList.Row>
      </GroupedList.Section>
    {/if}

    {#if info?.status !== 'Fetching'}
      <GroupedList.Section
        header="Find a video"
        footer={pickerOpen ? 'Checked before downloading — nothing is on disk yet.' : undefined}
      >
        {#if !pickerOpen}
          <GroupedList.Row onclick={onBrowse} disabled={video.busy}>
            <span class="text-body text-primary md:text-sm">Choose video…</span>
          </GroupedList.Row>
          <GroupedList.Row onclick={() => void startFetch()} disabled={fetchBlocked}>
            <span
              class={cn(
                'text-body md:text-sm',
                fetchBlocked ? 'text-muted-foreground-dim' : 'text-primary'
              )}
            >
              {info ? 'Replace with the best match' : 'Download the best match'}
            </span>
          </GroupedList.Row>
        {:else if candidatesLoading}
          <GroupedList.Row label="Searching YouTube…">
            {#snippet trailing()}<Loader2
                class="text-muted-foreground size-4 animate-spin"
              />{/snippet}
          </GroupedList.Row>
        {:else if candidatesError}
          <GroupedList.Row onclick={onBrowse}>
            <span class="text-body text-destructive-text md:text-sm">{candidatesError}</span>
          </GroupedList.Row>
        {:else if candidates && candidates.length === 0}
          <GroupedList.Row label="No candidates found for this song" />
          <GroupedList.Row onclick={() => void loadCandidates(true)}>
            <span class="text-body text-primary md:text-sm">Search again</span>
          </GroupedList.Row>
        {:else if candidates}
          {#each candidates as candidate (candidate.videoId)}
            {@const motion = MOTION_LABELS[candidate.motion]}
            <GroupedList.Row>
              {#snippet leading()}
                <span
                  class="bg-muted relative flex aspect-video w-20 shrink-0 items-center justify-center overflow-hidden rounded-sm"
                >
                  {#if candidate.hasThumbnail && video.songId !== null}
                    <img
                      src={getSongVideoCandidateThumbnailUrl(video.songId, candidate.videoId)}
                      alt=""
                      loading="lazy"
                      draggable="false"
                      class="size-full object-cover"
                    />
                  {:else}
                    <ImageOff class="text-muted-foreground size-4" />
                  {/if}
                </span>
              {/snippet}
              <span
                class="text-subheadline line-clamp-2 font-medium md:text-sm"
                title={candidate.title}
              >
                {candidate.title || candidate.videoId}
              </span>
              <span class="text-footnote text-muted-foreground truncate">
                {candidate.channel}{candidate.durationSeconds
                  ? ` · ${formatDuration(candidate.durationSeconds)}`
                  : ''} · {formatBytes(candidate.estimatedBytes)}
              </span>
              <span class="mt-1 flex flex-wrap items-center gap-1">
                {#if candidate.motion !== 'Unknown'}
                  <Badge variant={motion.variant} title={motion.title}>{motion.label}</Badge>
                {/if}
                {#if candidate.squareSource}
                  <Badge
                    variant="secondary"
                    title="The upload is square — an album cover filling the frame."
                  >
                    Square
                  </Badge>
                {/if}
                {#if candidate.isCurrent}
                  <Badge variant="secondary"><Check />Current</Badge>
                {/if}
              </span>
              {#snippet trailing()}
                <span class="flex flex-col items-end gap-1">
                  <Button
                    variant="tinted"
                    class="h-8 rounded-full px-3 pointer-coarse:h-11 pointer-coarse:px-4"
                    disabled={video.busy}
                    onclick={() => onPick(candidate.videoId)}
                    aria-label={`Use ${candidate.title || candidate.videoId}`}
                  >
                    Use
                  </Button>
                  {#if candidate.motion === 'Unknown'}
                    <Button
                      variant="gray"
                      class="h-8 rounded-full px-3 pointer-coarse:h-11 pointer-coarse:px-4"
                      disabled={probing.has(candidate.videoId)}
                      title="Measure this one: is it a real clip or a still image?"
                      onclick={() => onCheck(candidate.videoId)}
                    >
                      {#if probing.has(candidate.videoId)}
                        <Loader2 class="size-3.5 animate-spin" />
                      {/if}
                      Check
                    </Button>
                  {/if}
                </span>
              {/snippet}
            </GroupedList.Row>
          {/each}
          <GroupedList.Row onclick={() => void loadCandidates(true)}>
            <span class="text-body text-primary md:text-sm">Search again</span>
          </GroupedList.Row>
          <GroupedList.Row onclick={() => (pickerOpen = false)}>
            <span class="text-body text-primary md:text-sm">Close the list</span>
          </GroupedList.Row>
        {/if}
      </GroupedList.Section>

      <GroupedList.Section
        header="From a link"
        footer={video.infoUnavailable && !info
          ? 'Video status is unavailable right now — retrying before a fetch is allowed.'
          : undefined}
      >
        <!-- The field IS the cell, as in iOS Settings: no fill of its own on the cell's fill,
             and the action sits inside it at the trailing edge. -->
        <form
          onsubmit={(e) => {
            e.preventDefault();
            void onFetchLink();
          }}
        >
          <InputGroup.Root class="h-11 rounded-xl bg-transparent md:h-11">
            <InputGroup.Input
              bind:value={urlInput}
              type="url"
              inputmode="url"
              enterkeyhint="go"
              autocapitalize="off"
              autocorrect="off"
              spellcheck={false}
              placeholder="Paste a YouTube link"
              aria-label="YouTube link"
              class="px-4 md:px-4"
            />
            <InputGroup.Addon align="inline-end">
              <InputGroup.Button
                type="submit"
                variant="tinted"
                class="h-8 rounded-full px-3 pointer-coarse:h-9 pointer-coarse:px-4"
                disabled={fetchBlocked || !urlInput.trim()}
              >
                {info ? 'Refetch' : 'Fetch'}
              </InputGroup.Button>
            </InputGroup.Addon>
          </InputGroup.Root>
        </form>
      </GroupedList.Section>
    {/if}

    {#if info && info.status !== 'Fetching'}
      <GroupedList.Section>
        <GroupedList.Row
          label="Remove video"
          destructive
          disabled={video.busy}
          onclick={() => (confirmRemoveOpen = true)}
        />
      </GroupedList.Section>
    {/if}
  </div>
</BottomSheet.Root>

<ActionSheet
  bind:open={confirmRemoveOpen}
  title="Remove the video?"
  description="The downloaded clip is deleted. You can fetch it again later."
  actionLabel="Remove video"
  destructive
  onAction={() => {
    // The list marks the removed video as current: ask again on the next open.
    listFor = null;
    void video.remove();
  }}
/>
