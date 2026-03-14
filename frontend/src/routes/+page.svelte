<script lang="ts">
	import { onMount } from 'svelte';
	import { getStats, type Stats } from '$lib/api';

	let stats = $state<Stats | null>(null);
	let error = $state<string | null>(null);

	onMount(async () => {
		try {
			stats = await getStats();
		} catch (e) {
			error = e instanceof Error ? e.message : 'Failed to load stats';
		}
	});
</script>

<h1>MusicHoarder</h1>

{#if error}
	<p class="text-red-600">{error}</p>
	<p class="text-sm text-gray-500">
		Make sure the AppHost is running and the API is available. Run <code>dotnet run</code> from
		MusicHoarder.AppHost.
	</p>
{:else if stats}
	<div class="mt-4 space-y-4">
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
					With fingerprint: {stats.enrichment.withFingerprint} ({stats.enrichment.fingerprintPct}%) ·
					MusicBrainz: {stats.enrichment.withMusicBrainzId} ({stats.enrichment.musicBrainzPct}%)
				</p>
			</section>
		{/if}
	</div>
{:else}
	<p>Loading stats…</p>
{/if}
