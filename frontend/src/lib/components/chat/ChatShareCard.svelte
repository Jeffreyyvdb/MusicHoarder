<script lang="ts">
  import { goto } from '$app/navigation';
  import { Disc3, ExternalLink, Music, Pause, Play } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { getSongCoverUrl, type ChatShare } from '$lib/api-client';
  import { playChatShare } from '$lib/chat/play-shared';
  import { shareCoverUrl } from '$lib/share-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  // A song or album sent in a chat: its cover, what it is, and Play — which plays it here, in the
  // app's own player, through the share link it travels as. Open shows the share page (lyrics, the
  // music video, the whole tracklist). A link the sender has since turned off stays in the chat,
  // saying so, rather than vanishing from the conversation.
  type Props = { share: ChatShare; class?: string };
  const { share, class: className }: Props = $props();

  const isAlbum = $derived(share.scope === 'Album');
  const coverUrl = $derived.by(() => {
    if (!share.hasCover) return null;
    if (share.ownedByViewer) return getSongCoverUrl(share.songId, 160);
    return share.token ? shareCoverUrl(share.token, share.songId, 160) : null;
  });
  const kindLine = $derived(
    [isAlbum ? 'Album' : 'Song', share.artist, isAlbum && share.year ? String(share.year) : null]
      .filter(Boolean)
      .join(' · ')
  );

  const loaded = $derived(playerStore.currentSong?.id === share.songId);
  const playing = $derived(loaded && playerStore.isPlaying);
  let starting = $state(false);

  async function play() {
    if (loaded) {
      playerStore.togglePlay();
      return;
    }
    starting = true;
    try {
      await playChatShare(share);
    } finally {
      starting = false;
    }
  }
</script>

<div
  class={cn(
    'bg-card flex w-full max-w-sm items-center gap-3 rounded-2xl border p-2.5 text-left',
    share.revoked && 'opacity-80',
    className
  )}
>
  {#if coverUrl}
    <Cover
      artist={share.artist ?? ''}
      title={share.title}
      {coverUrl}
      size={56}
      corner={isAlbum ? 6 : 8}
      dprCap={3}
      caption={false}
    />
  {:else}
    <span class="bg-muted text-muted-foreground grid size-14 shrink-0 place-items-center rounded-lg">
      {#if isAlbum}<Disc3 class="size-6" aria-hidden="true" />{:else}<Music class="size-6" aria-hidden="true" />{/if}
    </span>
  {/if}

  <div class="min-w-0 flex-1">
    <p class="text-subheadline text-foreground truncate font-semibold md:text-sm">{share.title}</p>
    <p class="text-footnote text-muted-foreground truncate md:text-xs">{kindLine}</p>
    {#if share.revoked}
      <p class="text-footnote text-muted-foreground md:text-xs">No longer available</p>
    {/if}
  </div>

  {#if !share.revoked}
    {#if share.token}
      <button
        type="button"
        class="text-muted-foreground hover:text-foreground focus-visible:ring-ring grid size-11 shrink-0 place-items-center rounded-full outline-none focus-visible:ring-2 md:size-9"
        aria-label={`Open ${share.title}`}
        onclick={() => void goto(`/share/${encodeURIComponent(share.token ?? '')}`)}
      >
        <ExternalLink class="size-5 md:size-4" aria-hidden="true" />
      </button>
    {/if}
    <button
      type="button"
      class="bg-primary text-primary-foreground focus-visible:ring-ring grid size-11 shrink-0 place-items-center rounded-full outline-none focus-visible:ring-2 focus-visible:ring-offset-2 disabled:opacity-60 md:size-9"
      aria-label={playing ? `Pause ${share.title}` : `Play ${share.title}`}
      disabled={starting}
      onclick={play}
    >
      {#if playing}
        <Pause class="size-5 fill-current md:size-4" aria-hidden="true" />
      {:else}
        <Play class="ml-0.5 size-5 fill-current md:size-4" aria-hidden="true" />
      {/if}
    </button>
  {/if}
</div>
