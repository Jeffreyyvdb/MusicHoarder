<script lang="ts" module>
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { songDetail } from '#lib/stores/song-detail.svelte.ts';
  import { tapActionFor } from '#lib/track-list-view.svelte.ts';
  import { buttonVariants } from '#lib/components/ui/button/index.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import { cn } from '#lib/utils.ts';

  /**
   * A row tap on a phone (the tap rule): a song that is not the
   * loaded one plays the list it sits in, from it (`play`, via `playerStore.startQueue`); the
   * loaded song opens Now Playing instead, resuming it if it was paused. A tap never pauses and
   * never restarts — the old row play button toggled pause, which is the wrong thing for a
   * whole-row target to do.
   */
  export function activateTrack(songId: number, play: () => void, albumKey?: string): void {
    if (tapActionFor(songId, playerStore.currentSong?.id) === 'play') {
      play();
      return;
    }
    songDetail.open(songId, albumKey);
    if (!playerStore.isPlaying) playerStore.resume();
  }

  /** The ⋯ trigger's classes, worked out once: what `Button variant="ghost" size="icon"` renders. */
  const RESTING_TRIGGER = cn(
    buttonVariants({ variant: 'ghost', size: 'icon' }),
    'text-muted-foreground hover:text-foreground aria-expanded:text-foreground shrink-0 rounded-full pointer-coarse:size-11'
  );

  // Compact (below md) presents the actions as a bottom sheet, md+ as a menu at the ⋯ button. One
  // query for every row, made on first use: a list renders hundreds of these menus.
  let compactQuery: IsMobile | undefined;
  function isCompact(): boolean {
    compactQuery ??= new IsMobile();
    return compactQuery.current;
  }
</script>

<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    Disc3,
    Ellipsis,
    Flag,
    Heart,
    HeartOff,
    History,
    Info,
    ListEnd,
    ListMinus,
    ListPlus,
    ListStart,
    Mic2,
    Play,
    Send,
    Share,
    UsersRound
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import type { Component } from 'svelte';
  import type { LongPressPoint } from '#lib/actions/long-press.ts';
  import * as BottomSheet from '#lib/components/ui/bottom-sheet/index.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import * as GroupedList from '#lib/components/ui/grouped-list/index.ts';
  import ShareWithFriendDialog from '#lib/components/file-browser/ShareWithFriendDialog.svelte';
  import AddToPlaylistSheet from '#lib/components/v2/AddToPlaylistSheet.svelte';
  import ReportDownloadSheet from '#lib/components/v2/ReportDownloadSheet.svelte';
  import { albumKeyForSong, type ApiSong } from '#lib/api-client.ts';
  import { can, isAdmin } from '#lib/auth/capabilities.ts';
  import { findShareLink, shareLink, type ShareLink } from '#lib/share-links.ts';
  import { sendTo, sendToSong } from '#lib/stores/send-to.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { artistOf, titleOf } from '#lib/track-list-view.svelte.ts';
  import { queueSong } from '#lib/queue/queue-actions.ts';

  // The one action set for a song wherever it is listed — Tracks rows, album tracklists, the
  // Overview's favourites, an artist's songs — behind a visible ⋯ button and, on the row, a
  // touch-and-hold / right-click twin (the row calls `openAt`). A menu at md+, a bottom sheet on a
  // phone, both drawn from the one `groups` list below. iOS context-menu rules: at most
  // three groups by frequency, unavailable items hidden rather than disabled, destructive last.
  //   1. Play · Play next · Add to queue (a row's swipe right is the twin of the last)
  //   2. Add to / Remove from favourites · Add to playlist… · Go to album · Go to artist
  //   3. Song info · Share link… · Send to… · Share with a friend… · View timeline
  // and, on a playlist's own page, Remove from playlist — destructive, so last and on its own; for
  // the owner, on a track MusicHoarder downloaded, Report wrong download… (it removes the track).
  type Props = {
    song: ApiSong;
    /**
     * Plays the list this row belongs to, starting at it. The caller owns the queue, and must
     * start it with `playerStore.startQueue`: an item called Play never pauses.
     */
    onplay: () => void;
    /** The album the Now Playing sheet should page through for Song info. */
    albumKey?: string;
    /** Off on the album's own page, where "Go to album" would go nowhere. */
    showAlbum?: boolean;
    /** Off on the artist's own view. */
    showArtist?: boolean;
    /** On a playlist's page, for a song added there: removes it from that playlist. */
    onremove?: () => void;
    /** Classes for the ⋯ trigger (a desktop row hides it until hover). */
    class?: string;
  };

  const {
    song,
    onplay,
    albumKey,
    showAlbum = true,
    showArtist = true,
    onremove,
    class: className
  }: Props = $props();

  const user = $derived(page.data.user);
  const admin = $derived(isAdmin(user));
  // Public links and grants are the library owner's to hand out; neither exists for someone
  // else's rows.
  const canShare = $derived(admin);
  const canShareWithPeople = $derived(admin && can(user, 'ManageOwnShares'));
  // Only a download can be reported and removed: a file from the owner's own music folder is never
  // deleted by MusicHoarder, so the server would refuse it.
  const canReport = $derived(admin && song.originKind === 'Downloaded');

  const title = $derived(titleOf(song));
  const isLiked = $derived(Boolean(song.likedAtUtc));
  const albumHref = $derived(
    song.album ? `/library?album=${encodeURIComponent(albumKeyForSong(song))}` : null
  );
  const artistHref = $derived(`/library?artist=${encodeURIComponent(artistOf(song))}`);

  let open = $state(false);
  // A list builds one of these per row, and the menu machinery (the popper, its anchor, the focus
  // and dismiss layers) was close to half of what a row cost to create, paid again for every row
  // a scroll or a search brings in. Until it is first opened the menu is only its ⋯ button.
  let mounted = $state(false);
  // Set while the menu is open from a touch-and-hold or a right-click: the content then anchors at
  // that point instead of at the ⋯ button, the way an iOS context menu grows from the finger.
  let anchor = $state<LongPressPoint | null>(null);
  const customAnchor = $derived.by(() => {
    const point = anchor;
    if (!point) return null;
    return { getBoundingClientRect: () => new DOMRect(point.x, point.y, 0, 0) };
  });

  function setOpen(next: boolean) {
    if (next) mounted = true;
    open = next;
    if (next) lookUpShare();
    else anchor = null;
  }

  /** Open the menu at a point — the row's long-press and right-click. */
  export function openAt(point: LongPressPoint) {
    anchor = point;
    setOpen(true);
  }

  // ── Share link… ───────────────────────────────────────────────────────────────
  // Opening the menu only LOOKS for a link this song already has (see share-links.ts): minting on
  // open published a public link every time an admin opened a menu for anything else. With a link
  // in hand, Share link… opens the share sheet straight from the tap; without one it mints then.
  let known = $state<{ id: number; link: ShareLink | null } | null>(null);

  function lookUpShare() {
    if (!canShare || known?.id === song.id) return;
    const id = song.id;
    void findShareLink([id], 'song').then((link) => {
      if (song.id === id) known = { id, link };
    });
  }

  function share() {
    shareLink({ known: known?.id === song.id ? known.link : null, songId: song.id, scope: 'song' });
    // Whatever happens next, a link exists afterwards: look again on the next open.
    known = null;
  }

  // ── the rest ───────────────────────────────────────────────────────────────
  let friendsOpen = $state(false);
  // Mounted on first use and then kept, so the sheet animates out instead of vanishing: a list
  // renders one of these menus per row, and most never share anything.
  let friendsMounted = $state(false);
  function openFriends() {
    friendsMounted = true;
    friendsOpen = true;
  }

  // Mounted on first use, like the friends sheet.
  let playlistOpen = $state(false);
  let playlistMounted = $state(false);
  function openAddToPlaylist() {
    playlistMounted = true;
    playlistOpen = true;
  }

  // Mounted on first use, like the friends sheet.
  let reportOpen = $state(false);
  let reportMounted = $state(false);
  function openReport() {
    reportMounted = true;
    reportOpen = true;
  }

  async function toggleLike() {
    try {
      await songsStore.toggleLike(song.id);
    } catch (err) {
      toast.error('Could not update favourites', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  // Song info opens the Now Playing sheet on its info page rather than the player.
  function songInfo() {
    songDetail.open(song.id, albumKey, { mode: 'info' });
  }

  type RowAction = {
    label: string;
    icon: Component;
    run: () => void;
    destructive?: boolean;
  };

  // Read only once the menu or sheet is open, so a row that is never opened never builds it.
  const groups = $derived.by((): RowAction[][] => {
    const playback: RowAction[] = [
      { label: 'Play', icon: Play, run: onplay },
      { label: 'Play next', icon: ListStart, run: () => queueSong(song, 'next') },
      { label: 'Add to queue', icon: ListEnd, run: () => queueSong(song, 'last') }
    ];
    const library: RowAction[] = [
      isLiked
        ? { label: 'Remove from favourites', icon: HeartOff, run: toggleLike }
        : { label: 'Add to favourites', icon: Heart, run: toggleLike },
      { label: 'Add to playlist…', icon: ListPlus, run: openAddToPlaylist }
    ];
    if (showAlbum && albumHref) {
      const href = albumHref;
      library.push({ label: 'Go to album', icon: Disc3, run: () => void goto(href) });
    }
    if (showArtist) {
      library.push({ label: 'Go to artist', icon: Mic2, run: () => void goto(artistHref) });
    }
    const about: RowAction[] = [{ label: 'Song info', icon: Info, run: songInfo }];
    if (canShare) {
      about.push(
        { label: 'Share link…', icon: Share, run: share },
        { label: 'Send to…', icon: Send, run: () => sendTo.show(sendToSong(song)) }
      );
    }
    if (canShareWithPeople && song.album) {
      about.push({ label: 'Share with a friend…', icon: UsersRound, run: openFriends });
    }
    if (admin) {
      about.push({
        label: 'View timeline',
        icon: History,
        run: () => void goto(`/track/${song.id}`)
      });
    }
    const removal: RowAction[] = [];
    if (onremove) {
      removal.push({
        label: 'Remove from playlist',
        icon: ListMinus,
        run: onremove,
        destructive: true
      });
    }
    if (canReport) {
      removal.push({
        label: 'Report wrong download…',
        icon: Flag,
        run: openReport,
        destructive: true
      });
    }
    return [playback, library, about, removal].filter((group) => group.length > 0);
  });

  // The sheet closes before its action runs, as an iOS action sheet does, so the sheet or page
  // the action opens is the only thing on screen.
  function pick(action: RowAction) {
    setOpen(false);
    action.run();
  }
</script>

{#if !mounted || isCompact()}
  <!-- A plain element with the trigger's look: the Button component and an icon component per row
       were, after the menu itself, most of what was left of a row's cost. On a phone it stays the
       trigger, for the sheet below. -->
  <button
    type="button"
    data-slot="button"
    aria-label="More for {title}"
    aria-haspopup={isCompact() ? 'dialog' : 'menu'}
    aria-expanded={open}
    class={cn(RESTING_TRIGGER, className)}
    onclick={(e) => {
      // The row behind this button plays or opens the song on click; the menu must not.
      e.stopPropagation();
      setOpen(true);
    }}
    onkeydown={(e) => {
      // Rows handle Enter/Space themselves; keep the button's own key press from reaching them.
      e.stopPropagation();
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setOpen(true);
      }
    }}
  >
    <!-- lucide's ellipsis, inline -->
    <svg
      class="size-5"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="1" /><circle cx="19" cy="12" r="1" /><circle
        cx="5"
        cy="12"
        r="1"
      />
    </svg>
  </button>
{:else}
  <DropdownMenu.Root bind:open={() => open, setOpen}>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <Button
          {...props}
          variant="ghost"
          size="icon"
          aria-label="More for {title}"
          class={cn(
            'text-muted-foreground hover:text-foreground aria-expanded:text-foreground shrink-0 rounded-full pointer-coarse:size-11',
            className
          )}
          onclick={(e: MouseEvent) => {
            // The row behind this button plays or opens the song on click; the menu must not.
            e.stopPropagation();
            (props.onclick as ((ev: MouseEvent) => void) | undefined)?.(e);
          }}
          onkeydown={(e: KeyboardEvent) => {
            // Rows handle Enter/Space themselves; keep the trigger's own key press from reaching them.
            e.stopPropagation();
            (props.onkeydown as ((ev: KeyboardEvent) => void) | undefined)?.(e);
          }}
        >
          <Ellipsis class="size-5" />
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>
    <!-- Capped at the room the screen has: opened from a row mid-way down a phone, the full menu is
       taller than the space on either side, and it would run off the top, Play and all. -->
    <DropdownMenu.Content
      {customAnchor}
      side="bottom"
      align={anchor ? 'start' : 'end'}
      class="max-h-(--bits-dropdown-menu-content-available-height) w-60 pointer-coarse:w-72"
    >
      {#each groups as group, i (i)}
        {#if i > 0}<DropdownMenu.Separator />{/if}
        <DropdownMenu.Group>
          {#each group as action (action.label)}
            <DropdownMenu.Item
              variant={action.destructive ? 'destructive' : 'default'}
              onSelect={action.run}
            >
              <action.icon />
              {action.label}
            </DropdownMenu.Item>
          {/each}
        </DropdownMenu.Group>
      {/each}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/if}

{#if mounted && isCompact()}
  <!-- showClose off: Cancel is the way out, at the bottom where the thumb is (see ActionSheet). -->
  <BottomSheet.Root
    bind:open={() => open, setOpen}
    {title}
    description={[artistOf(song), song.album].filter(Boolean).join(' · ')}
    showClose={false}
  >
    <div class="flex flex-col gap-4">
      {#each groups as group, i (i)}
        <GroupedList.Section>
          {#each group as action (action.label)}
            <GroupedList.Row
              label={action.label}
              destructive={action.destructive}
              onclick={() => pick(action)}
            >
              {#snippet trailing()}
                <action.icon
                  aria-hidden="true"
                  class={cn(
                    'size-5',
                    action.destructive ? 'text-destructive-text' : 'text-muted-foreground'
                  )}
                />
              {/snippet}
            </GroupedList.Row>
          {/each}
        </GroupedList.Section>
      {/each}
      <GroupedList.Section>
        <GroupedList.Row onclick={() => setOpen(false)}>
          <span class="text-body text-primary text-center font-semibold">Cancel</span>
        </GroupedList.Row>
      </GroupedList.Section>
    </div>
  </BottomSheet.Root>
{/if}

{#if playlistMounted}
  <AddToPlaylistSheet bind:open={playlistOpen} songIds={[song.id]} label={title} />
{/if}

{#if reportMounted}
  <ReportDownloadSheet bind:open={reportOpen} songId={song.id} label={title} />
{/if}

{#if friendsMounted && song.album}
  <!-- Grants are per album (or wider), so sharing a song with someone shares its album; the sheet
       names the album it is about to share. -->
  <ShareWithFriendDialog
    bind:open={friendsOpen}
    artist={(song.albumArtist ?? song.artist ?? '').trim() || artistOf(song)}
    album={song.album}
  />
{/if}
