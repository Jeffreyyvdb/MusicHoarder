<script lang="ts">
  import { ListPlus, Loader2 } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import PlaylistCover from '$lib/components/v2/PlaylistCover.svelte';
  import { playlistSubtitle } from '$lib/playlists';
  import { playlistsStore } from '$lib/stores/playlists.svelte';

  // "Add to Playlist": Apple Music's sheet — New Playlist… first, then every playlist, most recently
  // changed first. A synced (Spotify / Deezer / YouTube) playlist takes additions too: they play
  // after its own tracks, and stay in MusicHoarder. Picking one adds and closes, with a toast that
  // says what happened (a song already on it is not added twice).
  type Props = {
    open?: boolean;
    songIds: number[];
    /** What is being added, for the sheet's subtitle ("One More Time", "Discovery"). */
    label: string;
    /** Opened from inside Now Playing: stacks above it (z-70) in its dark media appearance. */
    nested?: boolean;
  };

  let { open = $bindable(false), songIds, label, nested = false }: Props = $props();

  let mode = $state<'pick' | 'create'>('pick');
  let busyId = $state<number | 'new' | null>(null);
  let name = $state('');
  let input = $state<HTMLInputElement | null>(null);

  $effect(() => {
    if (open) {
      mode = 'pick';
      name = '';
      busyId = null;
      playlistsStore.revalidate();
    }
  });

  $effect(() => {
    if (mode === 'create') queueMicrotask(() => input?.focus());
  });

  const recent = $derived(
    [...playlistsStore.playlists].sort((a, b) => b.updatedAtUtc.localeCompare(a.updatedAtUtc))
  );

  const what = $derived(songIds.length === 1 ? label : `${songIds.length} tracks`);

  async function addTo(id: number) {
    if (busyId !== null) return;
    busyId = id;
    try {
      const result = await playlistsStore.addSongs(id, songIds);
      const target = result.playlist.name;
      if (result.added === 0) toast.info(`Already in ${target}`);
      else if (result.alreadyPresent > 0)
        toast.success(`Added ${result.added} to ${target}`, {
          description: `${result.alreadyPresent} ${result.alreadyPresent === 1 ? 'was' : 'were'} already on it.`
        });
      else toast.success(`Added to ${target}`);
      open = false;
    } catch (err) {
      toast.error('Could not add to the playlist', {
        description: err instanceof Error ? err.message : undefined
      });
    } finally {
      busyId = null;
    }
  }

  async function create() {
    const trimmed = name.trim();
    if (!trimmed || busyId !== null) return;
    busyId = 'new';
    try {
      const playlist = await playlistsStore.create(trimmed, songIds);
      toast.success(`Added to ${playlist.name}`);
      open = false;
    } catch (err) {
      toast.error('Could not make the playlist', {
        description: err instanceof Error ? err.message : undefined
      });
    } finally {
      busyId = null;
    }
  }
</script>

<BottomSheet.Root
  bind:open
  {nested}
  class={nested ? 'dark' : undefined}
  title={mode === 'create' ? 'New Playlist' : 'Add to Playlist'}
  description={what}
>
  {#snippet leading()}
    {#if mode === 'create'}
      <BottomSheet.Action onclick={() => (mode = 'pick')}>Back</BottomSheet.Action>
    {:else}
      <BottomSheet.Action onclick={() => (open = false)}>Cancel</BottomSheet.Action>
    {/if}
  {/snippet}
  {#snippet trailing()}
    {#if mode === 'create'}
      <BottomSheet.Action prominent onclick={create} disabled={!name.trim() || busyId !== null}>
        {#if busyId === 'new'}<Loader2 class="size-4 animate-spin" />{/if}
        Create
      </BottomSheet.Action>
    {/if}
  {/snippet}

  <div class="flex flex-col gap-7 pt-1 pb-2">
    {#if mode === 'create'}
      <GroupedList.Section footer="The new playlist starts with {what}.">
        <GroupedList.Row>
          <input
            bind:this={input}
            bind:value={name}
            type="text"
            maxlength={200}
            autocapitalize="sentences"
            autocomplete="off"
            enterkeyhint="done"
            aria-label="Playlist name"
            placeholder="Playlist name"
            class="placeholder:text-muted-foreground text-body w-full min-w-0 bg-transparent py-2.5 outline-none md:py-1.5 md:text-sm"
            onkeydown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                void create();
              }
            }}
          />
        </GroupedList.Row>
      </GroupedList.Section>
    {:else}
      <GroupedList.Section>
        <GroupedList.Row label="New Playlist…" icon={ListPlus} onclick={() => (mode = 'create')} />
      </GroupedList.Section>

      {#if playlistsStore.isLoading && !playlistsStore.hasLoaded}
        <p
          class="text-subheadline text-muted-foreground flex items-center justify-center gap-2 py-6"
        >
          <Loader2 class="size-4 animate-spin" /> Loading playlists…
        </p>
      {:else if playlistsStore.error && !playlistsStore.hasLoaded}
        <p class="text-subheadline text-destructive-text px-4 py-6 text-center">
          {playlistsStore.error}
        </p>
      {:else if recent.length > 0}
        <GroupedList.Section header="Playlists">
          {#each recent as playlist (playlist.id)}
            <GroupedList.Row
              label={playlist.name}
              sublabel={playlistSubtitle(playlist)}
              disabled={busyId !== null}
              onclick={() => void addTo(playlist.id)}
            >
              {#snippet leading()}
                <PlaylistCover {playlist} size={44} corner={6} dprCap={3} />
              {/snippet}
              {#snippet trailing()}
                {#if busyId === playlist.id}
                  <Loader2 class="text-muted-foreground size-4 animate-spin" />
                {/if}
              {/snippet}
            </GroupedList.Row>
          {/each}
        </GroupedList.Section>
      {/if}
    {/if}
  </div>
</BottomSheet.Root>
