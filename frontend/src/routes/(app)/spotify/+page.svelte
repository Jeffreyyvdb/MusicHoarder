<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
  import { AlertCircle } from '@lucide/svelte';

  let oauthBanner = $state<{ type: 'success' | 'error'; message: string } | null>(null);

  $effect(() => {
    const connected = page.url.searchParams.get('spotify_connected');
    const oauthErr = page.url.searchParams.get('spotify_error');

    if (connected === '1') {
      oauthBanner = { type: 'success', message: 'Spotify connected successfully.' };
      void goto('/spotify', { replaceState: true, noScroll: true });
    } else if (oauthErr) {
      oauthBanner = { type: 'error', message: `Spotify error: ${oauthErr}` };
      void goto('/spotify', { replaceState: true, noScroll: true });
    }
  });
</script>

<div class="flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto p-6">
  <div>
    <h1 class="text-2xl font-bold">Spotify</h1>
    <p class="text-muted-foreground mt-1 text-sm">Connect Spotify and sync your library</p>
  </div>

  {#if oauthBanner}
    <div
      class="rounded-lg border px-4 py-3 text-sm {oauthBanner.type === 'success'
        ? 'border-primary/40 bg-primary/10 text-primary'
        : 'border-destructive/40 bg-destructive/10 text-destructive'}"
    >
      {oauthBanner.message}
    </div>
  {/if}

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2 text-base">
        <AlertCircle class="text-amber-500 size-5" />
        Spotify page port pending
      </CardTitle>
    </CardHeader>
    <CardContent class="text-muted-foreground text-sm">
      <p>
        Ports from <code>frontend/app/(app)/spotify/page.tsx</code> (1009 lines). Sections: OAuth
        status card, liked songs list with virtualized scroll, playlist sync UI, track match
        results. Calls <code>getSpotifyStatus</code>, <code>linkSpotifyAccount</code>, etc.
        (already in <code>$lib/api-client</code>). The OAuth callback handling above is in place.
      </p>
    </CardContent>
  </Card>
</div>
