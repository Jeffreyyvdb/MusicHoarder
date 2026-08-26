<script lang="ts">
  import { Library, Loader2, UserRoundPlus } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as Dialog from '$lib/components/ui/dialog';
  import { Button } from '$lib/components/ui/button';
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
    album
  }: { open?: boolean; artist: string; album: string } = $props();

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

<Dialog.Root bind:open>
  <Dialog.Content class="sm:max-w-md">
    <Dialog.Header>
      <Dialog.Title>Share with a friend</Dialog.Title>
      <Dialog.Description>
        Pick who can see and stream <span class="text-foreground font-medium">{album}</span> by
        <span class="text-foreground font-medium">{artist}</span>. It shows up in their library,
        marked as shared by you.
      </Dialog.Description>
    </Dialog.Header>

    {#if isLoading}
      <div class="flex items-center justify-center py-8">
        <Loader2 class="text-muted-foreground size-5 animate-spin" />
      </div>
    {:else if friends.length === 0}
      <div class="space-y-3 py-4 text-center">
        <p class="text-muted-foreground text-sm">You haven't invited any friends yet.</p>
        <Button variant="outline" size="sm" href="/settings?tab=people">
          <UserRoundPlus class="size-4" />
          Invite a friend
        </Button>
      </div>
    {:else}
      <ul class="border-border divide-border divide-y rounded-lg border">
        {#each friends as friend (friend.id)}
          {@const covered = hasLibraryGrant(friend)}
          {@const viaArtist = !covered && Boolean(artistGrantOf(friend))}
          {@const shared = Boolean(albumGrantOf(friend))}
          <li class="flex items-center gap-3 px-4 py-2.5">
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm">{friend.displayName ?? friend.email}</div>
              {#if covered}
                <div class="text-muted-foreground flex items-center gap-1 text-xs">
                  <Library class="size-3" /> Already has your entire library
                </div>
              {:else if viaArtist}
                <div class="text-muted-foreground text-xs">Already has this artist</div>
              {/if}
            </div>
            {#if busyId === friend.id}
              <Loader2 class="text-muted-foreground size-4 animate-spin" />
            {:else}
              <input
                type="checkbox"
                class="accent-primary size-4"
                checked={covered || viaArtist || shared}
                disabled={covered || viaArtist}
                aria-label={`Share ${album} with ${friend.email}`}
                onchange={() => toggle(friend)}
              />
            {/if}
          </li>
        {/each}
      </ul>
      <p class="text-muted-foreground text-xs">
        Artist- and library-wide sharing is managed in
        <a href="/settings?tab=people" class="underline underline-offset-2">Settings → People</a>.
      </p>
    {/if}
  </Dialog.Content>
</Dialog.Root>
