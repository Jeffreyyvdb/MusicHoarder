<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { Folder, Tag, Database, ChevronRight, Check, AlertTriangle } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import PurgeStatusBanner from '$lib/components/settings/PurgeStatusBanner.svelte';
  import {
    fetchSettings,
    updateSettings,
    fetchSpotifyCredentials,
    fetchSpotifyStatus,
    saveSpotifyCredentials,
    fetchStats,
    fetchPurgeStatus,
    purgeAll,
    purgePostFingerprint,
    signOut,
    type SettingsResponse,
    type SettingsProvidersView,
    type SettingsQualityGradingView,
    type SpotifyCredentialsResponse,
    type SpotifyStatusResponse,
    type ApiStats,
    type PurgeMode,
    type PurgeSnapshot
  } from '$lib/api-client';
  import { formatFileSize } from '$lib/formatters';
  import { albumTint } from '$lib/album-tint';

  type View = null | 'paths' | 'spotify' | 'enrich' | 'data';
  let view = $state<View>(null);

  const user = $derived(
    page.data.user as
      | { id: string; email: string; role: 'Owner' | 'Demo'; displayName: string | null }
      | undefined
  );
  const name = $derived(user?.displayName ?? user?.email ?? 'Account');
  const initials = $derived((user?.displayName ?? user?.email ?? '?').slice(0, 2).toUpperCase());
  const tint = $derived(albumTint(name, user?.email ?? 'account'));

  let settings = $state<SettingsResponse | null>(null);
  let providers = $state<SettingsProvidersView | null>(null);
  let qualityGrading = $state<SettingsQualityGradingView | null>(null);
  let creds = $state<SpotifyCredentialsResponse | null>(null);
  let spotifyStatus = $state<SpotifyStatusResponse | null>(null);
  let stats = $state<ApiStats | null>(null);
  let purgeSnapshot = $state<PurgeSnapshot | null>(null);

  let clientId = $state('');
  let clientSecret = $state('');
  let showSecret = $state(false);
  let savingCreds = $state(false);
  let savingProviders = $state(false);
  let savingQualityGrading = $state(false);
  let purgeError = $state<string | null>(null);
  let confirmPurge = $state(false);
  let purgeText = $state('');

  async function load() {
    const [s, c, st, stat, purge] = await Promise.all([
      fetchSettings().catch(() => null),
      fetchSpotifyCredentials().catch(() => ({ clientId: null, hasClientSecret: false }) as SpotifyCredentialsResponse),
      fetchSpotifyStatus().catch(() => ({ connected: false, hasCredentials: false, tokenExpired: false }) as SpotifyStatusResponse),
      fetchStats().catch(() => null),
      fetchPurgeStatus().catch(() => null)
    ]);
    if (s) {
      settings = s;
      providers = { ...s.providers };
      qualityGrading = { ...s.qualityGrading };
    }
    creds = c;
    spotifyStatus = st;
    stats = stat;
    purgeSnapshot = purge;
    if (c.clientId) clientId = c.clientId;
  }

  onMount(load);

  const redirectUri = $derived(
    settings?.spotify.oAuthRedirectBaseUrl
      ? `${settings.spotify.oAuthRedirectBaseUrl.replace(/\/$/, '')}/api/spotify/callback`
      : 'http://127.0.0.1:5142/api/spotify/callback'
  );

  const storageUsed = $derived(stats?.storage?.totalBytes ?? 0);
  const trackTotal = $derived(stats?.tracks?.total ?? 0);

  const PROVIDERS: { key: keyof SettingsProvidersView; name: string; dot: string }[] = [
    { key: 'acoustId', name: 'AcoustID', dot: 'oklch(0.68 0.18 30)' },
    { key: 'spotifyApi', name: 'Spotify API', dot: '#1DB954' },
    { key: 'deezer', name: 'Deezer', dot: '#a238ff' },
    { key: 'appleMusic', name: 'Apple Music', dot: '#fa2d48' },
    { key: 'musicBrainzWeb', name: 'MusicBrainz web', dot: 'oklch(0.62 0.16 280)' },
    { key: 'tracker', name: 'Community trackers', dot: 'oklch(0.58 0.12 60)' }
  ];

  async function saveProviders() {
    if (!providers) return;
    savingProviders = true;
    try {
      await updateSettings({ providers });
    } finally {
      savingProviders = false;
    }
  }

  async function saveQualityGrading() {
    if (!qualityGrading) return;
    savingQualityGrading = true;
    try {
      await updateSettings({ qualityGrading: { enabled: qualityGrading.enabled } });
    } finally {
      savingQualityGrading = false;
    }
  }

  async function saveCreds() {
    if (!clientId.trim() || !clientSecret.trim()) return;
    savingCreds = true;
    try {
      await saveSpotifyCredentials(clientId.trim(), clientSecret.trim());
      creds = { clientId: clientId.trim(), hasClientSecret: true };
      clientSecret = '';
    } finally {
      savingCreds = false;
    }
  }

  async function runPurge(mode: PurgeMode) {
    purgeError = null;
    const res = mode === 'post-fingerprint' ? await purgePostFingerprint() : await purgeAll();
    if (!res.ok) {
      purgeError = res.message;
      return;
    }
    confirmPurge = false;
    purgeText = '';
    purgeSnapshot = await fetchPurgeStatus().catch(() => purgeSnapshot);
  }

  async function doSignOut() {
    await signOut(false);
    await goto('/login', { invalidateAll: true });
  }
</script>

{#if view === 'paths'}
  <div class="mob">
    <MobileHeader back="Profile" onback={() => (view = null)} title="Paths & storage" />
    <div class="mob-scroll">
      <div class="mob-grouped-h">Source</div>
      <div class="mob-field"><input class="mob-input" readonly value={settings?.paths.sourceDirectory ?? '—'} /></div>
      <div class="mob-grouped-h">Destination</div>
      <div class="mob-field"><input class="mob-input" readonly value={settings?.paths.destinationDirectory ?? '—'} /></div>
      <div class="mob-grouped-h">fpcalc binary</div>
      <div class="mob-field"><input class="mob-input" readonly value={settings?.paths.fpcalcPath ?? '—'} /></div>
      <div class="mob-grouped-h">Storage</div>
      <div class="px-4 pt-1 pb-6">
        <div class="text-muted-foreground font-mono text-[11.5px]">
          {formatFileSize(storageUsed)} used · {trackTotal.toLocaleString()} tracks
        </div>
      </div>
    </div>
  </div>
{:else if view === 'spotify'}
  <div class="mob">
    <MobileHeader back="Profile" onback={() => (view = null)} title="Spotify" />
    <div class="mob-scroll">
      <div class="mob-set-acct">
        <div class="mob-set-avatar" style="background: #1db954;">
          <svg width="28" height="28" viewBox="0 0 24 24" aria-hidden="true">
            <path d="M6 9.5c4-1.2 9-1 13 1M6.8 12.5c3.4-1 7.5-0.7 11 1.1M7.8 15.4c2.6-0.7 5.7-0.5 8.6 1" stroke="#000" stroke-width="2.2" stroke-linecap="round" fill="none" />
          </svg>
        </div>
        <div class="mob-set-acct-meta">
          <div class="mob-set-acct-name">Spotify</div>
          <div class="mob-set-acct-email">{spotifyStatus?.connected ? 'Connected' : 'Not connected'}</div>
        </div>
        {#if spotifyStatus?.connected}<span class="mob-pill ok">connected</span>{/if}
      </div>

      <div class="mob-grouped-h">Credentials</div>
      <div class="mob-field">
        <div class="mob-field-l">CLIENT ID</div>
        <input class="mob-input" bind:value={clientId} placeholder="client id" />
      </div>
      <div class="mob-field">
        <div class="mob-field-l">CLIENT SECRET</div>
        <div class="flex gap-1.5">
          <input
            class="mob-input flex-1"
            type={showSecret ? 'text' : 'password'}
            bind:value={clientSecret}
            placeholder={creds?.hasClientSecret ? '•••••••• (saved)' : 'paste secret'}
          />
          <button class="mob-btn w-auto px-3.5" onclick={() => (showSecret = !showSecret)}>
            {showSecret ? 'Hide' : 'Show'}
          </button>
        </div>
      </div>
      <div class="mob-field">
        <div class="mob-field-l">REDIRECT URI</div>
        <input class="mob-input" readonly value={redirectUri} />
      </div>
      <div class="mob-grouped-foot">Add this exact URI to your Spotify developer app under "Redirect URIs".</div>

      <div class="mob-grouped-h">Scopes</div>
      <div class="mob-grouped">
        {#each settings?.spotify.scopes ?? [] as s (s)}
          <div class="mob-row">
            <div class="mob-row-meta"><div class="mob-row-t font-mono text-[13px]">{s}</div></div>
            <Check size={13} class="text-primary" strokeWidth={2.5} />
          </div>
        {/each}
      </div>

      <div class="flex flex-col gap-2.5 p-4">
        <button class="mob-btn primary" disabled={savingCreds} onclick={saveCreds}>
          {savingCreds ? 'Saving…' : 'Save credentials'}
        </button>
      </div>
    </div>
  </div>
{:else if view === 'enrich'}
  <div class="mob">
    <MobileHeader back="Profile" onback={() => (view = null)} title="Enrichment sources" />
    <div class="mob-scroll">
      <div class="mob-grouped-h">Sources</div>
      <div class="mob-grouped">
        {#each PROVIDERS as p (p.key)}
          <div class="mob-row">
            <span class="size-2.5 shrink-0 rounded-full" style="background: {p.dot}; box-shadow: 0 0 0 1.5px rgba(255,255,255,0.6);"></span>
            <div class="mob-row-meta"><div class="mob-row-t">{p.name}</div></div>
            <button
              class="mob-toggle {providers?.[p.key] ? 'on' : ''}"
              aria-label={p.name}
              aria-pressed={providers?.[p.key] ?? false}
              onclick={() => providers && (providers = { ...providers, [p.key]: !providers[p.key] })}
            ></button>
          </div>
        {/each}
      </div>
      <div class="p-4">
        <button class="mob-btn primary" disabled={savingProviders} onclick={saveProviders}>
          {savingProviders ? 'Saving…' : 'Save sources'}
        </button>
      </div>

      <div class="mob-grouped-h">AI quality grading</div>
      <div class="mob-grouped">
        <div class="mob-row">
          <div class="mob-row-meta">
            <div class="mob-row-t">Enable AI grading</div>
            <div class="mob-row-s">
              {#if qualityGrading && !qualityGrading.configured}
                No API key set on the server
              {:else}
                Grades enriched songs in the background
              {/if}
            </div>
          </div>
          <button
            class="mob-toggle {qualityGrading?.enabled ? 'on' : ''}"
            aria-label="Enable AI grading"
            aria-pressed={qualityGrading?.enabled ?? false}
            onclick={() =>
              qualityGrading && (qualityGrading = { ...qualityGrading, enabled: !qualityGrading.enabled })}
          ></button>
        </div>
      </div>
      <div class="p-4">
        <button class="mob-btn primary" disabled={savingQualityGrading} onclick={saveQualityGrading}>
          {savingQualityGrading ? 'Saving…' : 'Save grading'}
        </button>
      </div>
    </div>
  </div>
{:else if view === 'data'}
  <div class="mob">
    <MobileHeader back="Profile" onback={() => (view = null)} title="Data & resets" />
    <div class="mob-scroll">
      {#if purgeSnapshot}
        <div class="px-4 pt-3"><PurgeStatusBanner snapshot={purgeSnapshot} /></div>
      {/if}
      <div class="mob-grouped-h">Resets</div>
      <div class="flex flex-col gap-2 px-4 pb-4">
        <button class="mob-btn warn" onclick={() => runPurge('post-fingerprint')}>
          <AlertTriangle size={13} />Reset enrichment data
        </button>
      </div>
      {#if purgeError}
        <div class="text-destructive px-4 pb-2 text-[12.5px]">{purgeError}</div>
      {/if}

      <div class="mob-grouped-h" style="color: #c23a3a;">Danger zone</div>
      <div class="mx-4 mb-6 rounded-xl border p-4" style="border-color: oklch(0.6 0.2 25 / 0.3); background: oklch(0.6 0.2 25 / 0.03);">
        <div class="text-sm font-semibold">Purge all data</div>
        <div class="text-muted-foreground mt-1.5 mb-3.5 text-[12.5px] leading-relaxed">
          Deletes every cache, match decision, and destination file.
          <strong class="text-foreground"> Source files are not touched.</strong> Cannot be undone.
        </div>
        {#if !confirmPurge}
          <button class="mob-btn danger" onclick={() => (confirmPurge = true)}>Purge all data…</button>
        {:else}
          <div class="text-muted-foreground mb-2 font-mono text-xs">
            Type <strong style="color: #c23a3a;">PURGE</strong> to confirm:
          </div>
          <input class="mob-input mb-2.5" bind:value={purgeText} placeholder="PURGE" />
          <div class="flex gap-2">
            <button class="mob-btn" onclick={() => { confirmPurge = false; purgeText = ''; }}>Cancel</button>
            <button class="mob-btn danger" disabled={purgeText !== 'PURGE'} onclick={() => runPurge('all')}>Yes, purge</button>
          </div>
        {/if}
      </div>
    </div>
  </div>
{:else}
  <div class="mob">
    <MobileHeader title="Profile" />
    <div class="mob-scroll">
      <div class="mob-set-acct">
        <div class="mob-set-avatar" style="background: linear-gradient(135deg, {tint.from}, {tint.to});">{initials}</div>
        <div class="mob-set-acct-meta">
          <div class="mob-set-acct-name">{name}</div>
          {#if user?.email}<div class="mob-set-acct-email font-mono">{user.email}</div>{/if}
          {#if user?.role}<div class="text-muted-foreground mt-1 font-mono text-[11px]">{user.role}</div>{/if}
        </div>
      </div>

      <div class="mob-grouped-h">Library</div>
      <div class="mob-grouped">
        <button class="mob-row" onclick={() => (view = 'paths')}>
          <Folder size={16} class="text-muted-foreground" />
          <div class="mob-row-meta">
            <div class="mob-row-t">Paths & storage</div>
            <div class="mob-row-s">{formatFileSize(storageUsed)} used</div>
          </div>
          <ChevronRight size={12} class="mob-row-chev" />
        </button>
        <button class="mob-row" onclick={() => (view = 'enrich')}>
          <Tag size={16} class="text-muted-foreground" />
          <div class="mob-row-meta">
            <div class="mob-row-t">Enrichment sources</div>
            <div class="mob-row-s">
              {providers ? PROVIDERS.filter((p) => providers?.[p.key]).length : 0} of {PROVIDERS.length} enabled
            </div>
          </div>
          <ChevronRight size={12} class="mob-row-chev" />
        </button>
      </div>

      <div class="mob-grouped-h">Integrations</div>
      <div class="mob-grouped">
        <button class="mob-row" onclick={() => (view = 'spotify')}>
          <span class="mob-sp-dot">
            <svg width="20" height="20" viewBox="0 0 24 24" fill="#1db954" aria-hidden="true">
              <circle cx="12" cy="12" r="11" />
              <path d="M6 9.5c4-1.2 9-1 13 1M6.8 12.5c3.4-1 7.5-0.7 11 1.1M7.8 15.4c2.6-0.7 5.7-0.5 8.6 1" stroke="#000" stroke-width="1.6" stroke-linecap="round" fill="none" />
            </svg>
          </span>
          <div class="mob-row-meta">
            <div class="mob-row-t">Spotify</div>
            <div class="mob-row-s">{spotifyStatus?.connected ? 'Connected' : creds?.hasClientSecret ? 'Credentials set' : 'Not configured'}</div>
          </div>
          {#if spotifyStatus?.connected}<span class="mob-pill ok">connected</span>{/if}
          <ChevronRight size={12} class="mob-row-chev" />
        </button>
      </div>

      <div class="mob-grouped-h">Data</div>
      <div class="mob-grouped">
        <button class="mob-row" onclick={() => (view = 'data')}>
          <Database size={16} class="text-muted-foreground" />
          <div class="mob-row-meta">
            <div class="mob-row-t">Caches & resets</div>
            <div class="mob-row-s">Reset enrichment · purge all data</div>
          </div>
          <ChevronRight size={12} class="mob-row-chev" />
        </button>
      </div>

      <div class="mob-grouped-h">Account</div>
      <div class="mob-grouped">
        <button class="mob-row" onclick={doSignOut}>
          <div class="mob-row-meta"><div class="mob-row-t" style="color: #c23a3a;">Sign out</div></div>
        </button>
      </div>

      <div class="text-muted-foreground px-4 pt-6 pb-4 text-center font-mono text-[11px]">
        MusicHoarder · self-hosted
      </div>
    </div>
  </div>
{/if}
