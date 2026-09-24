<script lang="ts">
  import { Check, ImageOff, Loader2, RotateCcw } from '@lucide/svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Badge, type BadgeVariant } from '$lib/components/ui/badge';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Switch } from '$lib/components/ui/switch';
  import {
    getSongVideoCandidates,
    getSongVideoCandidateThumbnailUrl,
    probeSongVideoCandidate,
    type SongVideoCandidate,
    type VideoMotion
  } from '$lib/api-client';
  import { videoBackdropPrefs } from '$lib/stores/video-backdrop-prefs.svelte';
  import { cn } from '$lib/utils';
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
  // so a static album cover can be recognised and skipped before it costs any disk.
  let pickerOpen = $state(false);
  let candidates = $state<SongVideoCandidate[] | null>(null);
  let candidatesLoading = $state(false);
  let candidatesError = $state<string | null>(null);
  let probing = $state<Set<string>>(new Set());

  // A new song starts the picker over (the sheet can stay mounted across songs).
  let pickerForSong: number | null = null;
  $effect(() => {
    const id = video.songId;
    if (pickerForSong === id) return;
    pickerForSong = id;
    pickerOpen = false;
    candidates = null;
    candidatesError = null;
    urlInput = '';
  });

  async function onBrowse() {
    const songId = video.songId;
    pickerOpen = true;
    if (candidatesLoading || songId === null) return;
    candidatesLoading = true;
    candidatesError = null;
    try {
      const result = await getSongVideoCandidates(songId);
      if (video.songId !== songId) return;
      candidates = result.candidates;
      // The list arrives unmeasured — one flat search, so it is quick. Measuring is a request per
      // candidate: fire the leading few in PARALLEL and let each row settle on its own, so one
      // slow video delays only its own verdict instead of the whole picker.
      void Promise.all(
        result.candidates.slice(0, result.probeLimit).map((c) => onCheck(c.videoId))
      );
    } catch {
      candidatesError = 'Search failed — try again.';
    } finally {
      candidatesLoading = false;
    }
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

  async function onPick(videoId: string) {
    // An explicit pick is honored verbatim by the backend, motion verdict notwithstanding — the
    // owner looked at the measurement and chose anyway.
    const ok = await video.fetch(`https://www.youtube.com/watch?v=${videoId}`);
    if (ok) pickerOpen = false;
    else candidatesError = 'Could not start the download.';
  }

  async function onFetch() {
    const ok = await video.fetch(urlInput.trim() || undefined);
    if (ok) urlInput = '';
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
        {:else if candidatesLoading}
          <GroupedList.Row label="Searching and checking candidates…">
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
          <GroupedList.Row onclick={() => (pickerOpen = false)}>
            <span class="text-body text-primary md:text-sm">Close the list</span>
          </GroupedList.Row>
        {/if}
      </GroupedList.Section>

      <GroupedList.Section
        header="From a link"
        footer={video.infoUnavailable && !info
          ? 'Video status is unavailable right now — retrying before a fetch is allowed.'
          : 'Leave it empty to search automatically.'}
      >
        <form
          class="flex items-center gap-2 px-4 py-2"
          onsubmit={(e) => {
            e.preventDefault();
            void onFetch();
          }}
        >
          <Input
            bind:value={urlInput}
            type="url"
            inputmode="url"
            enterkeyhint="go"
            autocapitalize="off"
            autocorrect="off"
            spellcheck={false}
            placeholder="YouTube URL (optional)"
            aria-label="YouTube URL"
            class="min-w-0 flex-1"
          />
          <Button
            type="submit"
            class="h-11 rounded-full px-4 md:h-8"
            disabled={video.busy || (video.infoUnavailable && !info)}
          >
            {info ? 'Refetch' : 'Fetch'}
          </Button>
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
  onAction={() => void video.remove()}
/>
