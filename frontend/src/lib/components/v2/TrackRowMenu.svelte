<script lang="ts" module>
  import { playerStore } from '$lib/stores/player.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { tapActionFor } from '$lib/track-list-view.svelte';

  /**
   * A row tap on a phone (the tap rule, mirrored by the Android client's `RowTap`): a song that is not the
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
</script>

<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    Disc3,
    Ellipsis,
    Heart,
    HeartOff,
    History,
    Info,
    ListMinus,
    ListPlus,
    Mic2,
    Play,
    Send,
    Share,
    UsersRound
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import type { LongPressPoint } from '$lib/actions/long-press';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import ShareWithFriendDialog from '$lib/components/file-browser/ShareWithFriendDialog.svelte';
  import AddToPlaylistSheet from '$lib/components/v2/AddToPlaylistSheet.svelte';
  import { albumKeyForSong, type ApiSong } from '$lib/api-client';
  import { can, isAdmin } from '$lib/auth/capabilities';
  import { findShareLink, shareLink, type ShareLink } from '$lib/share-links';
  import { sendTo, sendToSong } from '$lib/stores/send-to.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { artistOf, titleOf } from '$lib/track-list-view.svelte';
  import { cn } from '$lib/utils';

  // The one action set for a song wherever it is listed — Tracks rows, album tracklists, the
  // Overview's favourites, an artist's songs — behind a visible ⋯ button and, on the row, a
  // touch-and-hold / right-click twin (the row calls `openAt`). iOS context-menu rules: at most
  // three groups by frequency, unavailable items hidden rather than disabled, destructive last.
  //   1. Play (Play next is omitted: the player has no insert-into-queue API)
  //   2. Add to / Remove from favourites · Add to playlist… · Go to album · Go to artist
  //   3. Song info · Share link… · Send to… · Share with a friend… · View timeline
  // and, on a playlist's own page, Remove from playlist — destructive, so last and on its own.
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

  const title = $derived(titleOf(song));
  const isLiked = $derived(Boolean(song.likedAtUtc));
  const albumHref = $derived(
    song.album ? `/library?album=${encodeURIComponent(albumKeyForSong(song))}` : null
  );
  const artistHref = $derived(`/library?artist=${encodeURIComponent(artistOf(song))}`);

  let open = $state(false);
  // Set while the menu is open from a touch-and-hold or a right-click: the content then anchors at
  // that point instead of at the ⋯ button, the way an iOS context menu grows from the finger.
  let anchor = $state<LongPressPoint | null>(null);
  const customAnchor = $derived.by(() => {
    const point = anchor;
    if (!point) return null;
    return { getBoundingClientRect: () => new DOMRect(point.x, point.y, 0, 0) };
  });

  function setOpen(next: boolean) {
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
</script>

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
  <DropdownMenu.Content
    {customAnchor}
    side="bottom"
    align={anchor ? 'start' : 'end'}
    class="w-60 pointer-coarse:w-72"
  >
    <DropdownMenu.Group>
      <DropdownMenu.Item onSelect={onplay}>
        <Play /> Play
      </DropdownMenu.Item>
    </DropdownMenu.Group>
    <DropdownMenu.Separator />
    <DropdownMenu.Group>
      <DropdownMenu.Item onSelect={toggleLike}>
        {#if isLiked}
          <HeartOff /> Remove from favourites
        {:else}
          <Heart /> Add to favourites
        {/if}
      </DropdownMenu.Item>
      <DropdownMenu.Item onSelect={openAddToPlaylist}>
        <ListPlus /> Add to playlist…
      </DropdownMenu.Item>
      {#if showAlbum && albumHref}
        <DropdownMenu.Item onSelect={() => void goto(albumHref)}>
          <Disc3 /> Go to album
        </DropdownMenu.Item>
      {/if}
      {#if showArtist}
        <DropdownMenu.Item onSelect={() => void goto(artistHref)}>
          <Mic2 /> Go to artist
        </DropdownMenu.Item>
      {/if}
    </DropdownMenu.Group>
    <DropdownMenu.Separator />
    <DropdownMenu.Group>
      <DropdownMenu.Item onSelect={songInfo}>
        <Info /> Song info
      </DropdownMenu.Item>
      {#if canShare}
        <DropdownMenu.Item onSelect={share}>
          <Share /> Share link…
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={() => sendTo.show(sendToSong(song))}>
          <Send /> Send to…
        </DropdownMenu.Item>
      {/if}
      {#if canShareWithPeople && song.album}
        <DropdownMenu.Item onSelect={openFriends}>
          <UsersRound /> Share with a friend…
        </DropdownMenu.Item>
      {/if}
      {#if admin}
        <DropdownMenu.Item onSelect={() => void goto(`/track/${song.id}`)}>
          <History /> View timeline
        </DropdownMenu.Item>
      {/if}
    </DropdownMenu.Group>
    {#if onremove}
      <DropdownMenu.Separator />
      <DropdownMenu.Group>
        <DropdownMenu.Item variant="destructive" onSelect={onremove}>
          <ListMinus /> Remove from playlist
        </DropdownMenu.Item>
      </DropdownMenu.Group>
    {/if}
  </DropdownMenu.Content>
</DropdownMenu.Root>

{#if playlistMounted}
  <AddToPlaylistSheet bind:open={playlistOpen} songIds={[song.id]} label={title} />
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
