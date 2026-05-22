<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { toast } from 'svelte-sonner';
  import { Search, ScanLine, Disc3, Loader2 } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import ProcessingStrip from '$lib/components/file-browser/ProcessingStrip.svelte';
  import { buildAlbumsFromSongs, sortAlbumsByRecency, triggerEnrichmentScan, type ApiSong } from '$lib/api-client';
  import { applySectionFilter, type SectionId } from '$lib/album-sections';

  type Props = {
    songs: ApiSong[];
    section: SectionId;
    searchQuery: string;
    isLoading: boolean;
  };
  const { songs, section, searchQuery, isLoading }: Props = $props();

  let query = $state(untrack(() => searchQuery));
  let debounce: ReturnType<typeof setTimeout> | null = null;

  function pushQuery(value: string) {
    const url = new URL(page.url);
    if (value.trim()) url.searchParams.set('q', value);
    else url.searchParams.delete('q');
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
  }

  function onInput(value: string) {
    query = value;
    if (debounce) clearTimeout(debounce);
    debounce = setTimeout(() => pushQuery(value), 180);
  }

  function setSection(id: SectionId) {
    const url = new URL(page.url);
    if (id === 'lib') url.searchParams.delete('section');
    else url.searchParams.set('section', id);
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  function openAlbum(key: string) {
    void goto(`/app?album=${encodeURIComponent(key)}`);
  }

  let scanning = $state(false);
  async function scanSource() {
    if (scanning) return;
    scanning = true;
    try {
      const result = await triggerEnrichmentScan();
      if (result.ok) toast.success('Scan started', { description: 'Scanning the source directory for new files.' });
      else toast.error('Could not start scan', { description: result.message });
    } catch {
      toast.error('Could not start scan', { description: 'The API may be unavailable.' });
    } finally {
      scanning = false;
    }
  }

  const albumsInSection = $derived(
    section === 'recent'
      ? sortAlbumsByRecency(buildAlbumsFromSongs(applySectionFilter(songs, section)))
      : buildAlbumsFromSongs(applySectionFilter(songs, section))
  );
  const filtered = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return albumsInSection;
    return albumsInSection.filter(
      (a) => a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q)
    );
  });

  const trackCount = $derived(songs.length);
  const artistCount = $derived(
    new Set(songs.map((s) => (s.albumArtist ?? s.artist ?? '').trim().toLowerCase()).filter(Boolean))
      .size
  );
  const queueCount = $derived(songs.filter((s) => !s.destinationPath).length);

  const chips = $derived(
    [
      { id: 'lib' as const, label: 'All albums', n: buildAlbumsFromSongs(songs).length },
      { id: 'recent' as const, label: 'Recent', n: buildAlbumsFromSongs(applySectionFilter(songs, 'recent')).length },
      { id: 'dupes' as const, label: 'Duplicates', n: buildAlbumsFromSongs(applySectionFilter(songs, 'dupes')).length },
      { id: 'missing' as const, label: 'Missing meta', n: buildAlbumsFromSongs(applySectionFilter(songs, 'missing')).length },
      { id: 'queue' as const, label: 'Queue', n: queueCount, accent: true }
    ] satisfies { id: SectionId; label: string; n: number; accent?: boolean }[]
  );

  const showProcessing = $derived(section === 'lib' || section === 'queue');
</script>

<div class="mob">
  <MobileHeader title="Library" sub="{trackCount.toLocaleString()} tracks · {artistCount.toLocaleString()} artists">
    {#snippet right()}
      <button class="mob-h-btn" aria-label="Scan source" disabled={scanning} onclick={scanSource}>
        {#if scanning}<Loader2 size={16} class="animate-spin" />{:else}<ScanLine size={16} />{/if}
      </button>
    {/snippet}
  </MobileHeader>

  <div class="mob-scroll">
    <div class="mob-search">
      <Search size={14} class="text-muted-foreground" />
      <input
        placeholder="Search albums, artists, tracks…"
        value={query}
        oninput={(e) => onInput(e.currentTarget.value)}
      />
    </div>

    <div class="mob-chips">
      {#each chips as chip (chip.id)}
        <button
          class="mob-chip {section === chip.id ? 'active' : ''}"
          onclick={() => setSection(chip.id)}
        >
          {#if chip.accent && section !== chip.id}<span class="mh-pulse"></span>{/if}
          {chip.label}
          <span class="font-mono opacity-60">{chip.n.toLocaleString()}</span>
        </button>
      {/each}
    </div>

    {#if showProcessing}
      <div class="px-4">
        <ProcessingStrip />
      </div>
    {/if}

    {#if isLoading && filtered.length === 0}
      <div class="text-muted-foreground px-6 py-16 text-center text-sm">Loading albums…</div>
    {:else if filtered.length > 0}
      <div class="mob-grid">
        {#each filtered as album (album.key)}
          <button class="mob-tile" onclick={() => openAlbum(album.key)}>
            <div class="mob-tile-cover">
              <Cover
                artist={album.artist}
                title={album.title}
                coverUrl={album.coverUrl}
                size={180}
                corner={8}
                caption={false}
              />
            </div>
            <div class="mob-tile-title">{album.title}</div>
            <div class="mob-tile-artist">
              {album.artist}{#if album.year} · <span class="font-mono">{album.year}</span>{/if}
            </div>
          </button>
        {/each}
      </div>
    {:else}
      <div class="px-6 py-16 text-center">
        <Disc3 class="text-muted-foreground/40 mx-auto mb-3" size={32} />
        <div class="text-sm font-medium">Nothing here</div>
        <div class="text-muted-foreground mt-1 text-[13px]">Try a different search or filter.</div>
      </div>
    {/if}
  </div>
</div>
