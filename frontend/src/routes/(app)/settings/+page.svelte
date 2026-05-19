<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import { Badge } from '$lib/components/ui/badge';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import PurgeStatusBanner from '$lib/components/settings/PurgeStatusBanner.svelte';
  import {
    fetchSpotifyCredentials,
    saveSpotifyCredentials,
    fetchSpotifyStatus,
    purgeAll,
    purgePostFingerprint,
    fetchPurgeStatus,
    type PurgeMode,
    type PurgeSnapshot,
    type SpotifyCredentialsResponse,
    type SpotifyStatusResponse
  } from '$lib/api-client';
  import {
    Settings,
    KeyRound,
    Save,
    Loader2,
    CheckCircle2,
    AlertCircle,
    AlertTriangle,
    Music,
    ExternalLink,
    Trash2
  } from '@lucide/svelte';

  let clientId = $state('');
  let clientSecret = $state('');
  let isSaving = $state(false);
  let isLoading = $state(true);
  let savedCredentials = $state<SpotifyCredentialsResponse | null>(null);
  let spotifyStatus = $state<SpotifyStatusResponse | null>(null);
  let saveResult = $state<{ success: boolean; message: string } | null>(null);
  let purgeSnapshot = $state<PurgeSnapshot | null>(null);
  let purgeStartError = $state<string | null>(null);

  const purgeRunning = $derived(purgeSnapshot?.status === 'running');

  $effect(() => {
    let cancelled = false;
    void (async () => {
      isLoading = true;
      try {
        const [creds, status, purge] = await Promise.all([
          fetchSpotifyCredentials().catch(
            () => ({ clientId: null, hasClientSecret: false }) as SpotifyCredentialsResponse
          ),
          fetchSpotifyStatus().catch(
            () =>
              ({
                connected: false,
                hasCredentials: false,
                tokenExpired: false
              }) as SpotifyStatusResponse
          ),
          fetchPurgeStatus().catch(() => null)
        ]);
        if (cancelled) return;
        savedCredentials = creds;
        spotifyStatus = status;
        if (creds.clientId) clientId = creds.clientId;
        if (purge) purgeSnapshot = purge;
      } catch {
        // Settings page should load even if API is down
      } finally {
        if (!cancelled) isLoading = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  // Poll purge status every 1.5s while running; stops when complete/failed and
  // restarts when the user kicks off a new purge.
  $effect(() => {
    if (purgeSnapshot?.status !== 'running') return;

    let cancelled = false;
    const tick = async () => {
      try {
        const snap = await fetchPurgeStatus();
        if (!cancelled) purgeSnapshot = snap;
      } catch {
        // keep polling on transient errors
      }
    };
    const id = setInterval(tick, 1500);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  });

  async function handlePurge(mode: PurgeMode) {
    purgeStartError = null;
    const response =
      mode === 'post-fingerprint' ? await purgePostFingerprint() : await purgeAll();
    if (!response.ok) {
      purgeStartError = response.message;
      return;
    }
    // Optimistically reflect "running" — the poll loop takes over with real progress.
    purgeSnapshot = {
      status: 'running',
      mode,
      jobId: response.jobId,
      startedAt: new Date().toISOString(),
      completedAt: null,
      songsTotal: 0,
      songsProcessed: 0,
      filesTotal: 0,
      filesDeleted: 0,
      filesFailed: 0,
      spotifyMatchesCleared: 0,
      error: null
    };
  }

  async function handleSave() {
    if (!clientId.trim() || !clientSecret.trim()) {
      saveResult = { success: false, message: 'Both Client ID and Client Secret are required.' };
      return;
    }
    isSaving = true;
    saveResult = null;
    try {
      await saveSpotifyCredentials(clientId.trim(), clientSecret.trim());
      saveResult = { success: true, message: 'Spotify credentials saved successfully.' };
      savedCredentials = { clientId: clientId.trim(), hasClientSecret: true };
      clientSecret = '';
    } catch (err) {
      saveResult = {
        success: false,
        message: err instanceof Error ? err.message : 'Failed to save credentials.'
      };
    } finally {
      isSaving = false;
    }
  }
</script>

<div class="flex-1 overflow-auto">
  <div class="mx-auto max-w-2xl p-6 md:p-8">
    <div class="mb-8 flex items-center gap-3">
      <div class="bg-secondary flex size-10 items-center justify-center rounded-lg">
        <Settings class="text-foreground size-5" />
      </div>
      <div>
        <h1 class="text-2xl font-bold">Settings</h1>
        <p class="text-muted-foreground text-sm">Configure integrations and preferences</p>
      </div>
    </div>

    <section class="border-border bg-card rounded-xl border">
      <div class="border-border flex items-center gap-3 border-b px-6 py-4">
        <div class="flex size-8 items-center justify-center rounded-lg bg-[#1DB954]/10">
          <Music class="size-4 text-[#1DB954]" />
        </div>
        <div class="min-w-0 flex-1">
          <h2 class="font-semibold">Spotify Integration</h2>
          <p class="text-muted-foreground text-xs">
            Connect your Spotify account to browse playlists and liked songs
          </p>
        </div>
        {#if spotifyStatus?.connected}
          <Badge class="border-0 bg-[#1DB954]/20 text-[#1DB954]">Connected</Badge>
        {:else if savedCredentials?.hasClientSecret}
          <Badge variant="secondary">Credentials Set</Badge>
        {:else}
          <Badge variant="outline" class="text-muted-foreground">Not Configured</Badge>
        {/if}
      </div>

      <div class="space-y-5 p-6">
        {#if isLoading}
          <div class="flex items-center justify-center py-8">
            <Loader2 class="text-muted-foreground size-6 animate-spin" />
          </div>
        {:else}
          <div class="border-border bg-secondary/30 rounded-lg border p-4 text-sm">
            <div class="flex items-start gap-2">
              <KeyRound class="text-muted-foreground mt-0.5 size-4 shrink-0" />
              <div>
                <p class="mb-1 font-medium">How to get your Spotify API credentials</p>
                <ol class="text-muted-foreground list-inside list-decimal space-y-1 text-xs">
                  <li>
                    Go to the
                    <a
                      href="https://developer.spotify.com/dashboard"
                      target="_blank"
                      rel="noopener noreferrer"
                      class="text-primary inline-flex items-center gap-0.5 hover:underline"
                    >
                      Spotify Developer Dashboard
                      <ExternalLink class="size-3" />
                    </a>
                  </li>
                  <li>Create a new app (or use an existing one)</li>
                  <li>
                    Add a redirect URI for the API callback. Spotify does not allow
                    <code class="bg-secondary rounded px-1 py-0.5 text-xs">localhost</code> — use loopback
                    IP (e.g.
                    <code class="bg-secondary rounded px-1 py-0.5 text-xs"
                      >http://127.0.0.1:5142/api/spotify/callback</code
                    >). Match
                    <code class="bg-secondary rounded px-1 py-0.5 text-xs"
                      >Spotify:OAuthRedirectBaseUrl</code
                    > in the API config.
                  </li>
                  <li>
                    After login, the API redirects you back to this app. With .NET Aspire
                    (AppHost), that URL is set automatically. If you run the API without Aspire, set
                    <code class="bg-secondary rounded px-1 py-0.5 text-xs"
                      >Frontend:PublicBaseUrl</code
                    > (or env
                    <code class="bg-secondary rounded px-1 py-0.5 text-xs"
                      >Frontend__PublicBaseUrl</code
                    >) on the API to this app's origin (e.g.
                    <code class="bg-secondary rounded px-1 py-0.5 text-xs"
                      >http://localhost:3000</code
                    >)
                  </li>
                  <li>Copy the Client ID and Client Secret from the app settings</li>
                </ol>
              </div>
            </div>
          </div>

          <div class="space-y-4">
            <div class="space-y-2">
              <Label for="client-id">Client ID</Label>
              <Input
                id="client-id"
                type="text"
                placeholder="Enter your Spotify Client ID"
                bind:value={clientId}
                oninput={() => (saveResult = null)}
                class="font-mono text-sm"
              />
            </div>
            <div class="space-y-2">
              <Label for="client-secret">
                Client Secret
                {#if savedCredentials?.hasClientSecret && !clientSecret}
                  <span class="text-muted-foreground ml-2 text-xs font-normal">
                    (already saved — enter a new value to update)
                  </span>
                {/if}
              </Label>
              <Input
                id="client-secret"
                type="password"
                placeholder={savedCredentials?.hasClientSecret
                  ? '••••••••••••••••'
                  : 'Enter your Spotify Client Secret'}
                bind:value={clientSecret}
                oninput={() => (saveResult = null)}
                class="font-mono text-sm"
              />
            </div>
          </div>

          {#if saveResult}
            <div
              class="flex items-center gap-2 rounded-lg border px-4 py-3 text-sm {saveResult.success
                ? 'border-[#1DB954]/50 bg-[#1DB954]/10 text-[#1DB954]'
                : 'border-destructive/50 bg-destructive/10 text-destructive'}"
            >
              {#if saveResult.success}
                <CheckCircle2 class="size-4 shrink-0" />
              {:else}
                <AlertCircle class="size-4 shrink-0" />
              {/if}
              {saveResult.message}
            </div>
          {/if}

          <Button
            onclick={handleSave}
            disabled={isSaving || !clientId.trim() || !clientSecret.trim()}
            class="w-full sm:w-auto"
          >
            {#if isSaving}
              <Loader2 class="mr-2 size-4 animate-spin" />
            {:else}
              <Save class="mr-2 size-4" />
            {/if}
            Save Credentials
          </Button>
        {/if}
      </div>
    </section>

    <section class="border-destructive/40 bg-card mt-8 rounded-xl border">
      <div class="border-destructive/40 flex items-center gap-3 border-b px-6 py-4">
        <div class="bg-destructive/10 flex size-8 items-center justify-center rounded-lg">
          <AlertTriangle class="text-destructive size-4" />
        </div>
        <div class="min-w-0 flex-1">
          <h2 class="font-semibold">Danger zone</h2>
          <p class="text-muted-foreground text-xs">
            Irreversible actions that purge pipeline state. Make sure no job is running.
          </p>
        </div>
      </div>

      <div class="divide-border divide-y">
        <div class="flex flex-col gap-3 p-6 sm:flex-row sm:items-start sm:justify-between">
          <div class="flex-1 pr-4">
            <h3 class="text-sm font-semibold">Reset enrichment data</h3>
            <p class="text-muted-foreground mt-1 text-xs">
              Keeps your scanned files and fingerprints. Clears enrichment results, provider
              attempts, lyrics, duplicate detection, and library-build status for every active song,
              and deletes any files that were copied to the destination folder.
            </p>
          </div>
          <AlertDialog.Root>
            <AlertDialog.Trigger>
              {#snippet child({ props })}
                <Button
                  {...props}
                  variant="outline"
                  class="text-destructive hover:text-destructive shrink-0 gap-2"
                  disabled={purgeRunning}
                >
                  {#if purgeRunning && purgeSnapshot?.mode === 'post-fingerprint'}
                    <Loader2 class="size-4 animate-spin" />
                  {:else}
                    <Trash2 class="size-4" />
                  {/if}
                  Reset enrichment data
                </Button>
              {/snippet}
            </AlertDialog.Trigger>
            <AlertDialog.Content>
              <AlertDialog.Header>
                <AlertDialog.Title>Reset enrichment data?</AlertDialog.Title>
                <AlertDialog.Description>
                  This clears every song's enrichment, lyrics, duplicate flags, and library-build
                  state, and deletes files copied to the destination folder. Fingerprints and scan
                  data are preserved so the next run skips straight to enrichment. This runs in the
                  background — you can navigate away and the progress will be here when you come
                  back. This cannot be undone.
                </AlertDialog.Description>
              </AlertDialog.Header>
              <AlertDialog.Footer>
                <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                <AlertDialog.Action onclick={() => handlePurge('post-fingerprint')}>
                  Reset enrichment data
                </AlertDialog.Action>
              </AlertDialog.Footer>
            </AlertDialog.Content>
          </AlertDialog.Root>
        </div>

        <div class="flex flex-col gap-3 p-6 sm:flex-row sm:items-start sm:justify-between">
          <div class="flex-1 pr-4">
            <h3 class="text-sm font-semibold">Purge all data</h3>
            <p class="text-muted-foreground mt-1 text-xs">
              Removes every song, provider attempt, and cached Spotify match from the database, and
              deletes any files copied to the destination folder. Source files are not touched. The
              next run re-scans and re-fingerprints from source.
            </p>
          </div>
          <AlertDialog.Root>
            <AlertDialog.Trigger>
              {#snippet child({ props })}
                <Button
                  {...props}
                  variant="destructive"
                  class="shrink-0 gap-2"
                  disabled={purgeRunning}
                >
                  {#if purgeRunning && purgeSnapshot?.mode === 'all'}
                    <Loader2 class="size-4 animate-spin" />
                  {:else}
                    <Trash2 class="size-4" />
                  {/if}
                  Purge all data
                </Button>
              {/snippet}
            </AlertDialog.Trigger>
            <AlertDialog.Content>
              <AlertDialog.Header>
                <AlertDialog.Title>Purge all data?</AlertDialog.Title>
                <AlertDialog.Description>
                  This deletes every song record, provider attempt, and cached Spotify match, and
                  removes files that were copied to the destination folder. Source files are not
                  affected. This runs in the background — you can navigate away and the progress
                  will be here when you come back. This cannot be undone.
                </AlertDialog.Description>
              </AlertDialog.Header>
              <AlertDialog.Footer>
                <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                <AlertDialog.Action onclick={() => handlePurge('all')}>
                  Purge all data
                </AlertDialog.Action>
              </AlertDialog.Footer>
            </AlertDialog.Content>
          </AlertDialog.Root>
        </div>

        {#if purgeStartError}
          <div class="px-6 pt-4 pb-6">
            <div
              class="border-destructive/50 bg-destructive/10 text-destructive flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
            >
              <AlertCircle class="mt-0.5 size-4 shrink-0" />
              <p>{purgeStartError}</p>
            </div>
          </div>
        {/if}

        {#if purgeSnapshot && purgeSnapshot.status !== 'idle'}
          <div class="px-6 pt-4 pb-6">
            <PurgeStatusBanner snapshot={purgeSnapshot} />
          </div>
        {/if}
      </div>
    </section>
  </div>
</div>
