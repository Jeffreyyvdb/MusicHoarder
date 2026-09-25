<script lang="ts">
  import { Loader2, UserRoundPlus } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Button } from '$lib/components/ui/button';
  import { Switch } from '$lib/components/ui/switch';
  import {
    createFriendGrant,
    listFriends,
    revokeFriendGrant,
    type FriendView
  } from '$lib/api-client';

  /**
   * Per-album friend sharing: each friend row is a toggle for "can this person see this album".
   * Ticking creates an Album-scope grant, unticking revokes it. Friends who already hold an
   * Entire-library grant show as covered and can't be unticked here (that lives in Settings →
   * People, where the wider grant is managed).
   */
  let {
    open = $bindable(false),
    artist,
    album,
    nested = false,
    onnavigate
  }: {
    open?: boolean;
    artist: string;
    album: string;
    /** Opened from inside Now Playing: stacks above it (z-70) in its dark media appearance. */
    nested?: boolean;
    /** A link to Settings → People was followed; Now Playing closes so the page shows. */
    onnavigate?: () => void;
  } = $props();

  // The links lead to another page: the sheet (and whatever it was opened over) gets out of the way.
  function leave() {
    open = false;
    onnavigate?.();
  }

  let friends = $state<FriendView[]>([]);
  let isLoading = $state(false);
  let busyId = $state<string | null>(null);

  $effect(() => {
    if (!open) {
      friends = [];
      busyId = null;
      return;
    }
    void (async () => {
      isLoading = true;
      try {
        friends = (await listFriends()).filter((f) => !f.isDisabled);
      } catch (err) {
        toast.error(err instanceof Error ? err.message : 'Could not load your friends.');
      } finally {
        isLoading = false;
      }
    })();
  });

  function albumGrantOf(friend: FriendView) {
    return friend.grants.find(
      (g) =>
        g.scope === 'Album' &&
        (g.artist ?? '').toLowerCase() === artist.toLowerCase() &&
        (g.album ?? '').toLowerCase() === album.toLowerCase()
    );
  }

  function artistGrantOf(friend: FriendView) {
    return friend.grants.find(
      (g) => g.scope === 'Artist' && (g.artist ?? '').toLowerCase() === artist.toLowerCase()
    );
  }

  function hasLibraryGrant(friend: FriendView) {
    return friend.grants.some((g) => g.scope === 'Library');
  }

  async function toggle(friend: FriendView) {
    if (busyId) return;
    busyId = friend.id;
    try {
      const existing = albumGrantOf(friend);
      if (existing) {
        await revokeFriendGrant(friend.id, existing.id);
      } else {
        await createFriendGrant(friend.id, { scope: 'album', artist, album });
      }
      friends = (await listFriends()).filter((f) => !f.isDisabled);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not update that share.');
    } finally {
      busyId = null;
    }
  }
</script>

<!-- A sheet on a phone, a dialog on desktop: one switch row per invited account, so the whole
     44pt row is the control's label rather than a 16px checkbox. -->
<BottomSheet.Root
  bind:open
  {nested}
  class={nested ? 'dark' : undefined}
  title="Share with a friend"
  description="Pick who can see and stream {album} by {artist}. It shows up in their library, marked as shared by you."
>
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={() => (open = false)}>Done</BottomSheet.Action>
  {/snippet}

  {#if isLoading}
    <div class="flex items-center justify-center py-8">
      <Loader2 class="text-muted-foreground size-5 animate-spin" aria-label="Loading" />
    </div>
  {:else if friends.length === 0}
    <div class="flex flex-col items-center gap-3 px-4 py-6 text-center">
      <p class="text-body text-muted-foreground md:text-sm">You haven't invited anyone yet.</p>
      <Button
        variant="gray"
        size="pill"
        href="/settings?tab=people"
        onclick={leave}
        class="text-primary md:h-8 md:text-sm"
      >
        <UserRoundPlus />
        Invite someone
      </Button>
    </div>
  {:else}
    {#snippet peopleFooter()}
      Sharing a whole artist or your entire library is managed in
      <a
        href="/settings?tab=people"
        onclick={leave}
        class="text-primary underline-offset-2 hover:underline">Settings → People</a
      >.
    {/snippet}
    <GroupedList.Section header="People" footer={peopleFooter}>
      {#each friends as friend (friend.id)}
        {@const covered = hasLibraryGrant(friend)}
        {@const viaArtist = !covered && Boolean(artistGrantOf(friend))}
        {@const shared = Boolean(albumGrantOf(friend))}
        <GroupedList.Row
          label={friend.displayName ?? friend.email}
          sublabel={covered
            ? 'Already has your entire library'
            : viaArtist
              ? 'Already has this artist'
              : undefined}
        >
          {#snippet trailing()}
            {#if busyId === friend.id}
              <Loader2 class="text-muted-foreground size-5 animate-spin" aria-label="Saving" />
            {:else}
              <Switch
                checked={covered || viaArtist || shared}
                disabled={covered || viaArtist}
                aria-label={`Share ${album} with ${friend.displayName ?? friend.email}`}
                onCheckedChange={() => toggle(friend)}
              />
            {/if}
          {/snippet}
        </GroupedList.Row>
      {/each}
    </GroupedList.Section>
  {/if}
</BottomSheet.Root>
