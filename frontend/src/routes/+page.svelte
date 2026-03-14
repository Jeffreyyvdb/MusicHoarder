<script lang="ts">
	import { onMount } from 'svelte';
	import { getSongs, getStats, type Song, type Stats } from '$lib/api';

	let stats = $state<Stats | null>(null);
	let songs = $state<Song[]>([]);
	let includeDeleted = $state(false);
	let loadingSongs = $state(false);
	let error = $state<string | null>(null);

	function valueOrDash(value: string | number | null | undefined): string {
		if (value === null || value === undefined || value === '') return '-';
		return String(value);
	}

	function formatDuration(seconds: number | null): string {
		if (seconds === null) return '-';
		const mins = Math.floor(seconds / 60);
		const secs = seconds % 60;
		return `${mins}:${String(secs).padStart(2, '0')}`;
	}

	async function loadSongs() {
		loadingSongs = true;
		try {
			const response = await getSongs(includeDeleted);
			songs = response.songs;
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load songs';
		} finally {
			loadingSongs = false;
		}
	}

	onMount(async () => {
		try {
			stats = await getStats();
			await loadSongs();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load data';
		}
	});
</script>

<h1>MusicHoarder</h1>

{#if error}
	<p class="text-red-600">{error}</p>
	<p class="text-sm text-gray-500">
		Make sure the AppHost is running and the API is available. Run <code>dotnet run</code> from MusicHoarder.AppHost.
	</p>
{:else if stats}
	<div class="mt-4 space-y-4 px-4 pb-6">
		<section>
			<h2 class="text-lg font-semibold">Tracks</h2>
			<p>Total: {stats.tracks.total} · Deleted: {stats.tracks.deleted}</p>
		</section>
		{#if stats.storage}
			<section>
				<h2 class="text-lg font-semibold">Storage</h2>
				<p>Total: {stats.storage.totalGiB} GiB</p>
			</section>
		{/if}
		{#if stats.enrichment}
			<section>
				<h2 class="text-lg font-semibold">Enrichment</h2>
				<p>
					With fingerprint: {stats.enrichment.withFingerprint} ({stats.enrichment.fingerprintPct}%)
					· MusicBrainz: {stats.enrichment.withMusicBrainzId} ({stats.enrichment.musicBrainzPct}%)
				</p>
			</section>
		{/if}

		<section class="space-y-3">
			<div class="flex items-center gap-3">
				<h2 class="text-lg font-semibold">Songs</h2>
				<label class="flex items-center gap-2 text-sm">
					<input type="checkbox" bind:checked={includeDeleted} onchange={loadSongs} />
					Include deleted
				</label>
				<button
					class="rounded border border-gray-300 px-2 py-1 text-sm hover:bg-gray-50"
					onclick={loadSongs}
					disabled={loadingSongs}
				>
					{loadingSongs ? 'Refreshing…' : 'Refresh'}
				</button>
			</div>
			<p class="text-sm text-gray-600">
				Showing {songs.length} tracks. Source path is the original file location.
			</p>

			<div class="overflow-x-auto rounded border border-gray-200">
				<table class="min-w-full border-collapse text-sm">
					<thead class="bg-gray-50 text-left">
						<tr>
							<th class="border-b border-gray-200 px-3 py-2 font-semibold">Current metadata</th>
							<th class="border-b border-gray-200 px-3 py-2 font-semibold">Original metadata</th>
							<th class="border-b border-gray-200 px-3 py-2 font-semibold">File / original path</th>
						</tr>
					</thead>
					<tbody>
						{#if songs.length === 0}
							<tr>
								<td colspan="3" class="px-3 py-4 text-center text-gray-500"> No songs found. </td>
							</tr>
						{:else}
							{#each songs as song (song.id)}
								<tr class="align-top odd:bg-white even:bg-gray-50/40">
									<td class="border-b border-gray-100 px-3 py-2">
										<div><strong>Title:</strong> {valueOrDash(song.title)}</div>
										<div><strong>Artist:</strong> {valueOrDash(song.artist)}</div>
										<div><strong>Album:</strong> {valueOrDash(song.album)}</div>
										<div><strong>Year:</strong> {valueOrDash(song.year)}</div>
										<div><strong>Track:</strong> {valueOrDash(song.trackNumber)}</div>
										<div><strong>ISRC:</strong> {valueOrDash(song.isrc)}</div>
										<div><strong>MBID:</strong> {valueOrDash(song.musicBrainzId)}</div>
										<div><strong>Spotify:</strong> {valueOrDash(song.spotifyId)}</div>
										<div>
											<strong>Status:</strong>
											{song.enrichmentStatus}
											{#if song.matchConfidence !== null}
												({Math.round(song.matchConfidence * 100)}%)
											{/if}
										</div>
									</td>
									<td class="border-b border-gray-100 px-3 py-2">
										<div>
											<strong>Captured:</strong>
											{song.originalMetadataCaptured ? 'Yes' : 'No'}
										</div>
										<div><strong>Title:</strong> {valueOrDash(song.originalTitle)}</div>
										<div><strong>Artist:</strong> {valueOrDash(song.originalArtist)}</div>
										<div><strong>Album:</strong> {valueOrDash(song.originalAlbum)}</div>
										<div><strong>Year:</strong> {valueOrDash(song.originalYear)}</div>
										<div><strong>Track:</strong> {valueOrDash(song.originalTrackNumber)}</div>
										<div><strong>ISRC:</strong> {valueOrDash(song.originalIsrc)}</div>
										<div><strong>MBID:</strong> {valueOrDash(song.originalMusicBrainzId)}</div>
										<div><strong>Spotify:</strong> {valueOrDash(song.originalSpotifyId)}</div>
									</td>
									<td class="border-b border-gray-100 px-3 py-2">
										<div><strong>File:</strong> {song.fileName}</div>
										<div><strong>Type:</strong> {song.extension}</div>
										<div><strong>Size:</strong> {song.fileSizeBytes.toLocaleString()} bytes</div>
										<div><strong>Duration:</strong> {formatDuration(song.durationSeconds)}</div>
										<div class="mt-1"><strong>Original path:</strong></div>
										<div class="font-mono text-xs break-all text-gray-700">{song.sourcePath}</div>
										{#if song.deletedAtUtc}
											<div class="mt-1 text-red-600">Deleted at {song.deletedAtUtc}</div>
										{/if}
									</td>
								</tr>
							{/each}
						{/if}
					</tbody>
				</table>
			</div>
		</section>
	</div>
{:else}
	<p class="px-4">Loading data…</p>
{/if}
