<script lang="ts">
  import {
    AlertCircle,
    Check,
    Copy,
    History,
    LibraryBig,
    Loader2,
    Search,
    Sparkles
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Badge, type BadgeVariant } from '$lib/components/ui/badge';
  import { SegmentedControl } from '$lib/components/ui/segmented-control';
  import SourceRow from '$lib/components/file-browser/SourceRow.svelte';
  import ActionSheet from './ActionSheet.svelte';
  import {
    enrichSong,
    fetchEnrichmentDetail,
    fetchSongProvenance,
    mapEnrichmentStatus,
    resetSongEnrichment,
    fetchSongQualityGrade,
    gradeSong,
    copyQualitySongDossier,
    soulseek,
    type ApiSong,
    type AlbumSummary,
    type EnrichmentDetail,
    type ProviderAttempt,
    type ProvenanceGroup,
    type SongQualityGradeView,
    type QualityVerdict,
    type NormalizedEnrichmentStatus
  } from '$lib/api-client';
  import { fingerprintBars, fingerprintHash, providerAttemptRows } from '$lib/review-helpers';
  import {
    formatDate,
    formatDateTime,
    formatDuration,
    formatFileSize,
    formatReleaseDate
  } from '$lib/formatters';
  import { lrclibWebUrl, lrclibWebSearchUrl } from '$lib/lrclib-url';
  import { acoustIdSourceConnected, lrclibSourceConnected } from '$lib/source-connection';
  import { cn } from '$lib/utils';
  import { PROVENANCE_ICON, seedSublabel } from '$lib/provenance';
  import { songsStore } from '$lib/stores/songs.svelte';
  import type { LyricsStatus } from '$lib/types';

  /**
   * Now Playing's Info mode: Details for everyone, plus Fingerprint and Enrichment for admins
   * (their loads hit owner-only endpoints, and Demo is not an admin here either). Inset grouped
   * rows, iOS-Settings style: identifiers and paths are monospace and copy on tap; the admin
   * actions sit at the bottom with the one destructive action last, behind a confirmation.
   *
   * Owns every lazy load behind the inspectors (provider attempts, the AI grade, the Soulseek
   * probe). It stays mounted while the overlay is open, so a load settles once per song however
   * often the mode changes — the guards below are the ones TrackPanel had, moved verbatim.
   */
  type Section = 'details' | 'fingerprint' | 'enrichment';
  type Props = {
    album: AlbumSummary;
    song: ApiSong;
    trackIndex: number;
    /** Info is the mode on screen (its loads wait for this). */
    active: boolean;
    isOwner: boolean;
    trackTitle: string;
    trackArtist: string;
    artistHref: string;
    albumHref: string;
    lyricsStatus: LyricsStatus;
    /** "FLAC 1411" / "MP3 320" — the format row. */
    formatLabel: string;
    /** Follow a link out of the overlay (it closes first). */
    onNavigate: () => void;
    onResetEnrichment?: () => void;
    timelineHref?: string;
  };
  const {
    album,
    song,
    trackIndex,
    active,
    isOwner,
    trackTitle,
    trackArtist,
    artistHref,
    albumHref,
    lyricsStatus,
    formatLabel,
    onNavigate,
    onResetEnrichment,
    timelineHref
  }: Props = $props();

  let chosenSection = $state<Section>('details');
  // Members and Demo get Details only — no control, as a single segment would be.
  const section = $derived<Section>(isOwner ? chosenSection : 'details');
  const sectionItems: { value: Section; label: string }[] = [
    { value: 'details', label: 'Details' },
    { value: 'fingerprint', label: 'Fingerprint' },
    { value: 'enrichment', label: 'Enrichment' }
  ];
  // What the lazy loads key on: nothing loads while another mode has the screen.
  const loadSection = $derived(active ? section : null);

  let resetState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let resetError = $state<string | null>(null);
  let enrichState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let enrichOutcome = $state<string | null>(null);
  let enrichError = $state<string | null>(null);

  // Provider attempts (real candidate matches) are loaded lazily when the
  // Fingerprint tab is first viewed, and refetched when the song changes.
  let enrichmentDetail = $state<EnrichmentDetail | null>(null);
  let detailLoading = $state(false);
  let detailError = $state<string | null>(null);
  let loadedSongId = $state<number | null>(null);

  async function loadEnrichmentDetail(id: number) {
    detailLoading = true;
    detailError = null;
    try {
      const detail = await fetchEnrichmentDetail(id);
      if (id !== song.id) return; // song changed while in flight — discard
      enrichmentDetail = detail;
      loadedSongId = id;
    } catch (err) {
      if (id !== song.id) return; // stale failure for a song we navigated away from
      detailError = err instanceof Error ? err.message : 'Failed to load provider attempts';
    } finally {
      detailLoading = false; // ALWAYS clear — gating this on id === song.id deadlocks the effect
    }
  }

  $effect(() => {
    if (
      (loadSection !== 'fingerprint' && loadSection !== 'enrichment') ||
      detailLoading ||
      loadedSongId === song.id
    )
      return;
    void loadEnrichmentDetail(song.id);
  });

  // ── How it got here ─────────────────────────────────────────────────────────
  // Why this track is in the library: its reason, and for an album fill the tracks you already had
  // that started it. Your own songs only — a shared song's history is its owner's, and the endpoint
  // answers empty for it anyway. Loaded once per song, when Details is on screen.
  let provenance = $state<{ songId: number; group: ProvenanceGroup | null } | null>(null);
  $effect(() => {
    if (loadSection !== 'details' || songsStore.grantorOf(song)) return;
    const id = song.id;
    if (provenance?.songId === id) return;
    let cancelled = false;
    void fetchSongProvenance([id])
      .then((p) => {
        if (!cancelled) provenance = { songId: id, group: p.groups[0] ?? null };
      })
      .catch(() => {
        // Best-effort: Details simply goes without the section.
      });
    return () => {
      cancelled = true;
    };
  });
  const provenanceGroup = $derived(provenance?.songId === song.id ? provenance.group : null);
  const provenanceTrack = $derived(provenanceGroup?.tracks[0] ?? null);

  // ── Soulseek quality upgrade ────────────────────────────────────────────────
  // Shown only when slskd is configured; the /api/soulseek/* endpoints enforce owner-only.
  let soulseekConfigured = $state(false);
  let upgradeRequesting = $state(false);
  let upgradeError = $state<string | null>(null);

  $effect(() => {
    if (!isOwner) return; // owner-only endpoint; don't even probe for friends/demo
    let cancelled = false;
    void soulseek
      .getStatus()
      .then((s) => {
        if (!cancelled) soulseekConfigured = s.configured;
      })
      .catch(() => {
        // Endpoint unavailable (not owner / not configured) — keep the action hidden.
      });
    return () => {
      cancelled = true;
    };
  });

  // Reflect an in-flight upgrade as a disabled button label.
  const upgradeActiveLabel = $derived.by(() => {
    const u = enrichmentDetail?.upgrade;
    if (!u?.active) return null;
    switch (u.status) {
      case 'Queued':
        return 'Queued…';
      case 'Searching':
        return 'Searching…';
      case 'Downloading':
        return 'Downloading…';
      case 'AwaitingIngest':
        return 'Processing…';
      default:
        return 'Upgrading…';
    }
  });

  const upgradeTerminalNote = $derived.by(() => {
    const u = enrichmentDetail?.upgrade;
    if (!u || u.active) return null;
    if (u.status === 'NotFound') return 'No better copy found on Soulseek.';
    if (u.status === 'Failed') return u.error ? `Upgrade failed — ${u.error}` : 'Upgrade failed.';
    if (u.status === 'Deferred') return 'Provider was unavailable. Will retry automatically.';
    if (u.status === 'Completed') return 'Upgraded to a better copy.';
    return null;
  });

  async function handleFindBetterQuality() {
    if (upgradeRequesting || !song) return;
    upgradeRequesting = true;
    upgradeError = null;
    try {
      await soulseek.requestUpgrade({ songId: song.id });
      loadedSongId = null; // force a refetch so the button reflects the new active state
      await loadEnrichmentDetail(song.id);
    } catch (err) {
      upgradeError = err instanceof Error ? err.message : 'Failed to queue upgrade';
    } finally {
      upgradeRequesting = false;
    }
  }

  // AI quality grade for the Enrichment tab — loaded lazily, refetched per song.
  let quality = $state<SongQualityGradeView | null>(null);
  let qualityLoadedId = $state<number | null>(null);
  let gradeBusy = $state(false);
  let copied = $state(false);

  async function handleCopyDossier() {
    try {
      await copyQualitySongDossier(song.id);
      copied = true;
      setTimeout(() => (copied = false), 1500);
    } catch {
      // keep the panel quiet; failure leaves the icon unchanged
    }
  }

  $effect(() => {
    if (loadSection !== 'enrichment' || qualityLoadedId === song.id) return;
    const id = song.id;
    void (async () => {
      try {
        const grade = await fetchSongQualityGrade(id);
        if (id !== song.id) return; // song changed while in flight — discard
        quality = grade;
        qualityLoadedId = id;
      } catch {
        // grade is optional UI; ignore load failures
      }
    })();
  });

  async function handleGradeNow() {
    gradeBusy = true;
    try {
      await gradeSong(song.id);
      quality = await fetchSongQualityGrade(song.id);
      qualityLoadedId = song.id;
    } catch {
      // surfaced via the unchanged grade card; keep the panel quiet
    } finally {
      gradeBusy = false;
    }
  }

  // Verdicts as the contrast-checked Badge tints, each with its word — never a hue alone.
  function verdictVariant(v: QualityVerdict | undefined): BadgeVariant {
    switch (v) {
      case 'Excellent':
      case 'Good':
        return 'tinted';
      case 'Questionable':
        return 'warning';
      case 'Wrong':
        return 'destructive';
      default:
        return 'secondary';
    }
  }

  // Provider-attempt outcomes arrive as enum names; people read words.
  const ATTEMPT_WORDS: Record<string, string> = {
    Matched: 'Matched',
    NoMatch: 'No match',
    RateLimited: 'Rate limited',
    Failed: 'Failed'
  };
  function attemptWord(status: string): string {
    return ATTEMPT_WORDS[status] ?? status;
  }

  const trackN = $derived(song.trackNumber ?? trackIndex + 1);
  const totalTracks = $derived(album.trackCount);

  async function handleResetEnrichment() {
    resetState = 'loading';
    resetError = null;
    try {
      await resetSongEnrichment(song.id);
      resetState = 'success';
      onResetEnrichment?.();
      setTimeout(() => (resetState = 'idle'), 3000);
    } catch (err) {
      resetState = 'error';
      resetError = err instanceof Error ? err.message : 'Failed to reset enrichment';
      setTimeout(() => {
        resetState = 'idle';
        resetError = null;
      }, 5000);
    }
  }

  async function handleEnrichNow() {
    enrichState = 'loading';
    enrichError = null;
    enrichOutcome = null;
    try {
      // reset=true gives a clean re-run from scratch and returns the exact outcome —
      // works even when the automatic pipeline is disabled.
      const result = await enrichSong(song.id, true);
      enrichState = 'success';
      enrichOutcome = result.outcome;
      onResetEnrichment?.();
      setTimeout(() => {
        enrichState = 'idle';
        enrichOutcome = null;
      }, 4000);
    } catch (err) {
      enrichState = 'error';
      enrichError = err instanceof Error ? err.message : 'Failed to enrich song';
      setTimeout(() => {
        enrichState = 'idle';
        enrichError = null;
      }, 5000);
    }
  }

  const matchValue = $derived.by(() => {
    const v = song.matchConfidence ?? enrichmentDetail?.matchConfidence;
    return typeof v === 'number' ? Math.max(0, Math.min(1, v)) : null;
  });

  const enrichmentNormalized = $derived(mapEnrichmentStatus(song.enrichmentStatus));

  // The Metadata tab's "Status" row reads its value straight off the domain vocabulary
  // (Pending/Matched/NeedsReview/Failed — see CLAUDE.md's pipeline architecture), not the
  // raw normalized code, which a viewer would otherwise see verbatim as e.g. "needsreview".
  const ENRICHMENT_STATUS_LABELS: Record<NormalizedEnrichmentStatus, string> = {
    pending: 'Pending',
    processing: 'Processing',
    complete: 'Matched',
    needsreview: 'Needs review',
    failed: 'Failed'
  };

  // The enrich action also builds the track into the library, so the label reflects the outcome:
  // "Add to library" for a track not yet built, "Update in library" once it has a destination.
  const inLibrary = $derived(!!song.destinationPath);

  // Real provider attempts → candidate rows, guarded so stale data from a
  // previously-viewed song isn't shown while the new one loads.
  const attemptRows = $derived(
    loadedSongId === song.id ? providerAttemptRows(enrichmentDetail) : []
  );

  // Provider attempts keyed by backend provider name, for the Enrichment tab's
  // connected dots. Empty until the detail loads for the current song.
  const attemptByProvider = $derived.by(() => {
    const map = new Map<string, ProviderAttempt>();
    if (loadedSongId !== song.id || !enrichmentDetail) return map;
    for (const a of enrichmentDetail.providerAttempts) map.set(a.provider, a);
    return map;
  });

  type EnrichmentSource = {
    key: string;
    name: string;
    connected: boolean;
    url?: string;
    label?: string;
  };

  // The full catalogue of enrichment sources wired into the pipeline. AcoustID /
  // MusicBrainz / Spotify resolve their connected state from stored song ids;
  // Deezer / Apple Music / Tracker have no stored id, so they reflect whether the
  // provider produced a candidate on its last attempt. Tracker is opt-in and niche,
  // so it only appears once it has actually run for this song.
  const enrichmentSources = $derived.by<EnrichmentSource[]>(() => {
    const query = encodeURIComponent(`${trackArtist} ${trackTitle}`.trim());
    const matched = (provider: string) => attemptByProvider.get(provider)?.candidate != null;

    const sources: EnrichmentSource[] = [
      {
        key: 'acoustid',
        name: 'AcoustID',
        connected: acoustIdSourceConnected(
          song.acoustIdTrackId ?? undefined,
          song.matchedBy ?? undefined
        ),
        url: song.acoustIdTrackId
          ? `https://acoustid.org/track/${song.acoustIdTrackId}`
          : 'https://acoustid.org',
        label: song.acoustIdTrackId
          ? `acoustid.org/track/${song.acoustIdTrackId.slice(0, 8)}…`
          : undefined
      },
      {
        key: 'musicbrainz-recording',
        name: 'MusicBrainz Recording',
        connected: Boolean(song.musicBrainzId),
        url: song.musicBrainzId
          ? `https://musicbrainz.org/recording/${song.musicBrainzId}`
          : 'https://musicbrainz.org',
        label: song.musicBrainzId
          ? `musicbrainz.org/recording/${song.musicBrainzId.slice(0, 8)}…`
          : undefined
      }
    ];

    if (song.musicBrainzReleaseId) {
      sources.push({
        key: 'musicbrainz-release',
        name: 'MusicBrainz Release',
        connected: true,
        url: `https://musicbrainz.org/release/${song.musicBrainzReleaseId}`,
        label: `musicbrainz.org/release/${song.musicBrainzReleaseId.slice(0, 8)}…`
      });
    }

    sources.push({
      key: 'spotify',
      name: 'Spotify',
      connected: Boolean(song.spotifyId),
      url: song.spotifyId
        ? `https://open.spotify.com/track/${song.spotifyId}`
        : 'https://spotify.com',
      label: song.spotifyId ? `open.spotify.com/track/${song.spotifyId.slice(0, 8)}…` : undefined
    });

    sources.push({
      key: 'deezer',
      name: 'Deezer',
      connected: matched('Deezer'),
      url: query ? `https://www.deezer.com/search/${query}` : 'https://www.deezer.com',
      label: query ? 'deezer.com/search/…' : undefined
    });

    sources.push({
      key: 'apple-music',
      name: 'Apple Music',
      connected: matched('AppleMusic'),
      url: query ? `https://music.apple.com/search?term=${query}` : 'https://music.apple.com',
      label: query ? 'music.apple.com/search/…' : undefined
    });

    sources.push({
      key: 'lrclib',
      name: 'LRCLIB (Lyrics)',
      connected: lrclibSourceConnected({
        lrclibId: song.lrclibId ?? undefined,
        lyricsStatus,
        artist: trackArtist,
        title: trackTitle,
        enrichmentStatus: enrichmentNormalized
      }),
      url: lrclibWebUrl(trackArtist, trackTitle),
      label: lrclibWebSearchUrl(trackArtist, trackTitle) ? 'lrclib.net/search/…' : undefined
    });

    if (attemptByProvider.has('Tracker')) {
      sources.push({
        key: 'tracker',
        name: 'Community Tracker',
        connected: matched('Tracker')
      });
    }

    return sources;
  });

  // ── Details ───────────────────────────────────────────────────────────────
  type Row = { label: string; value: string; href?: string; copy?: boolean; full?: string };
  // Optional descriptive rows: only shown when a value exists, so a track that never got these
  // enrichment fields doesn't gain a wall of "—" placeholders.
  const present = (
    label: string,
    value: string | null | undefined,
    extra: Partial<Row> = {}
  ): Row[] => (value && value.trim() ? [{ label, value: value.trim(), ...extra }] : []);

  const songRows = $derived<Row[]>([
    { label: 'Title', value: trackTitle },
    { label: 'Artist', value: trackArtist, href: artistHref },
    { label: 'Album', value: album.title, href: albumHref },
    { label: 'Track', value: `${trackN} of ${totalTracks}` },
    { label: 'Year', value: album.year != null ? String(album.year) : '—' },
    ...present('Release date', song.releaseDate ? formatReleaseDate(song.releaseDate) : null),
    { label: 'Genre', value: song.genre ?? album.genre ?? '—' },
    ...present('Composer', song.composer)
  ]);
  const releaseRows = $derived<Row[]>([
    ...present('Label', song.label ?? album.label),
    ...present('Catalog number', song.catalogNumber ?? album.catalogNumber),
    ...present('Barcode', song.upc ?? album.upc, { copy: true }),
    ...present('Copyright', song.copyright)
  ]);
  // Identifiers are what people copy into MusicBrainz/AcoustID: mono, and a tap copies them.
  const identifierRows = $derived<Row[]>([
    {
      label: 'MusicBrainz recording',
      value: song.musicBrainzId ?? '—',
      copy: Boolean(song.musicBrainzId)
    },
    {
      label: 'MusicBrainz release',
      value: song.musicBrainzReleaseId ?? album.musicBrainzReleaseId ?? '—',
      copy: Boolean(song.musicBrainzReleaseId ?? album.musicBrainzReleaseId)
    },
    { label: 'AcoustID', value: song.acoustIdTrackId ?? '—', copy: Boolean(song.acoustIdTrackId) },
    { label: 'ISRC', value: song.isrc ?? '—', copy: Boolean(song.isrc) },
    // The chromaprint itself is long; the row shows its head and copies the whole thing.
    {
      label: 'Fingerprint',
      value: song.fingerprint ? `${song.fingerprint.slice(0, 22)}…` : '—',
      copy: Boolean(song.fingerprint),
      full: song.fingerprint ?? undefined
    }
  ]);
  const fileRows = $derived<Row[]>([
    { label: 'Format', value: formatLabel },
    {
      label: 'Sample rate',
      value: song.sampleRate ? `${(song.sampleRate / 1000).toFixed(1)} kHz` : '—'
    },
    { label: 'Duration', value: formatDuration(song.durationSeconds) },
    { label: 'File size', value: formatFileSize(song.fileSizeBytes) },
    { label: 'Status', value: ENRICHMENT_STATUS_LABELS[enrichmentNormalized] }
  ]);

  async function copy(label: string, value: string) {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(`${label} copied`);
    } catch {
      toast.error('Could not copy to the clipboard');
    }
  }

  // Reset clears matches, lyrics and the AI documents — destructive, so it asks first.
  let confirmResetOpen = $state(false);
</script>

{#snippet rows(list: Row[])}
  {#each list as row (row.label)}
    {#if row.href}
      <GroupedList.Row
        href={row.href}
        onclick={onNavigate}
        label={row.label}
        value={row.value}
        chevron
        title={row.value}
      />
    {:else if row.copy}
      <!-- Stacked, so a 36-character id is readable whole; the row is the copy button. -->
      <GroupedList.Row
        onclick={() => copy(row.label, row.full ?? row.value)}
        label={row.label}
        aria-label={`Copy ${row.label}, ${row.value}`}
      >
        <span class="text-subheadline text-muted-foreground font-mono break-all md:text-xs"
          >{row.value}</span
        >
        {#snippet trailing()}
          <Copy class="text-muted-foreground size-4" aria-hidden="true" />
        {/snippet}
      </GroupedList.Row>
    {:else}
      <GroupedList.Row label={row.label} value={row.value} title={row.value} />
    {/if}
  {/each}
{/snippet}

{#snippet pathRow(label: string, value: string)}
  <GroupedList.Row onclick={() => copy(label, value)} {label} aria-label={`Copy ${label}`}>
    <span class="text-subheadline text-muted-foreground font-mono break-all md:text-xs"
      >{value}</span
    >
    {#snippet trailing()}
      <Copy class="text-muted-foreground size-4" aria-hidden="true" />
    {/snippet}
  </GroupedList.Row>
{/snippet}

<!-- The section control sits above the scroller rather than sticking inside it: there is no page
     colour behind it to scroll rows under, only the cover wash. The mask fades rows out at the
     edges, like the lyrics. -->
{#if isOwner}
  <div class="mx-auto w-full max-w-2xl shrink-0 px-4 pt-1 pb-3 md:px-4 lg:px-0">
    <SegmentedControl items={sectionItems} bind:value={chosenSection} label="Song information" />
  </div>
{/if}
<div
  class="no-scrollbar min-h-0 flex-1 overflow-y-auto overscroll-contain [mask-image:linear-gradient(to_bottom,transparent,#000_12px,#000_calc(100%-24px),transparent)] pt-2 pb-6"
>
  <div class="mx-auto flex w-full max-w-2xl flex-col gap-6 md:px-4 lg:px-0">
    {#if section === 'details'}
      <GroupedList.Section header="Song">{@render rows(songRows)}</GroupedList.Section>
      {#if provenanceGroup}
        {@const fill = provenanceGroup.fill}
        <GroupedList.Section header="How it got here" footer={provenanceGroup.explanation}>
          <GroupedList.Row
            icon={PROVENANCE_ICON[provenanceGroup.reason]}
            label={provenanceGroup.label}
            value={provenanceTrack?.atUtc ? formatDate(provenanceTrack.atUtc) : undefined}
            title={provenanceTrack?.atUtc
              ? `${provenanceTrack.atLabel} ${formatDate(provenanceTrack.atUtc)}`
              : undefined}
          />
          {#each fill?.seeds ?? [] as seed (seed.songId)}
            <GroupedList.Row
              icon={PROVENANCE_ICON[seed.reason]}
              label="Started from “{seed.title}”"
              sublabel={seedSublabel(seed, fill?.album)}
            />
          {/each}
        </GroupedList.Section>
      {/if}
      {#if releaseRows.length}
        <GroupedList.Section header="Release">{@render rows(releaseRows)}</GroupedList.Section>
      {/if}
      <GroupedList.Section header="Identifiers" footer="Tap an identifier to copy it.">
        {@render rows(identifierRows)}
      </GroupedList.Section>
      <GroupedList.Section header="File">{@render rows(fileRows)}</GroupedList.Section>
      {#if song.destinationPath || song.sourcePath}
        <GroupedList.Section header="Library">
          {#if song.destinationPath}{@render pathRow('Destination path', song.destinationPath)}{/if}
          {#if song.sourcePath}{@render pathRow('Source path', song.sourcePath)}{/if}
        </GroupedList.Section>
      {/if}
    {:else if section === 'fingerprint'}
      <GroupedList.Section header="AcoustID · Chromaprint v1.5">
        <GroupedList.Row label="Match confidence">
          {#snippet trailing()}
            <span class="text-title-2 tabular-nums">
              {matchValue !== null ? matchValue.toFixed(2) : '—'}
            </span>
          {/snippet}
        </GroupedList.Row>
        <div class="px-4 py-3">
          <div class="bg-muted flex h-16 items-end gap-[2px] rounded-lg p-1.5" aria-hidden="true">
            {#each fingerprintBars(song.fingerprint) as h, i (i)}
              <div
                class="from-primary to-primary/40 flex-1 rounded-[1px] bg-gradient-to-t"
                style="height: {h}%; min-height: 2px;"
              ></div>
            {/each}
          </div>
        </div>
        {#if song.fingerprint}
          <GroupedList.Row
            onclick={() => copy('Fingerprint', song.fingerprint ?? '')}
            label="Fingerprint"
            aria-label="Copy fingerprint"
          >
            <span class="text-footnote text-muted-foreground line-clamp-3 font-mono break-all">
              {fingerprintHash(song.fingerprint)}
            </span>
            {#snippet trailing()}
              <Copy class="text-muted-foreground size-4" aria-hidden="true" />
            {/snippet}
          </GroupedList.Row>
        {:else}
          <GroupedList.Row label="Fingerprint" value="None" />
        {/if}
      </GroupedList.Section>

      <GroupedList.Section
        header={attemptRows.length
          ? `${attemptRows.length} provider ${attemptRows.length === 1 ? 'attempt' : 'attempts'}`
          : 'Provider attempts'}
      >
        {#if detailLoading}
          <GroupedList.Row label="Loading provider attempts…">
            {#snippet trailing()}<Loader2
                class="text-muted-foreground size-4 animate-spin"
              />{/snippet}
          </GroupedList.Row>
        {:else if detailError}
          <GroupedList.Row>
            <span class="text-body text-destructive-text flex items-center gap-2 md:text-sm">
              <AlertCircle class="size-4 shrink-0" />
              {detailError}
            </span>
          </GroupedList.Row>
        {:else if !attemptRows.length}
          <GroupedList.Row label="No provider attempts yet" />
        {:else}
          {#each attemptRows as row (row.key)}
            <GroupedList.Row
              label={row.matched
                ? `${row.title || '(untitled)'}${row.artist ? ` — ${row.artist}` : ''}`
                : (row.error ?? attemptWord(row.status))}
              sublabel={row.matched
                ? `${row.source}${row.album ? ` · ${row.album}${row.year ? `, ${row.year}` : ''}` : ''}`
                : row.source}
            >
              {#snippet leading()}
                <span
                  class={cn(
                    'text-subheadline w-9 text-right font-semibold tabular-nums',
                    row.chosen ? 'text-foreground' : 'text-muted-foreground'
                  )}
                >
                  {row.score !== null ? row.score.toFixed(2) : '—'}
                </span>
              {/snippet}
              {#snippet trailing()}
                {#if row.chosen}
                  <Badge variant="secondary"><Check class="text-primary" />Chosen</Badge>
                {:else if !row.matched && row.error}
                  <!-- The label is the provider's error text here, so the badge says what kind of
                       outcome it was; otherwise the label already does. -->
                  <Badge variant="secondary">{attemptWord(row.status)}</Badge>
                {/if}
              {/snippet}
            </GroupedList.Row>
          {/each}
        {/if}
      </GroupedList.Section>
    {:else if section === 'enrichment'}
      <GroupedList.Section>
        {#if timelineHref}
          <GroupedList.Row
            href={timelineHref}
            onclick={onNavigate}
            icon={History}
            label="View timeline"
            chevron
          />
        {/if}
        <GroupedList.Row label="Matched via" value={song.matchedBy ?? '—'} />
      </GroupedList.Section>

      <GroupedList.Section header="AI quality">
        <GroupedList.Row label="Verdict">
          {#if quality?.graded}
            {#if quality.summary}
              <span class="text-subheadline text-muted-foreground mt-0.5 md:text-xs"
                >{quality.summary}</span
              >
            {/if}
            {#if quality.issues && quality.issues.length > 0}
              <span class="mt-1.5 flex flex-wrap gap-1">
                {#each quality.issues as issue, i (i)}
                  <Badge variant="secondary" class="font-mono">{issue.code}</Badge>
                {/each}
              </span>
            {/if}
            {#if quality.model || quality.gradedAtUtc}
              <span class="text-footnote text-muted-foreground mt-1">
                {[
                  quality.model,
                  quality.gradedAtUtc ? `Graded ${formatDateTime(quality.gradedAtUtc)}` : null
                ]
                  .filter(Boolean)
                  .join(' · ')}
              </span>
            {/if}
          {:else}
            <span class="text-subheadline text-muted-foreground md:text-xs">Not graded yet.</span>
          {/if}
          {#snippet trailing()}
            {#if quality?.graded}
              <Badge variant={verdictVariant(quality.verdict)}
                >{quality.verdict} · {quality.score}</Badge
              >
            {/if}
          {/snippet}
        </GroupedList.Row>
        <GroupedList.Row onclick={handleGradeNow} disabled={gradeBusy} icon={Sparkles}>
          <span class="text-body text-primary md:text-sm"
            >{quality?.graded ? 'Re-grade' : 'Grade now'}</span
          >
          {#snippet trailing()}
            {#if gradeBusy}<Loader2 class="text-muted-foreground size-4 animate-spin" />{/if}
          {/snippet}
        </GroupedList.Row>
        <GroupedList.Row onclick={handleCopyDossier} icon={copied ? Check : Copy}>
          <span class="text-body text-primary md:text-sm">{copied ? 'Copied' : 'Copy dossier'}</span
          >
        </GroupedList.Row>
      </GroupedList.Section>

      <GroupedList.Section header="Sources">
        {#each enrichmentSources as src (src.key)}
          <SourceRow name={src.name} connected={src.connected} url={src.url} label={src.label} />
        {/each}
      </GroupedList.Section>

      <GroupedList.Section
        footer={soulseekConfigured
          ? (upgradeError ??
            upgradeTerminalNote ??
            'Find better quality searches Soulseek for a higher-quality copy and swaps it in place.')
          : undefined}
      >
        <GroupedList.Row
          onclick={handleEnrichNow}
          disabled={enrichState === 'loading'}
          icon={LibraryBig}
        >
          <span
            class={cn(
              'text-body md:text-sm',
              enrichState === 'error' ? 'text-destructive-text' : 'text-primary'
            )}
          >
            {#if enrichState === 'loading'}
              {inLibrary ? 'Updating…' : 'Adding…'}
            {:else if enrichState === 'success'}
              {enrichOutcome ?? 'Done'}
            {:else if enrichState === 'error'}
              {inLibrary ? 'Update failed' : 'Add failed'}
            {:else}
              {inLibrary ? 'Update in library' : 'Add to library'}
            {/if}
          </span>
          {#if enrichError}
            <span class="text-footnote text-destructive-text">{enrichError}</span>
          {/if}
          {#snippet trailing()}
            {#if enrichState === 'loading'}
              <Loader2 class="text-muted-foreground size-4 animate-spin" />
            {:else if enrichState === 'success'}
              <Check class="text-primary size-4" />
            {/if}
          {/snippet}
        </GroupedList.Row>
        {#if soulseekConfigured}
          <GroupedList.Row
            onclick={handleFindBetterQuality}
            disabled={upgradeRequesting || enrichmentDetail?.upgrade?.active === true}
            icon={Search}
          >
            <span class="text-body text-primary md:text-sm"
              >{upgradeActiveLabel ?? 'Find better quality'}</span
            >
            {#snippet trailing()}
              {#if upgradeRequesting || enrichmentDetail?.upgrade?.active}
                <Loader2 class="text-muted-foreground size-4 animate-spin" />
              {/if}
            {/snippet}
          </GroupedList.Row>
        {/if}
      </GroupedList.Section>

      <GroupedList.Section
        footer={resetError ?? 'Clears matches and lyrics; re-enrichment runs automatically.'}
      >
        <GroupedList.Row
          onclick={() => (confirmResetOpen = true)}
          disabled={resetState === 'loading'}
          label={resetState === 'loading'
            ? 'Resetting…'
            : resetState === 'success'
              ? 'Metadata reset'
              : resetState === 'error'
                ? 'Reset failed'
                : 'Reset metadata'}
          destructive={resetState !== 'success'}
        >
          {#snippet trailing()}
            {#if resetState === 'loading'}
              <Loader2 class="text-muted-foreground size-4 animate-spin" />
            {:else if resetState === 'success'}
              <Check class="text-primary size-4" />
            {/if}
          {/snippet}
        </GroupedList.Row>
      </GroupedList.Section>
    {/if}
  </div>
</div>

<!-- Destructive and irreversible, so it asks first — as an action sheet, Cancel at the bottom. -->
<ActionSheet
  bind:open={confirmResetOpen}
  title="Reset metadata?"
  description={`Clears the matches and lyrics for “${trackTitle}”. Re-enrichment runs automatically.`}
  actionLabel="Reset metadata"
  destructive
  onAction={() => void handleResetEnrichment()}
/>
