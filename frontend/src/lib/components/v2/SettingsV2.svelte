<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { cn } from '$lib/utils';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import { Badge } from '$lib/components/ui/badge';
  import { Switch } from '$lib/components/ui/switch';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import PurgeStatusBanner from '$lib/components/settings/PurgeStatusBanner.svelte';
  import { isPasskeySupported } from '$lib/webauthn-client';
  import {
    fetchSpotifyCredentials,
    saveSpotifyCredentials,
    fetchSpotifyStatus,
    fetchSpotifyConnectUrl,
    disconnectSpotify,
    fetchSettings,
    updateSettings,
    purgeAll,
    purgePostFingerprint,
    fetchPurgeStatus,
    registerPasskey,
    listPasskeys,
    deletePasskey,
    type PasskeyView,
    type PurgeMode,
    type PurgeSnapshot,
    type SettingsResponse,
    type SettingsProvidersView,
    type SettingsQualityGradingView,
    type SpotifyCredentialsResponse,
    type SpotifyStatusResponse
  } from '$lib/api-client';
  import { signOutAndReset } from '$lib/auth/sign-out';
  import {
    Loader2,
    CheckCircle2,
    AlertCircle,
    AlertTriangle,
    ExternalLink,
    Copy,
    KeyRound,
    Trash2,
    Save,
    LogOut,
    Sparkles,
    FolderInput
  } from '@lucide/svelte';

  // ── tabs ─────────────────────────────────────────────────────────────────────
  type TabId = 'sources' | 'providers' | 'rules' | 'output' | 'account' | 'updates';
  const TABS: { id: TabId; label: string }[] = [
    { id: 'sources', label: 'Sources' },
    { id: 'providers', label: 'Providers' },
    { id: 'rules', label: 'Filename rules' },
    { id: 'output', label: 'Library output' },
    { id: 'account', label: 'Account' },
    { id: 'updates', label: 'Updates' }
  ];

  // Deep-linkable via ?tab=, defaults to Sources. The v2 sidebar links every
  // settings sub-item to /settings, so this also honours an explicit tab query.
  function readTab(): TabId {
    const q = page.url.searchParams.get('tab');
    return TABS.some((t) => t.id === q) ? (q as TabId) : 'sources';
  }
  let activeTab = $state<TabId>(readTab());
  function selectTab(id: TabId) {
    activeTab = id;
    const url = new URL(page.url);
    url.searchParams.set('tab', id);
    void goto(`${url.pathname}${url.search}`, { replaceState: true, keepFocus: true, noScroll: true });
  }

  // ── account ────────────────────────────────────────────────────────────────────
  const user = $derived(
    page.data.user as
      | { id: string; email: string; role: 'Owner' | 'Demo'; displayName: string | null }
      | undefined
  );
  const initials = $derived((user?.displayName ?? user?.email ?? '?').slice(0, 2).toUpperCase());

  async function handleSignOut(allSessions = false) {
    await signOutAndReset(allSessions);
  }

  // ── passkeys (owner-only) ──────────────────────────────────────────────────────
  let passkeySupported = $state(false);
  let passkeys = $state<PasskeyView[]>([]);
  let passkeysLoading = $state(false);
  let newPasskeyName = $state('');
  let isAddingPasskey = $state(false);
  let passkeyError = $state<string | null>(null);

  $effect(() => {
    passkeySupported = isPasskeySupported();
  });

  $effect(() => {
    if (user?.role !== 'Owner') return;
    let cancelled = false;
    void (async () => {
      passkeysLoading = true;
      try {
        const list = await listPasskeys();
        if (!cancelled) passkeys = list;
      } catch {
        // non-fatal; section just shows empty
      } finally {
        if (!cancelled) passkeysLoading = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  async function handleAddPasskey() {
    isAddingPasskey = true;
    passkeyError = null;
    try {
      const created = await registerPasskey(newPasskeyName.trim() || 'Passkey');
      passkeys = [created, ...passkeys];
      newPasskeyName = '';
    } catch (err) {
      passkeyError = err instanceof Error ? err.message : 'Could not add passkey.';
    } finally {
      isAddingPasskey = false;
    }
  }

  async function handleRemovePasskey(id: string) {
    passkeyError = null;
    try {
      await deletePasskey(id);
      passkeys = passkeys.filter((p) => p.id !== id);
    } catch (err) {
      passkeyError = err instanceof Error ? err.message : 'Could not remove passkey.';
    }
  }

  // ── settings + spotify state ───────────────────────────────────────────────────
  let isLoading = $state(true);
  let settings = $state<SettingsResponse | null>(null);
  let providers = $state<SettingsProvidersView | null>(null);
  let isSavingProviders = $state(false);
  let providersResult = $state<{ success: boolean; message: string } | null>(null);
  let qualityGrading = $state<SettingsQualityGradingView | null>(null);
  let isSavingQualityGrading = $state(false);
  let qualityGradingResult = $state<{ success: boolean; message: string } | null>(null);

  // Spotify credential form state
  let clientId = $state('');
  let clientSecret = $state('');
  let isSaving = $state(false);
  let showSecret = $state(false);
  let savedCredentials = $state<SpotifyCredentialsResponse | null>(null);
  let spotifyStatus = $state<SpotifyStatusResponse | null>(null);
  let saveResult = $state<{ success: boolean; message: string } | null>(null);
  let isConnecting = $state(false);
  let spotifyError = $state<string | null>(null);

  // Purge state
  let purgeSnapshot = $state<PurgeSnapshot | null>(null);
  let purgeStartError = $state<string | null>(null);
  const purgeRunning = $derived(purgeSnapshot?.status === 'running');

  // "Purge all data" is the most destructive action in the app — it requires an explicit typed
  // acknowledgment (not just a click-through Cancel/Confirm) before the dialog's action enables.
  let purgeAllDialogOpen = $state(false);
  let purgeAllConfirmText = $state('');
  const PURGE_ALL_CONFIRM_WORD = 'DELETE';
  const purgeAllConfirmed = $derived(purgeAllConfirmText.trim().toUpperCase() === PURGE_ALL_CONFIRM_WORD);
  function onPurgeAllDialogOpenChange(open: boolean) {
    purgeAllDialogOpen = open;
    if (!open) purgeAllConfirmText = '';
  }

  const redirectUri = $derived(
    settings?.spotify.oAuthRedirectBaseUrl
      ? `${settings.spotify.oAuthRedirectBaseUrl.replace(/\/$/, '')}/api/spotify/callback`
      : 'http://127.0.0.1:5142/api/spotify/callback'
  );

  $effect(() => {
    let cancelled = false;
    void (async () => {
      isLoading = true;
      try {
        const [creds, status, purge, settingsResp] = await Promise.all([
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
          fetchPurgeStatus().catch(() => null),
          fetchSettings().catch(() => null)
        ]);
        if (cancelled) return;
        savedCredentials = creds;
        spotifyStatus = status;
        if (creds.clientId) clientId = creds.clientId;
        if (purge) purgeSnapshot = purge;
        if (settingsResp) {
          settings = settingsResp;
          providers = { ...settingsResp.providers };
          qualityGrading = { ...settingsResp.qualityGrading };
        }
      } finally {
        if (!cancelled) isLoading = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  // Poll purge status while running
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
    const response = mode === 'post-fingerprint' ? await purgePostFingerprint() : await purgeAll();
    if (!response.ok) {
      purgeStartError = response.message;
      return;
    }
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

  async function handleSaveCredentials() {
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

  async function handleConnectSpotify() {
    isConnecting = true;
    spotifyError = null;
    try {
      const { authorizationUrl } = await fetchSpotifyConnectUrl();
      window.location.href = authorizationUrl;
    } catch (err) {
      spotifyError = err instanceof Error ? err.message : 'Could not start the Spotify connection.';
      isConnecting = false;
    }
  }

  async function handleDisconnectSpotify() {
    spotifyError = null;
    try {
      await disconnectSpotify();
      spotifyStatus = spotifyStatus
        ? { ...spotifyStatus, connected: false, connectedAt: null }
        : spotifyStatus;
    } catch (err) {
      spotifyError = err instanceof Error ? err.message : 'Could not disconnect Spotify.';
    }
  }

  async function handleSaveProviders() {
    if (!providers) return;
    isSavingProviders = true;
    providersResult = null;
    try {
      await updateSettings({ providers });
      providersResult = { success: true, message: 'Enrichment providers updated.' };
    } catch (err) {
      providersResult = {
        success: false,
        message: err instanceof Error ? err.message : 'Failed to save providers.'
      };
    } finally {
      isSavingProviders = false;
    }
  }

  async function handleSaveQualityGrading() {
    if (!qualityGrading) return;
    isSavingQualityGrading = true;
    qualityGradingResult = null;
    try {
      await updateSettings({ qualityGrading: { enabled: qualityGrading.enabled } });
      qualityGradingResult = {
        success: true,
        message: qualityGrading.enabled ? 'AI quality grading enabled.' : 'AI quality grading disabled.'
      };
    } catch (err) {
      qualityGradingResult = {
        success: false,
        message: err instanceof Error ? err.message : 'Failed to save AI grading setting.'
      };
    } finally {
      isSavingQualityGrading = false;
    }
  }

  async function copyRedirectUri() {
    try {
      await navigator.clipboard.writeText(redirectUri);
    } catch {
      // Clipboard may be unavailable in some embeddings; silently ignore.
    }
  }

  // ── updates ──────────────────────────────────────────────────────────────────
  const UPDATE_CMD = 'docker compose pull && docker compose up -d';
  let updateCmdCopied = $state(false);

  async function copyUpdateCommand() {
    try {
      await navigator.clipboard.writeText(UPDATE_CMD);
      updateCmdCopied = true;
      setTimeout(() => (updateCmdCopied = false), 2000);
    } catch {
      // Clipboard may be unavailable in some embeddings; silently ignore.
    }
  }

  // ── provider catalog (real provider keys) ───────────────────────────────────────
  // `dot` is only set for a genuine third-party brand mark (Spotify); every other provider gets a
  // single neutral gray identity dot — see design-audit finding on hardcoded per-provider hexes.
  type ProviderKey = keyof SettingsProvidersView;
  const PROVIDER_CATALOG: { key: ProviderKey; name: string; desc: string; auth: string; dot: string | null }[] = [
    {
      key: 'acoustId',
      name: 'AcoustID',
      desc: 'Fingerprint → MusicBrainz recording match',
      auth: 'Free',
      dot: null
    },
    {
      key: 'spotifyApi',
      name: 'Spotify API',
      desc: 'Artist + title catalog search with ISRC verification',
      auth: 'OAuth',
      dot: '#1DB954'
    },
    {
      key: 'deezer',
      name: 'Deezer',
      desc: 'Free ISRC-first + artist/title catalog search (no auth)',
      auth: 'Free',
      dot: null
    },
    {
      key: 'appleMusic',
      name: 'Apple Music',
      desc: 'Free iTunes artist + title catalog search (no auth)',
      auth: 'Free',
      dot: null
    },
    {
      key: 'musicBrainzWeb',
      name: 'MusicBrainz web',
      desc: 'Direct ISRC / artist+title lookups against MusicBrainz',
      auth: 'Free',
      dot: null
    },
    {
      key: 'tracker',
      name: 'Community trackers',
      desc: 'Juice WRLD unreleased / leak files (best-effort)',
      auth: 'Community',
      dot: null
    }
  ];

  function toggleProvider(key: ProviderKey, value: boolean) {
    if (providers) providers = { ...providers, [key]: value };
  }
</script>

<!-- Page header (mirrors PipelineHomeV2's header rhythm) -->
<header class="border-border flex shrink-0 items-end justify-between gap-4 border-b px-4 py-4 sm:px-7 sm:py-5">
  <div class="min-w-0">
    <h1 class="text-2xl font-semibold tracking-tight">Settings</h1>
    <p class="text-muted-foreground mt-1 max-w-2xl text-sm">
      Where the pipeline reads from, which providers it queries, and where the clean library lives.
    </p>
  </div>
</header>

<!-- Tab bar — Apple-style segmented control, matching the section sub-nav and
     the song-panel tabs (one tab idiom app-wide). -->
<nav class="no-scrollbar border-border flex shrink-0 items-center overflow-x-auto border-b px-4 py-2 sm:px-7" aria-label="Settings sections">
  <div class="bg-foreground/5 flex shrink-0 items-center gap-1 rounded-full p-1">
    {#each TABS as tab (tab.id)}
      {@const isActive = tab.id === activeTab}
      <button
        type="button"
        onclick={() => selectTab(tab.id)}
        data-active={isActive || undefined}
        class={cn(
          'flex shrink-0 items-center gap-1.5 rounded-full px-3 py-1.5 text-xs font-medium whitespace-nowrap transition-colors sm:px-4 sm:text-[13px]',
          'focus-visible:ring-ring/60 outline-none focus-visible:ring-2',
          isActive
            ? 'bg-background text-foreground shadow-sm'
            : 'text-muted-foreground hover:text-foreground'
        )}
      >
        {tab.label}
      </button>
    {/each}
  </div>
</nav>

<div class="min-h-0 flex-1 overflow-auto pb-[var(--mh-content-pad)]">
  <div class="mx-auto flex max-w-4xl flex-col gap-6 px-4 py-4 sm:px-7 sm:py-6">
    {#if isLoading}
      <div class="flex items-center justify-center py-16">
        <Loader2 class="text-muted-foreground size-6 animate-spin" />
      </div>
    {:else if activeTab === 'sources'}
      <!-- =================== SOURCES =================== -->
      <section class="border-border bg-card rounded-lg border">
        <header class="border-border border-b px-5 py-3.5">
          <h2 class="text-sm font-semibold">Source directories</h2>
          <p class="text-muted-foreground text-xs">
            Where MusicHoarder reads raw files. Configured via Aspire AppHost parameters
            (<code class="bg-secondary rounded px-1 py-0.5">source-directory</code> /
            <code class="bg-secondary rounded px-1 py-0.5">destination-directory</code>) — edit
            user-secrets and restart to change. Files here are never modified, only copied.
          </p>
        </header>
        <div class="divide-border divide-y">
          <div class="flex flex-col gap-2 px-5 py-4">
            <div class="text-sm font-medium">Primary source</div>
            <Input readonly value={settings?.paths.sourceDirectory ?? ''} class="font-mono text-sm" />
            <p class="text-muted-foreground text-xs">
              Files under this folder are scanned, fingerprinted, and enriched.
            </p>
          </div>
          <div class="flex flex-col gap-2 px-5 py-4">
            <div class="text-sm font-medium">fpcalc binary</div>
            <Input readonly value={settings?.paths.fpcalcPath ?? ''} class="font-mono text-sm" />
            <p class="text-muted-foreground text-xs">
              Chromaprint CLI used for fingerprinting. Must be on
              <code class="bg-secondary rounded px-1 py-0.5">$PATH</code> or an absolute path.
            </p>
          </div>
        </div>
      </section>

      <!-- Spotify (sync source) -->
      <section class="border-border bg-card rounded-lg border">
        <div class="border-border flex items-center gap-3 border-b px-5 py-3.5">
          <div class="flex size-8 shrink-0 items-center justify-center rounded-lg bg-[#1DB954]/10">
            <span class="size-2.5 rounded-full bg-[#1DB954]"></span>
          </div>
          <div class="min-w-0 flex-1">
            <h2 class="text-sm font-semibold">Spotify (sync source)</h2>
            <p class="text-muted-foreground text-xs">
              Pulls liked songs, playlists, and release metadata. Audio is never streamed.
            </p>
          </div>
          {#if spotifyStatus?.connected}
            <Badge class="border-0 bg-primary/20 text-primary">Connected</Badge>
          {:else if savedCredentials?.hasClientSecret}
            <Badge variant="secondary">Credentials set</Badge>
          {:else}
            <Badge variant="outline" class="text-muted-foreground">Not configured</Badge>
          {/if}
        </div>

        <div class="space-y-5 p-5">
          <div class="border-border bg-secondary/30 rounded-lg border p-4 text-sm">
            <div class="flex items-start gap-2">
              <KeyRound class="text-muted-foreground mt-0.5 size-4 shrink-0" />
              <div>
                <p class="mb-1 text-sm font-medium">How to get your Spotify API credentials</p>
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
                  <li>Create a new app (or use an existing one).</li>
                  <li>
                    Add the redirect URI below in <em>Settings → Redirect URIs</em>. Spotify does not
                    allow <code class="bg-secondary rounded px-1 py-0.5">localhost</code> — use a
                    loopback IP like <code class="bg-secondary rounded px-1 py-0.5">127.0.0.1</code>.
                  </li>
                  <li>Copy the Client ID and Client Secret here.</li>
                </ol>
              </div>
            </div>
          </div>

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
            <div class="flex flex-col gap-2 sm:flex-row">
              <Input
                id="client-secret"
                type={showSecret ? 'text' : 'password'}
                placeholder={savedCredentials?.hasClientSecret
                  ? '••••••••••••••••'
                  : 'Enter your Spotify Client Secret'}
                bind:value={clientSecret}
                oninput={() => (saveResult = null)}
                class="min-w-0 font-mono text-sm"
              />
              <Button
                type="button"
                variant="outline"
                class="w-full sm:w-auto"
                onclick={() => (showSecret = !showSecret)}
              >
                {showSecret ? 'Hide' : 'Show'}
              </Button>
            </div>
          </div>

          <div class="space-y-2">
            <Label>Redirect URI</Label>
            <div class="flex flex-col gap-2 sm:flex-row">
              <Input readonly value={redirectUri} class="min-w-0 font-mono text-sm" />
              <Button
                type="button"
                variant="outline"
                class="w-full sm:w-auto"
                onclick={copyRedirectUri}
              >
                <Copy class="mr-2 size-4" /> Copy
              </Button>
            </div>
            <p class="text-muted-foreground text-xs">
              Add this exact URI to your Spotify app's Redirect URIs list.
            </p>
          </div>

          {#if (settings?.spotify.scopes ?? []).length > 0}
            <div class="space-y-2">
              <Label>Scopes requested</Label>
              <div class="flex flex-wrap gap-2">
                {#each settings?.spotify.scopes ?? [] as scope (scope)}
                  <Badge variant="secondary" class="font-mono text-xs">{scope}</Badge>
                {/each}
              </div>
            </div>
          {/if}

          {#if saveResult}
            <div
              class="flex items-center gap-2 rounded-lg border px-4 py-3 text-sm {saveResult.success
                ? 'border-primary/50 bg-primary/10 text-primary'
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

          {#if spotifyError}
            <div
              class="border-destructive/50 bg-destructive/10 text-destructive flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
            >
              <AlertCircle class="mt-0.5 size-4 shrink-0" />
              <span>{spotifyError}</span>
            </div>
          {/if}

          <div class="flex flex-wrap items-center gap-2">
            <Button
              onclick={handleSaveCredentials}
              disabled={isSaving || !clientId.trim() || !clientSecret.trim()}
            >
              {#if isSaving}
                <Loader2 class="mr-2 size-4 animate-spin" />
              {:else}
                <Save class="mr-2 size-4" />
              {/if}
              Save credentials
            </Button>

            {#if spotifyStatus?.connected}
              <Button variant="outline" onclick={handleDisconnectSpotify}>Disconnect</Button>
            {:else}
              <Button
                variant="outline"
                onclick={handleConnectSpotify}
                disabled={isConnecting || !savedCredentials?.hasClientSecret}
              >
                {#if isConnecting}
                  <Loader2 class="mr-2 size-4 animate-spin" />
                {/if}
                Connect Spotify
              </Button>
            {/if}
          </div>
        </div>
      </section>
    {:else if activeTab === 'providers'}
      <!-- =================== PROVIDERS =================== -->
      <section class="border-border bg-card rounded-lg border">
        <header class="border-border border-b px-5 py-3.5">
          <h2 class="text-sm font-semibold">Active providers — queried in parallel during match</h2>
          <p class="text-muted-foreground text-xs">
            Toggle providers off to skip them entirely. New toggles take effect on the next
            enrichment cycle; previously-attempted songs keep their per-provider history.
          </p>
        </header>
        <div class="divide-border divide-y">
          {#each PROVIDER_CATALOG as p (p.key)}
            <div class="flex items-center gap-4 px-5 py-3.5">
              <span
                class="inline-block size-2.5 shrink-0 rounded-full {p.dot ? '' : 'bg-muted-foreground/50'}"
                style={p.dot ? `background: ${p.dot}` : undefined}
              ></span>
              <div class="min-w-0 flex-1">
                <div class="text-sm font-medium">{p.name}</div>
                <div class="text-muted-foreground text-xs">{p.desc}</div>
              </div>
              <Badge variant="outline" class="text-muted-foreground hidden shrink-0 sm:inline-flex">
                {p.auth}
              </Badge>
              <Switch
                checked={providers?.[p.key] ?? false}
                onCheckedChange={(v) => toggleProvider(p.key, v)}
                aria-label="Toggle {p.name}"
              />
            </div>
          {/each}
        </div>

        {#if providersResult}
          <div
            class="mx-5 mb-4 flex items-center gap-2 rounded-lg border px-4 py-2 text-sm {providersResult.success
              ? 'border-primary/50 bg-primary/10 text-primary'
              : 'border-destructive/50 bg-destructive/10 text-destructive'}"
          >
            {#if providersResult.success}
              <CheckCircle2 class="size-4 shrink-0" />
            {:else}
              <AlertCircle class="size-4 shrink-0" />
            {/if}
            {providersResult.message}
          </div>
        {/if}

        <div class="border-border flex justify-end gap-2 border-t px-5 py-3.5">
          <Button onclick={handleSaveProviders} disabled={isSavingProviders || !providers}>
            {#if isSavingProviders}
              <Loader2 class="mr-2 size-4 animate-spin" />
            {:else}
              <Save class="mr-2 size-4" />
            {/if}
            Save providers
          </Button>
        </div>
      </section>

      <!-- AI quality grading (real toggle) -->
      <section class="border-border bg-card rounded-lg border">
        <header class="border-border border-b px-5 py-3.5">
          <h2 class="flex items-center gap-2 text-sm font-semibold">
            <Sparkles class="size-4" /> AI quality grading
          </h2>
          <p class="text-muted-foreground text-xs">
            An LLM grades each enrichment result so you can benchmark and debug the algorithm. Turn
            it off to stop the background grading sweep.
          </p>
        </header>

        <div class="flex items-center gap-4 px-5 py-3.5">
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium">Enable AI quality grading</div>
            <div class="text-muted-foreground text-xs">
              {#if qualityGrading && !qualityGrading.configured}
                No API key set on the server — also set
                <code class="bg-secondary rounded px-1 py-0.5">QUALITY_GRADING_API_KEY</code>
                in the environment for grading to run.
              {:else}
                Grades enriched songs in the background and powers the AI quality page.
              {/if}
            </div>
          </div>
          <Switch
            checked={qualityGrading?.enabled ?? false}
            onCheckedChange={(v) => {
              if (qualityGrading) qualityGrading = { ...qualityGrading, enabled: v };
            }}
            aria-label="Enable AI quality grading"
          />
        </div>

        {#if qualityGradingResult}
          <div
            class="mx-5 mb-4 flex items-center gap-2 rounded-lg border px-4 py-2 text-sm {qualityGradingResult.success
              ? 'border-primary/50 bg-primary/10 text-primary'
              : 'border-destructive/50 bg-destructive/10 text-destructive'}"
          >
            {#if qualityGradingResult.success}
              <CheckCircle2 class="size-4 shrink-0" />
            {:else}
              <AlertCircle class="size-4 shrink-0" />
            {/if}
            {qualityGradingResult.message}
          </div>
        {/if}

        <div class="border-border flex justify-end gap-2 border-t px-5 py-3.5">
          <Button onclick={handleSaveQualityGrading} disabled={isSavingQualityGrading || !qualityGrading}>
            {#if isSavingQualityGrading}
              <Loader2 class="mr-2 size-4 animate-spin" />
            {:else}
              <Save class="mr-2 size-4" />
            {/if}
            Save grading
          </Button>
        </div>
      </section>

      <!-- Consensus thresholds — not API-writable yet -->
      <section class="border-border bg-card rounded-lg border border-dashed">
        <header class="border-border flex items-center gap-2 border-b border-dashed px-5 py-3.5">
          <h2 class="text-sm font-semibold">Consensus thresholds</h2>
          <span
            class="text-muted-foreground rounded-full bg-muted px-2 py-0.5 text-xs"
            >Soon</span
          >
        </header>
        <div class="px-5 py-4">
          <p class="text-muted-foreground text-xs leading-relaxed">
            Auto-accept threshold, single-source minimum, and the per-provider confidence weights
            are tuned server-side today (in <code class="bg-secondary rounded px-1 py-0.5">MusicEnricherOptions</code>).
            An in-app editor for these consensus rules — including hit-rate and latency stats per
            provider — is coming soon.
          </p>
        </div>
      </section>
    {:else if activeTab === 'rules'}
      <!-- =================== FILENAME RULES (coming soon) =================== -->
      <section class="border-border bg-card rounded-lg border border-dashed">
        <header class="border-border flex items-center gap-2 border-b border-dashed px-5 py-3.5">
          <h2 class="text-sm font-semibold">Custom filename rules</h2>
          <span
            class="text-muted-foreground rounded-full bg-muted px-2 py-0.5 text-xs"
            >Soon</span
          >
        </header>
        <div class="flex flex-col items-start gap-3 px-5 py-8">
          <div class="bg-secondary text-muted-foreground flex size-10 items-center justify-center rounded-lg">
            <FolderInput class="size-5" />
          </div>
          <p class="text-muted-foreground max-w-xl text-sm leading-relaxed">
            Capture groups from a filename and map them to metadata fallbacks — useful for YouTube
            downloads, niche scenes, leaks, or any source public databases don't track. These
            user-defined rules aren't wired to the API yet, so there's nothing to configure here
            today. This editor is coming soon.
          </p>
        </div>
      </section>
    {:else if activeTab === 'output'}
      <!-- =================== LIBRARY OUTPUT =================== -->
      <section class="border-border bg-card rounded-lg border">
        <header class="border-border border-b px-5 py-3.5">
          <h2 class="text-sm font-semibold">Library output</h2>
          <p class="text-muted-foreground text-xs">
            Where the organised library is written after enrichment + tag-write. Destination is set
            via the <code class="bg-secondary rounded px-1 py-0.5">destination-directory</code>
            AppHost parameter.
          </p>
        </header>
        <div class="divide-border divide-y">
          <div class="flex flex-col gap-2 px-5 py-4">
            <div class="text-sm font-medium">Destination</div>
            <Input
              readonly
              value={settings?.paths.destinationDirectory ?? ''}
              class="font-mono text-sm"
            />
            <p class="text-muted-foreground text-xs">Where enriched files land.</p>
          </div>
        </div>
      </section>

      <!-- Folder / filename templates — not API-writable yet -->
      <section class="border-border bg-card rounded-lg border border-dashed">
        <header class="border-border flex items-center gap-2 border-b border-dashed px-5 py-3.5">
          <h2 class="text-sm font-semibold">Folder &amp; filename templates</h2>
          <span
            class="text-muted-foreground rounded-full bg-muted px-2 py-0.5 text-xs"
            >Soon</span
          >
        </header>
        <div class="divide-border divide-y">
          <div class="flex items-center gap-4 px-5 py-3.5">
            <div class="min-w-0 flex-1">
              <div class="text-sm font-medium">Folder structure</div>
              <div class="text-muted-foreground text-xs">
                Path template the library builder uses today.
              </div>
            </div>
            <code class="bg-secondary rounded px-2 py-1 font-mono text-xs"
              >{'{albumartist}/{album}'}</code
            >
          </div>
          <div class="flex items-center gap-4 px-5 py-3.5">
            <div class="min-w-0 flex-1">
              <div class="text-sm font-medium">Filename</div>
              <div class="text-muted-foreground text-xs">
                How individual track files are named today.
              </div>
            </div>
            <code class="bg-secondary rounded px-2 py-1 font-mono text-xs"
              >{'{track:02} - {title}'}</code
            >
          </div>
          <div class="px-5 py-4">
            <p class="text-muted-foreground text-xs leading-relaxed">
              These templates are fixed in the library builder for now and aren't exposed by the
              settings API. Editable path/filename templates and re-encode options are coming soon.
            </p>
          </div>
        </div>
      </section>
    {:else if activeTab === 'account'}
      <!-- =================== ACCOUNT =================== -->
      <section class="border-border bg-card rounded-lg border">
        <header class="border-border border-b px-5 py-3.5">
          <h2 class="text-sm font-semibold">Account</h2>
          <p class="text-muted-foreground text-xs">
            Signed-in user. Sign in by magic link or passkey — no passwords. Roles control who can
            mutate pipeline state.
          </p>
        </header>

        <div class="space-y-5 p-5">
          <div class="flex items-center gap-4">
            <div
              class="text-foreground flex size-12 items-center justify-center rounded-full bg-gradient-to-br from-cyan-500/80 to-indigo-500/80 font-semibold text-white shadow-sm"
            >
              {initials}
            </div>
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-medium">
                {user?.displayName ?? user?.email ?? '—'}
              </div>
              <div class="text-muted-foreground truncate font-mono text-xs">
                {user?.email ?? '—'}
              </div>
            </div>
            <Badge variant={user?.role === 'Owner' ? 'default' : 'secondary'}>
              {user?.role ?? 'Anonymous'}
            </Badge>
          </div>

          {#if user?.role === 'Demo'}
            <div
              class="border-border bg-secondary/40 text-foreground/80 rounded-lg border px-4 py-3 text-xs"
            >
              You're signed in as the demo account. It's strictly read-only: you can browse and play
              the seeded library, but every mutating action (scan, enrich, build, approve, edit,
              delete, settings, purge…) returns
              <span class="bg-secondary rounded px-1 font-mono">403 demo_read_only</span>.
            </div>
          {/if}

          <div class="border-border flex flex-wrap items-center gap-2 border-t pt-4">
            <Button variant="outline" onclick={() => handleSignOut(false)}>
              <LogOut class="mr-2 size-4" /> Sign out
            </Button>
            <Button variant="ghost" onclick={() => handleSignOut(true)}>Sign out everywhere</Button>
          </div>
        </div>
      </section>

      {#if user?.role === 'Owner'}
        <section class="border-border bg-card rounded-lg border">
          <header class="border-border border-b px-5 py-3.5">
            <h2 class="flex items-center gap-2 text-sm font-semibold">
              <KeyRound class="size-4" /> Passkeys
            </h2>
            <p class="text-muted-foreground text-xs">
              Sign in with Touch ID, Windows Hello, or a security key — no email needed. Enroll one
              on each device you use. Keep at least one magic-link-capable email so you can recover
              access.
            </p>
          </header>

          <div class="space-y-5 p-5">
            {#if !passkeySupported}
              <div
                class="border-border bg-secondary/40 text-foreground/80 rounded-lg border px-4 py-3 text-xs"
              >
                This browser doesn't support passkeys.
              </div>
            {:else}
              <div class="flex flex-wrap items-end gap-2">
                <div class="min-w-0 flex-1 space-y-2">
                  <Label for="passkey-name">Passkey name</Label>
                  <Input
                    id="passkey-name"
                    placeholder="e.g. MacBook Touch ID"
                    bind:value={newPasskeyName}
                    oninput={() => (passkeyError = null)}
                  />
                </div>
                <Button onclick={handleAddPasskey} disabled={isAddingPasskey}>
                  {#if isAddingPasskey}
                    <Loader2 class="mr-2 size-4 animate-spin" />
                  {:else}
                    <KeyRound class="mr-2 size-4" />
                  {/if}
                  Add a passkey
                </Button>
              </div>

              {#if passkeyError}
                <div
                  class="border-destructive/50 bg-destructive/10 text-destructive flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
                >
                  <AlertCircle class="mt-0.5 size-4 shrink-0" />
                  <span>{passkeyError}</span>
                </div>
              {/if}

              <div class="border-border divide-border divide-y rounded-lg border">
                {#if passkeysLoading}
                  <div class="text-muted-foreground px-4 py-3 text-sm">Loading…</div>
                {:else if passkeys.length === 0}
                  <div class="text-muted-foreground px-4 py-3 text-sm">No passkeys enrolled yet.</div>
                {:else}
                  {#each passkeys as passkey (passkey.id)}
                    <div class="flex items-center gap-3 px-4 py-3">
                      <KeyRound class="text-muted-foreground size-4 shrink-0" />
                      <div class="min-w-0 flex-1">
                        <div class="truncate text-sm font-medium">{passkey.displayName}</div>
                        <div class="text-muted-foreground text-xs">
                          Added {new Date(passkey.createdAtUtc).toLocaleDateString()}
                          {#if passkey.lastUsedAtUtc}
                            · last used {new Date(passkey.lastUsedAtUtc).toLocaleDateString()}
                          {/if}
                        </div>
                      </div>
                      <Button
                        variant="ghost"
                        size="icon"
                        onclick={() => handleRemovePasskey(passkey.id)}
                        aria-label="Remove passkey"
                      >
                        <Trash2 class="size-4" />
                      </Button>
                    </div>
                  {/each}
                {/if}
              </div>
            {/if}
          </div>
        </section>

        <!-- Anonymous telemetry — not API-writable yet -->
        <section class="border-border bg-card rounded-lg border border-dashed">
          <header class="border-border flex items-center gap-2 border-b border-dashed px-5 py-3.5">
            <h2 class="text-sm font-semibold">Anonymous telemetry</h2>
            <span
              class="text-muted-foreground rounded-full bg-muted px-2 py-0.5 text-xs"
              >Soon</span
            >
          </header>
          <div class="px-5 py-4">
            <p class="text-muted-foreground text-xs leading-relaxed">
              MusicHoarder is self-hosted and ships no telemetry today — track names, artists, and
              paths never leave your server. A per-user opt-in for anonymous pipeline-performance
              stats is coming soon.
            </p>
          </div>
        </section>

        <!-- Danger zone (real purge actions) -->
        <section class="border-destructive/40 bg-card rounded-lg border">
          <div class="border-destructive/40 flex items-center gap-3 border-b px-5 py-3.5">
            <div class="bg-destructive/10 flex size-8 items-center justify-center rounded-lg">
              <AlertTriangle class="text-destructive size-4" />
            </div>
            <div class="min-w-0 flex-1">
              <h2 class="text-sm font-semibold">Danger zone</h2>
              <p class="text-muted-foreground text-xs">
                Irreversible actions that purge pipeline state. Make sure no job is running.
              </p>
            </div>
          </div>

          <div class="divide-border divide-y">
            <div class="flex flex-col gap-3 p-5 sm:flex-row sm:items-start sm:justify-between">
              <div class="flex-1 pr-4">
                <h3 class="text-sm font-semibold">Reset enrichment data</h3>
                <p class="text-muted-foreground mt-1 text-xs">
                  Keeps your scanned files and fingerprints. Clears enrichment results, provider
                  attempts, lyrics, duplicate detection, and library-build status for every active
                  song, and deletes any files that were copied to the destination folder.
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

            <div class="flex flex-col gap-3 p-5 sm:flex-row sm:items-start sm:justify-between">
              <div class="flex-1 pr-4">
                <h3 class="text-sm font-semibold">Purge all data</h3>
                <p class="text-muted-foreground mt-1 text-xs">
                  Removes every song, provider attempt, and cached Spotify match from the database,
                  and deletes any files copied to the destination folder. Source files are not
                  touched. The next run re-scans and re-fingerprints from source.
                </p>
              </div>
              <AlertDialog.Root bind:open={purgeAllDialogOpen} onOpenChange={onPurgeAllDialogOpenChange}>
                <AlertDialog.Trigger>
                  {#snippet child({ props })}
                    <Button {...props} variant="destructive" class="shrink-0 gap-2" disabled={purgeRunning}>
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
                      affected. If you're demoing this build or benchmarking match quality, this also
                      wipes any grading history you were comparing against. This runs in the
                      background — you can navigate away and the progress will be here when you come
                      back. This cannot be undone.
                    </AlertDialog.Description>
                  </AlertDialog.Header>
                  <div class="px-6 pb-2">
                    <Label for="purge-all-confirm" class="text-xs text-muted-foreground">
                      Type <span class="font-semibold text-foreground">{PURGE_ALL_CONFIRM_WORD}</span> to confirm
                    </Label>
                    <Input
                      id="purge-all-confirm"
                      autocomplete="off"
                      bind:value={purgeAllConfirmText}
                      placeholder={PURGE_ALL_CONFIRM_WORD}
                      class="mt-1.5"
                    />
                  </div>
                  <AlertDialog.Footer>
                    <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                    <AlertDialog.Action
                      variant="destructive"
                      disabled={!purgeAllConfirmed}
                      onclick={() => handlePurge('all')}
                    >
                      Purge all data
                    </AlertDialog.Action>
                  </AlertDialog.Footer>
                </AlertDialog.Content>
              </AlertDialog.Root>
            </div>

            {#if purgeStartError}
              <div class="px-5 pt-4 pb-5">
                <div
                  class="border-destructive/50 bg-destructive/10 text-destructive flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
                >
                  <AlertCircle class="mt-0.5 size-4 shrink-0" />
                  <p>{purgeStartError}</p>
                </div>
              </div>
            {/if}

            {#if purgeSnapshot && purgeSnapshot.status !== 'idle'}
              <div class="px-5 pt-4 pb-5">
                <PurgeStatusBanner snapshot={purgeSnapshot} />
              </div>
            {/if}
          </div>
        </section>
      {/if}
    {:else if activeTab === 'updates'}
      <!-- =================== UPDATES =================== -->
      <section class="border-border bg-card rounded-lg border">
        <header class="border-border flex items-center gap-2 border-b px-5 py-3.5">
          <h2 class="flex items-center gap-2 text-sm font-semibold">
            <Sparkles class="size-4" /> Updating MusicHoarder
          </h2>
          <p class="text-muted-foreground text-xs">
            MusicHoarder ships as containers. Pulling the latest images and recreating the stack
            applies a new release — your database and library are untouched.
          </p>
        </header>

        <div class="space-y-4 p-5">
          <div class="flex flex-col gap-2">
            <div class="text-sm font-medium">Update command</div>
            <div class="flex items-center justify-between gap-2 rounded-md border border-border bg-secondary/30 px-3 py-2">
              <code class="min-w-0 flex-1 truncate text-xs">{UPDATE_CMD}</code>
              <Button variant="ghost" size="icon" class="size-8 shrink-0" onclick={copyUpdateCommand} aria-label="Copy update command" title="Copy update command">
                {#if updateCmdCopied}
                  <CheckCircle2 class="size-4 text-primary" />
                {:else}
                  <Copy class="size-4" />
                {/if}
              </Button>
            </div>
            <p class="text-muted-foreground text-xs">
              Run this in your compose stack's directory to pull the new images and recreate the
              containers.
            </p>
          </div>

          <div class="border-border rounded-md border border-dashed p-3">
            <p class="text-muted-foreground text-xs leading-relaxed">
              Running <a href="https://github.com/ofkm/arcane" target="_blank" rel="noopener noreferrer" class="text-primary underline">Arcane</a>?
              Add the <code class="bg-secondary rounded px-1 py-0.5">arcane.stack.auto-update</code> label
              to your compose stack to let it pull and redeploy new releases automatically instead of
              running the command by hand.
            </p>
          </div>

          <p class="text-muted-foreground text-xs">
            The current version and whether a newer release is available show up as a banner across
            the app when one is out — this tab is just the reference copy of the same instructions.
          </p>
        </div>
      </section>
    {/if}
  </div>
</div>
