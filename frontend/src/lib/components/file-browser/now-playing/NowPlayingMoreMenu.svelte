<script lang="ts">
  import {
    Disc3,
    Ellipsis,
    ExternalLink,
    EyeOff,
    Film,
    Gauge,
    GitCompareArrows,
    Loader2,
    MessageSquareQuote,
    MicVocal,
    RefreshCw,
    Send,
    Settings2,
    Share,
    Sparkles,
    Timer,
    UserPlus
  } from '@lucide/svelte';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { Button } from '$lib/components/ui/button';
  import type { ApiSong } from '$lib/api-client';
  import type { AiLyrics, LyricsViewMode } from '$lib/lyrics/ai-lyrics.svelte';
  import type { LyricsDoc } from '$lib/lyrics/lyrics-doc.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { videoBackdropPrefs } from '$lib/stores/video-backdrop-prefs.svelte';
  import { findShareLink, shareLink, type ShareLink } from '$lib/share-links';
  import { sendTo, sendToSong } from '$lib/stores/send-to.svelte';
  import { cn } from '$lib/utils';
  import { videoProblem, type SongVideo } from './song-video.svelte';

  /**
   * Now Playing's one ⋯ menu, top-right in every mode. Three groups, one level of submenus, and
   * anything unavailable is left out rather than disabled:
   *
   *   Go to album · Go to artist
   *   Share link… · Send to… · Share with a friend…           (admin)
   *   Lyrics › · Music video › · Playback speed › · Hide player
   *
   * Lyrics › gathers what used to be lg-only or buried in the compare split — the view, Synced /
   * Plain, the player's default source, Compare, the AI run, the timing check, the LRCLIB
   * re-check and the source link — so a phone reaches all of it. Actions whose result shows in
   * the lyrics view switch to it first; the AI run asks first (its ellipsis promises that step).
   */
  type Props = {
    song: ApiSong;
    isOwner: boolean;
    /** The song is the one loaded in the player (Hide player applies to it). */
    isCurrent: boolean;
    ai: AiLyrics;
    /** The document the lyrics viewer shows (AI or LRCLIB) — Synced / Plain acts on it. */
    viewerDoc: LyricsDoc;
    /** The stored LRCLIB lyrics — the timing check and the re-check act on these. */
    lrclibDoc: LyricsDoc;
    video: SongVideo;
    lrclibUrl: string | undefined;
    albumHref: string;
    artistHref: string;
    onGo: (href: string) => void;
    onShowLyrics: () => void;
    onCompare: () => void;
    /** "Improve with AI…": asks first (the run is a paid AI job), then starts it. */
    onEnhance: () => void;
    onManageVideo: () => void;
    onShareWithFriend: () => void;
    onHidePlayer: () => void;
    class?: string;
  };
  const {
    song,
    isOwner,
    isCurrent,
    ai,
    viewerDoc,
    lrclibDoc,
    video,
    lrclibUrl,
    albumHref,
    artistHref,
    onGo,
    onShowLyrics,
    onCompare,
    onEnhance,
    onManageVideo,
    onShareWithFriend,
    onHidePlayer,
    class: className
  }: Props = $props();

  const speedOptions = [0.5, 0.65, 0.75, 0.85, 1, 1.1, 1.25, 1.5];

  const recheckLabel = $derived(lrclibDoc.hasAny ? 'Look for synced lyrics' : 'Check LRCLIB again');
  const canRecheck = $derived(isOwner && !lrclibDoc.hasSynced && song.isInstrumental !== true);
  const canCheckTiming = $derived(isOwner && lrclibDoc.hasSynced);
  const canPickSource = $derived(isOwner && ai.canCompare);
  const problem = $derived(videoProblem(video.info, video.infoUnavailable));
  // Non-admins see the section only when there is a record to say something about.
  const showVideoMenu = $derived(isOwner || video.info !== null);

  // Share link…: the same flow as the Listen menus (see share-links.ts). Opening the menu only
  // LOOKS for a link the song already has, so the tap can hand it to the share sheet inside its
  // activation; a link is minted only when Share link… is picked, never because the menu opened.
  let known = $state<{ id: number; link: ShareLink | null } | null>(null);

  function onOpenChange(open: boolean) {
    if (!open || !isOwner || known?.id === song.id) return;
    const id = song.id;
    void findShareLink([id], 'song').then((link) => {
      if (song.id === id) known = { id, link };
    });
  }

  function shareSong() {
    shareLink({ known: known?.id === song.id ? known.link : null, songId: song.id, scope: 'song' });
    // Whatever happens next, a link exists afterwards: look again on the next open.
    known = null;
  }
</script>

<DropdownMenu.Root {onOpenChange}>
  <DropdownMenu.Trigger>
    {#snippet child({ props })}
      <Button
        {...props}
        variant="ghost"
        size="icon"
        class={cn(
          'group/more size-11 shrink-0 rounded-full hover:bg-transparent aria-expanded:bg-transparent',
          className
        )}
        aria-label="More"
      >
        <span
          class="bg-secondary group-hover/more:bg-secondary-hover flex size-8 items-center justify-center rounded-full transition-colors"
        >
          <Ellipsis class="size-5" />
        </span>
      </Button>
    {/snippet}
  </DropdownMenu.Trigger>
  <!-- dark: the menu belongs to Now Playing's media appearance, whatever the app theme. z-[70]:
       above the overlay's z-60. -->
  <DropdownMenu.Content align="end" class="dark z-[70] min-w-56">
    <DropdownMenu.Group>
      <DropdownMenu.Item onSelect={() => onGo(albumHref)}>
        <Disc3 />
        Go to album
      </DropdownMenu.Item>
      <DropdownMenu.Item onSelect={() => onGo(artistHref)}>
        <MicVocal />
        Go to artist
      </DropdownMenu.Item>
    </DropdownMenu.Group>

    {#if isOwner}
      <DropdownMenu.Separator />
      <DropdownMenu.Group>
        <DropdownMenu.Item onSelect={shareSong}>
          <Share />
          Share link…
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={() => sendTo.show(sendToSong(song, { nested: true }))}>
          <Send />
          Send to…
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={onShareWithFriend}>
          <UserPlus />
          Share with a friend…
        </DropdownMenu.Item>
      </DropdownMenu.Group>
    {/if}

    <DropdownMenu.Separator />
    <DropdownMenu.Group>
      <DropdownMenu.Sub>
        <DropdownMenu.SubTrigger>
          <MessageSquareQuote />
          Lyrics
        </DropdownMenu.SubTrigger>
        <DropdownMenu.SubContent class="min-w-56">
          {#if ai.hasTranslation && !ai.comparing}
            <DropdownMenu.RadioGroup
              value={ai.lyricsView}
              onValueChange={(v) => {
                ai.setLyricsView(v as LyricsViewMode);
                onShowLyrics();
              }}
            >
              <DropdownMenu.RadioItem value="original">Show original</DropdownMenu.RadioItem>
              <DropdownMenu.RadioItem value="pronunciation">Pronunciation</DropdownMenu.RadioItem>
              <DropdownMenu.RadioItem value="translation">Translation</DropdownMenu.RadioItem>
            </DropdownMenu.RadioGroup>
            <DropdownMenu.Separator />
          {/if}
          {#if viewerDoc.canToggleSynced}
            <DropdownMenu.RadioGroup
              value={viewerDoc.showSynced ? 'synced' : 'plain'}
              onValueChange={(v) => {
                viewerDoc.setShowSynced(v === 'synced');
                onShowLyrics();
              }}
            >
              <DropdownMenu.RadioItem value="synced">Synced</DropdownMenu.RadioItem>
              <DropdownMenu.RadioItem value="plain">Plain</DropdownMenu.RadioItem>
            </DropdownMenu.RadioGroup>
            <DropdownMenu.Separator />
          {/if}
          {#if canPickSource}
            <DropdownMenu.RadioGroup
              value={ai.preferredSource}
              onValueChange={(v) =>
                void ai.setPreferred(v === 'transcribed' ? 'transcribed' : 'lrclib')}
            >
              <DropdownMenu.RadioItem value="lrclib" disabled={ai.preferSaving}>
                Source: LRCLIB
              </DropdownMenu.RadioItem>
              <DropdownMenu.RadioItem value="transcribed" disabled={ai.preferSaving}>
                Source: AI-synced
              </DropdownMenu.RadioItem>
            </DropdownMenu.RadioGroup>
            <DropdownMenu.Separator />
          {/if}
          {#if canPickSource}
            <DropdownMenu.Item onSelect={onCompare}>
              <GitCompareArrows />
              Compare versions…
            </DropdownMenu.Item>
          {/if}
          {#if ai.canEnhance}
            <DropdownMenu.Item disabled={ai.enhanceBusy} onSelect={onEnhance}>
              {#if ai.enhanceBusy}<Loader2 class="animate-spin" />{:else}<Sparkles />{/if}
              {ai.enhanceBusy ? 'Improving with AI…' : `${ai.enhanceLabel}…`}
            </DropdownMenu.Item>
          {/if}
          {#if canCheckTiming}
            <DropdownMenu.Item
              disabled={lrclibDoc.verifyState === 'checking'}
              onSelect={() => {
                onShowLyrics();
                void lrclibDoc.verifyTiming();
              }}
            >
              <Timer />
              Check timing
            </DropdownMenu.Item>
          {/if}
          {#if canRecheck}
            <DropdownMenu.Item
              disabled={lrclibDoc.recheckState === 'checking'}
              onSelect={() => {
                onShowLyrics();
                void lrclibDoc.recheck();
              }}
            >
              <RefreshCw />
              {recheckLabel}
            </DropdownMenu.Item>
          {/if}
          {#if lrclibUrl}
            <DropdownMenu.Item>
              {#snippet child({ props })}
                <a {...props} href={lrclibUrl} target="_blank" rel="noopener noreferrer">
                  <ExternalLink />
                  View on LRCLIB
                </a>
              {/snippet}
            </DropdownMenu.Item>
          {/if}
        </DropdownMenu.SubContent>
      </DropdownMenu.Sub>

      {#if showVideoMenu}
        <DropdownMenu.Sub>
          <DropdownMenu.SubTrigger>
            <Film />
            Music video
          </DropdownMenu.SubTrigger>
          <DropdownMenu.SubContent class="min-w-56">
            {#if problem}
              <DropdownMenu.Label
                class={cn(
                  'text-footnote font-normal',
                  problem.tone === 'destructive' ? 'text-destructive-text' : 'text-muted-foreground'
                )}
              >
                {problem.text}
              </DropdownMenu.Label>
            {:else if !video.info && isOwner}
              <DropdownMenu.Label class="text-footnote text-muted-foreground font-normal">
                No video attached
              </DropdownMenu.Label>
            {/if}
            {#if video.playable}
              <DropdownMenu.CheckboxItem
                checked={videoBackdropPrefs.enabled}
                onCheckedChange={(v) => videoBackdropPrefs.setEnabled(v)}
              >
                Show as background
              </DropdownMenu.CheckboxItem>
            {/if}
            {#if isOwner}
              <DropdownMenu.Item onSelect={onManageVideo}>
                <Settings2 />
                Manage video…
              </DropdownMenu.Item>
            {/if}
          </DropdownMenu.SubContent>
        </DropdownMenu.Sub>
      {/if}

      <!-- This device's speed, which is not what anyone hears while another device plays. -->
      {#if playerStore.speedAdjustable}
        <DropdownMenu.Sub>
          <DropdownMenu.SubTrigger>
            <Gauge />
            Playback speed
          </DropdownMenu.SubTrigger>
          <DropdownMenu.SubContent class="min-w-44">
            <DropdownMenu.RadioGroup
              value={String(playerStore.playbackRate)}
              onValueChange={(v) => playerStore.setPlaybackRate(Number(v))}
            >
              {#each speedOptions as rate (rate)}
                <DropdownMenu.RadioItem value={String(rate)} class="tabular-nums">
                  {rate === 1 ? 'Normal' : `${rate}×`}
                </DropdownMenu.RadioItem>
              {/each}
            </DropdownMenu.RadioGroup>
          </DropdownMenu.SubContent>
        </DropdownMenu.Sub>
      {/if}

      {#if isCurrent}
        <DropdownMenu.Item onSelect={onHidePlayer}>
          <EyeOff />
          Hide player
        </DropdownMenu.Item>
      {/if}
    </DropdownMenu.Group>
  </DropdownMenu.Content>
</DropdownMenu.Root>
