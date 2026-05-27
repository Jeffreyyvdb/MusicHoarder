<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import { Badge } from '$lib/components/ui/badge';
  import { Slider } from '$lib/components/ui/slider';
  import * as Tabs from '$lib/components/ui/tabs';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import PurgeStatusBanner from '$lib/components/settings/PurgeStatusBanner.svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    fetchSpotifyCredentials,
    saveSpotifyCredentials,
    fetchSpotifyStatus,
    fetchSettings,
    updateSettings,
    purgeAll,
    purgePostFingerprint,
    fetchPurgeStatus,
    signOut,
    registerPasskey,
    listPasskeys,
    deletePasskey,
    type PasskeyView,
    type PurgeMode,
    type PurgeSnapshot,
    type SettingsResponse,
    type SettingsProvidersView,
    type SettingsPipelineView,
    type SettingsQualityGradingView,
    type SpotifyCredentialsResponse,
    type SpotifyStatusResponse,
    listMatchRules,
    createMatchRule,
    updateMatchRule,
    deleteMatchRule,
    testMatchRule,
    type MatchRuleView,
    type MatchRuleSourceField,
    type MatchRuleTestResponse
  } from '$lib/api-client';
  import { isPasskeySupported } from '$lib/webauthn-client';
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
    Trash2,
    Folder,
    Database,
    Tag,
    SlidersHorizontal,
    Copy,
    UserRound,
    LogOut,
    Wand2,
    Plus,
    Pencil,
    X
  } from '@lucide/svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import MobileSettings from '$lib/components/mobile/MobileSettings.svelte';

  const isMobile = new IsMobile();

  const user = $derived(page.data.user as { id: string; email: string; role: 'Owner' | 'Demo'; displayName: string | null } | undefined);
  const initials = $derived((user?.displayName ?? user?.email ?? '?').slice(0, 2).toUpperCase());

  async function handleSignOut(allSessions = false) {
    await signOut(allSessions);
    await goto('/login', { invalidateAll: true });
  }

  // Passkeys (owner-only)
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

  // ---- Match rules (owner-only) ----
  let matchRules = $state<MatchRuleView[]>([]);
  let matchRulesLoading = $state(false);
  let matchRuleError = $state<string | null>(null);

  // Editor form (shared for create + edit; editingId === null means "new")
  let editingId = $state<number | null>(null);
  let ruleName = $state('');
  let rulePattern = $state('');
  let ruleSourceField = $state<MatchRuleSourceField>('title');
  let rulePriority = $state(100);
  let ruleEnabled = $state(true);
  let ruleAlbumOverride = $state('');
  let ruleAlbumArtistOverride = $state('');
  let isSavingRule = $state(false);

  // Live test box
  let testSample = $state('');
  let testResult = $state<MatchRuleTestResponse | null>(null);

  const isEditingRule = $derived(editingId !== null);

  function resetRuleForm() {
    editingId = null;
    ruleName = '';
    rulePattern = '';
    ruleSourceField = 'title';
    rulePriority = 100;
    ruleEnabled = true;
    ruleAlbumOverride = '';
    ruleAlbumArtistOverride = '';
    matchRuleError = null;
  }

  function startEditRule(rule: MatchRuleView) {
    editingId = rule.id;
    ruleName = rule.name;
    rulePattern = rule.pattern;
    ruleSourceField = rule.sourceField;
    rulePriority = rule.priority;
    ruleEnabled = rule.enabled;
    ruleAlbumOverride = rule.albumOverride ?? '';
    ruleAlbumArtistOverride = rule.albumArtistOverride ?? '';
    matchRuleError = null;
  }

  $effect(() => {
    if (isMobile.current || user?.role !== 'Owner') return;
    let cancelled = false;
    void (async () => {
      matchRulesLoading = true;
      try {
        const list = await listMatchRules();
        if (!cancelled) matchRules = list;
      } catch {
        // non-fatal; section just shows empty
      } finally {
        if (!cancelled) matchRulesLoading = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  // Debounced live test as the user types a pattern + sample.
  $effect(() => {
    const pattern = rulePattern.trim();
    const sample = testSample;
    if (!pattern || !sample.trim()) {
      testResult = null;
      return;
    }
    let cancelled = false;
    const id = setTimeout(async () => {
      try {
        const result = await testMatchRule(pattern, sample);
        if (!cancelled) testResult = result;
      } catch {
        if (!cancelled) testResult = null;
      }
    }, 250);
    return () => {
      cancelled = true;
      clearTimeout(id);
    };
  });

  async function handleSaveRule() {
    if (!ruleName.trim() || !rulePattern.trim()) {
      matchRuleError = 'Name and pattern are required.';
      return;
    }
    isSavingRule = true;
    matchRuleError = null;
    try {
      const input = {
        name: ruleName.trim(),
        pattern: rulePattern.trim(),
        sourceField: ruleSourceField,
        enabled: ruleEnabled,
        priority: rulePriority,
        albumOverride: ruleAlbumOverride.trim() || null,
        albumArtistOverride: ruleAlbumArtistOverride.trim() || null
      };
      if (editingId !== null) {
        const updated = await updateMatchRule(editingId, input);
        matchRules = matchRules.map((r) => (r.id === updated.id ? updated : r));
      } else {
        const created = await createMatchRule(input);
        matchRules = [...matchRules, created];
      }
      resetRuleForm();
    } catch (err) {
      matchRuleError = err instanceof Error ? err.message : 'Could not save the rule.';
    } finally {
      isSavingRule = false;
    }
  }

  async function handleToggleRule(rule: MatchRuleView) {
    try {
      const updated = await updateMatchRule(rule.id, {
        name: rule.name,
        pattern: rule.pattern,
        sourceField: rule.sourceField,
        enabled: !rule.enabled,
        priority: rule.priority,
        albumOverride: rule.albumOverride,
        albumArtistOverride: rule.albumArtistOverride
      });
      matchRules = matchRules.map((r) => (r.id === updated.id ? updated : r));
    } catch (err) {
      matchRuleError = err instanceof Error ? err.message : 'Could not update the rule.';
    }
  }

  async function handleDeleteRule(id: number) {
    try {
      await deleteMatchRule(id);
      matchRules = matchRules.filter((r) => r.id !== id);
      if (editingId === id) resetRuleForm();
    } catch (err) {
      matchRuleError = err instanceof Error ? err.message : 'Could not delete the rule.';
    }
  }

  // Spotify credential form state
  let clientId = $state('');
  let clientSecret = $state('');
  let isSaving = $state(false);
  let showSecret = $state(false);
  let savedCredentials = $state<SpotifyCredentialsResponse | null>(null);
  let spotifyStatus = $state<SpotifyStatusResponse | null>(null);
  let saveResult = $state<{ success: boolean; message: string } | null>(null);

  // Settings form state
  let isLoading = $state(true);
  let settings = $state<SettingsResponse | null>(null);
  let providers = $state<SettingsProvidersView | null>(null);
  let pipeline = $state<SettingsPipelineView | null>(null);
  let isSavingProviders = $state(false);
  let isSavingPipeline = $state(false);
  let providersResult = $state<{ success: boolean; message: string } | null>(null);
  let pipelineResult = $state<{ success: boolean; message: string } | null>(null);
  let qualityGrading = $state<SettingsQualityGradingView | null>(null);
  let isSavingQualityGrading = $state(false);
  let qualityGradingResult = $state<{ success: boolean; message: string } | null>(null);

  // Purge state
  let purgeSnapshot = $state<PurgeSnapshot | null>(null);
  let purgeStartError = $state<string | null>(null);
  const purgeRunning = $derived(purgeSnapshot?.status === 'running');

  // Redirect URI shown on Spotify tab — falls back to a sensible local value.
  const redirectUri = $derived(
    settings?.spotify.oAuthRedirectBaseUrl
      ? `${settings.spotify.oAuthRedirectBaseUrl.replace(/\/$/, '')}/api/spotify/callback`
      : 'http://127.0.0.1:5142/api/spotify/callback'
  );

  $effect(() => {
    if (isMobile.current) return;
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
          pipeline = { ...settingsResp.pipeline };
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
    const response =
      mode === 'post-fingerprint' ? await purgePostFingerprint() : await purgeAll();
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

  async function handleSaveProviders() {
    if (!providers) return;
    isSavingProviders = true;
    providersResult = null;
    try {
      await updateSettings({ providers });
      providersResult = { success: true, message: 'Enrichment sources updated.' };
    } catch (err) {
      providersResult = {
        success: false,
        message: err instanceof Error ? err.message : 'Failed to save providers.'
      };
    } finally {
      isSavingProviders = false;
    }
  }

  async function handleSavePipeline() {
    if (!pipeline) return;
    isSavingPipeline = true;
    pipelineResult = null;
    try {
      await updateSettings({ pipeline });
      pipelineResult = {
        success: true,
        message:
          'Pipeline settings saved. Worker-concurrency changes apply on the next API restart.'
      };
    } catch (err) {
      pipelineResult = {
        success: false,
        message: err instanceof Error ? err.message : 'Failed to save pipeline settings.'
      };
    } finally {
      isSavingPipeline = false;
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
        message: qualityGrading.enabled
          ? 'AI quality grading enabled.'
          : 'AI quality grading disabled.'
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

  // ---- Provider catalog (matches design "Enrichment sources" section) ----
  type ProviderKey = keyof SettingsProvidersView;
  const PROVIDER_CATALOG: { key: ProviderKey; name: string; subtitle: string; dot: string }[] = [
    {
      key: 'acoustId',
      name: 'AcoustID',
      subtitle: 'Fingerprint → MusicBrainz recording match',
      dot: 'oklch(0.68 0.18 30)'
    },
    {
      key: 'spotifyApi',
      name: 'Spotify API',
      subtitle: 'Artist + title catalog search with ISRC verification',
      dot: '#1DB954'
    },
    {
      key: 'deezer',
      name: 'Deezer',
      subtitle: 'Free ISRC-first + artist/title catalog search (no auth)',
      dot: '#a238ff'
    },
    {
      key: 'appleMusic',
      name: 'Apple Music',
      subtitle: 'Free iTunes artist + title catalog search (no auth)',
      dot: '#fa2d48'
    },
    {
      key: 'musicBrainzWeb',
      name: 'MusicBrainz web',
      subtitle: 'Direct ISRC / artist+title lookups against MusicBrainz',
      dot: 'oklch(0.62 0.16 280)'
    },
    {
      key: 'tracker',
      name: 'Community trackers',
      subtitle: 'Juice WRLD unreleased / leak files (best-effort)',
      dot: 'oklch(0.58 0.12 60)'
    }
  ];
</script>

{#if isMobile.current}
  <MobileSettings />
{:else}
<div class="flex-1 overflow-auto">
  <div class="mx-auto max-w-4xl p-6 md:p-8">
    <div class="mb-8 flex items-center gap-3">
      <div class="bg-secondary flex size-10 items-center justify-center rounded-lg">
        <Settings class="text-foreground size-5" />
      </div>
      <div>
        <h1 class="text-2xl font-bold">Settings</h1>
        <p class="text-muted-foreground text-sm">
          Paths, enrichment sources, pipeline tuning, integrations, and data resets.
        </p>
      </div>
    </div>

    {#if isLoading}
      <div class="flex items-center justify-center py-16">
        <Loader2 class="text-muted-foreground size-6 animate-spin" />
      </div>
    {:else}
      <Tabs.Root value="account" class="w-full">
        <Tabs.List class="mb-6 flex w-full flex-wrap justify-start gap-1">
          <Tabs.Trigger value="account" class="gap-1.5"
            ><UserRound class="size-3.5" />Account</Tabs.Trigger
          >
          <Tabs.Trigger value="paths" class="gap-1.5"><Folder class="size-3.5" />Paths</Tabs.Trigger>
          <Tabs.Trigger value="enrichment" class="gap-1.5"
            ><Tag class="size-3.5" />Enrichment</Tabs.Trigger
          >
          {#if user?.role === 'Owner'}
            <Tabs.Trigger value="rules" class="gap-1.5"
              ><Wand2 class="size-3.5" />Match rules</Tabs.Trigger
            >
          {/if}
          <Tabs.Trigger value="pipeline" class="gap-1.5"
            ><SlidersHorizontal class="size-3.5" />Pipeline</Tabs.Trigger
          >
          <Tabs.Trigger value="spotify" class="gap-1.5"
            ><Music class="size-3.5" />Spotify</Tabs.Trigger
          >
          <Tabs.Trigger value="data" class="gap-1.5"
            ><Database class="size-3.5" />Data &amp; resets</Tabs.Trigger
          >
        </Tabs.List>

        <!-- =================== ACCOUNT =================== -->
        <Tabs.Content value="account" class="mt-0">
          <section class="border-border bg-card rounded-xl border">
            <header class="border-border border-b px-6 py-4">
              <h2 class="font-semibold">Account</h2>
              <p class="text-muted-foreground text-xs">
                Signed-in user. Sign in by magic link or passkey — no passwords. Roles control who
                can mutate pipeline state.
              </p>
            </header>

            <div class="space-y-5 p-6">
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
                  You're signed in as the demo account. You can browse the seeded library but
                  mutating actions (scan, enrich, settings PUT, purge) return <span
                    class="bg-secondary rounded px-1 font-mono">403 owner_required</span
                  >.
                </div>
              {/if}

              <div class="border-border flex flex-wrap items-center gap-2 border-t pt-4">
                <Button variant="outline" onclick={() => handleSignOut(false)}>
                  <LogOut class="mr-2 size-4" /> Sign out
                </Button>
                <Button variant="ghost" onclick={() => handleSignOut(true)}>
                  Sign out everywhere
                </Button>
              </div>
            </div>
          </section>

          {#if user?.role === 'Owner'}
            <section class="border-border bg-card mt-6 rounded-xl border">
              <header class="border-border border-b px-6 py-4">
                <h2 class="flex items-center gap-2 font-semibold">
                  <KeyRound class="size-4" /> Passkeys
                </h2>
                <p class="text-muted-foreground text-xs">
                  Sign in with Touch ID, Windows Hello, or a security key — no email needed. Enroll
                  one on each device you use. Keep at least one magic-link-capable email so you can
                  recover access.
                </p>
              </header>

              <div class="space-y-5 p-6">
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
                      <div class="text-muted-foreground px-4 py-3 text-sm">
                        No passkeys enrolled yet.
                      </div>
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
          {/if}
        </Tabs.Content>

        <!-- =================== PATHS =================== -->
        <Tabs.Content value="paths" class="mt-0">
          <section class="border-border bg-card rounded-xl border">
            <header class="border-border border-b px-6 py-4">
              <h2 class="font-semibold">Paths &amp; storage</h2>
              <p class="text-muted-foreground text-xs">
                Where MusicHoarder reads from and writes to. Configured via Aspire AppHost
                parameters (<code class="bg-secondary rounded px-1 py-0.5">source-directory</code> /
                <code class="bg-secondary rounded px-1 py-0.5">destination-directory</code>) — edit
                user-secrets and restart to change.
              </p>
            </header>
            <div class="space-y-5 p-6">
              <div class="space-y-2">
                <Label>Source directory</Label>
                <Input readonly value={settings?.paths.sourceDirectory ?? ''} class="font-mono text-sm" />
                <p class="text-muted-foreground text-xs">
                  Files under this folder are scanned, fingerprinted, and enriched.
                </p>
              </div>

              <div class="space-y-2">
                <Label>Destination directory</Label>
                <Input
                  readonly
                  value={settings?.paths.destinationDirectory ?? ''}
                  class="font-mono text-sm"
                />
                <p class="text-muted-foreground text-xs">
                  Where the organised library is written after enrichment + tag-write.
                </p>
              </div>

              <div class="space-y-2">
                <Label>fpcalc binary</Label>
                <Input readonly value={settings?.paths.fpcalcPath ?? ''} class="font-mono text-sm" />
                <p class="text-muted-foreground text-xs">
                  Chromaprint CLI used for fingerprinting. Must be on
                  <code class="bg-secondary rounded px-1 py-0.5">$PATH</code> or an absolute path.
                </p>
              </div>
            </div>
          </section>
        </Tabs.Content>

        <!-- =================== ENRICHMENT =================== -->
        <Tabs.Content value="enrichment" class="mt-0">
          <section class="border-border bg-card rounded-xl border">
            <header class="border-border border-b px-6 py-4">
              <h2 class="font-semibold">Enrichment sources</h2>
              <p class="text-muted-foreground text-xs">
                Toggle providers off to skip them entirely. New toggles take effect on the next
                enrichment cycle; previously-attempted songs keep their per-provider history.
              </p>
            </header>
            <div class="divide-border divide-y">
              {#each PROVIDER_CATALOG as p (p.key)}
                <div class="flex items-center gap-4 px-6 py-4">
                  <span
                    class="inline-block size-3 rounded-full ring-1 ring-white/60"
                    style="background: {p.dot}"
                  ></span>
                  <div class="flex-1">
                    <div class="text-sm font-medium">{p.name}</div>
                    <div class="text-muted-foreground text-xs">{p.subtitle}</div>
                  </div>
                  <label class="inline-flex cursor-pointer items-center gap-2">
                    <input
                      type="checkbox"
                      class="peer sr-only"
                      checked={providers?.[p.key] ?? false}
                      onchange={(e) => {
                        if (providers) providers = { ...providers, [p.key]: e.currentTarget.checked };
                      }}
                    />
                    <span
                      class="border-input bg-secondary peer-checked:bg-primary relative h-5 w-9 rounded-full border transition-colors after:absolute after:top-0.5 after:left-0.5 after:size-4 after:rounded-full after:bg-white after:shadow after:transition-transform peer-checked:after:translate-x-4"
                    ></span>
                  </label>
                </div>
              {/each}
            </div>

            {#if providersResult}
              <div
                class="mx-6 mb-4 flex items-center gap-2 rounded-lg border px-4 py-2 text-sm {providersResult.success
                  ? 'border-[#1DB954]/50 bg-[#1DB954]/10 text-[#1DB954]'
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

            <div class="border-border flex justify-end gap-2 border-t px-6 py-4">
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

          <section class="border-border bg-card mt-6 rounded-xl border">
            <header class="border-border border-b px-6 py-4">
              <h2 class="font-semibold">AI quality grading</h2>
              <p class="text-muted-foreground text-xs">
                An LLM grades each enrichment result so you can benchmark and debug the algorithm.
                Turn it off to stop the background grading sweep and hide any grading-error
                notifications.
              </p>
            </header>

            <div class="flex items-center gap-4 px-6 py-4">
              <div class="flex-1">
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
              <label class="inline-flex cursor-pointer items-center gap-2">
                <input
                  type="checkbox"
                  class="peer sr-only"
                  checked={qualityGrading?.enabled ?? false}
                  onchange={(e) => {
                    if (qualityGrading)
                      qualityGrading = { ...qualityGrading, enabled: e.currentTarget.checked };
                  }}
                />
                <span
                  class="border-input bg-secondary peer-checked:bg-primary relative h-5 w-9 rounded-full border transition-colors after:absolute after:top-0.5 after:left-0.5 after:size-4 after:rounded-full after:bg-white after:shadow after:transition-transform peer-checked:after:translate-x-4"
                ></span>
              </label>
            </div>

            {#if qualityGradingResult}
              <div
                class="mx-6 mb-4 flex items-center gap-2 rounded-lg border px-4 py-2 text-sm {qualityGradingResult.success
                  ? 'border-[#1DB954]/50 bg-[#1DB954]/10 text-[#1DB954]'
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

            <div class="border-border flex justify-end gap-2 border-t px-6 py-4">
              <Button
                onclick={handleSaveQualityGrading}
                disabled={isSavingQualityGrading || !qualityGrading}
              >
                {#if isSavingQualityGrading}
                  <Loader2 class="mr-2 size-4 animate-spin" />
                {:else}
                  <Save class="mr-2 size-4" />
                {/if}
                Save grading
              </Button>
            </div>
          </section>
        </Tabs.Content>

        <!-- =================== MATCH RULES =================== -->
        <Tabs.Content value="rules" class="mt-0">
          <section class="border-border bg-card rounded-xl border">
            <header class="border-border border-b px-6 py-4">
              <h2 class="flex items-center gap-2 font-semibold">
                <Wand2 class="size-4" />
                {isEditingRule ? 'Edit rule' : 'New rule'}
              </h2>
              <p class="text-muted-foreground text-xs">
                Recognize songs the music databases don't carry (e.g. YouTube channel uploads) by a
                pattern, and rewrite their tags from the captured parts. Use
                <code class="bg-secondary rounded px-1 py-0.5">{'{artist}'}</code>,
                <code class="bg-secondary rounded px-1 py-0.5">{'{title}'}</code>,
                <code class="bg-secondary rounded px-1 py-0.5">{'{album}'}</code>, and
                <code class="bg-secondary rounded px-1 py-0.5">{'{albumartist}'}</code>
                placeholders; literal text (like a channel name) anchors the match. A match is trusted
                and overwrites the existing tags (originals are kept and restored if you re-enrich).
              </p>
            </header>

            <div class="space-y-5 p-6">
              <div class="space-y-2">
                <Label for="rule-name">Name</Label>
                <Input
                  id="rule-name"
                  placeholder="e.g. 101Barz sessions"
                  bind:value={ruleName}
                  oninput={() => (matchRuleError = null)}
                />
              </div>

              <div class="space-y-2">
                <Label for="rule-pattern">Pattern</Label>
                <Input
                  id="rule-pattern"
                  placeholder={'{artist} | {title} | 101Barz'}
                  bind:value={rulePattern}
                  oninput={() => (matchRuleError = null)}
                  class="font-mono text-sm"
                />
              </div>

              <div class="flex flex-wrap gap-4">
                <div class="min-w-48 flex-1 space-y-2">
                  <Label for="rule-source">Match against</Label>
                  <select
                    id="rule-source"
                    bind:value={ruleSourceField}
                    class="border-input bg-background ring-offset-background focus-visible:ring-ring h-10 w-full rounded-md border px-3 py-2 text-sm focus-visible:ring-2 focus-visible:outline-none"
                  >
                    <option value="title">Title tag</option>
                    <option value="filename">File name</option>
                  </select>
                </div>
                <div class="w-32 space-y-2">
                  <Label for="rule-priority">Priority</Label>
                  <Input id="rule-priority" type="number" bind:value={rulePriority} />
                </div>
                <div class="flex flex-col justify-end pb-1">
                  <label class="inline-flex cursor-pointer items-center gap-2">
                    <input type="checkbox" class="peer sr-only" bind:checked={ruleEnabled} />
                    <span
                      class="border-input bg-secondary peer-checked:bg-primary relative h-5 w-9 rounded-full border transition-colors after:absolute after:top-0.5 after:left-0.5 after:size-4 after:rounded-full after:bg-white after:shadow after:transition-transform peer-checked:after:translate-x-4"
                    ></span>
                    <span class="text-sm">Enabled</span>
                  </label>
                </div>
              </div>

              <div class="flex flex-wrap gap-4">
                <div class="min-w-48 flex-1 space-y-2">
                  <Label for="rule-album">Set album to <span class="text-muted-foreground font-normal">(optional)</span></Label>
                  <Input id="rule-album" placeholder="101Barz sessies" bind:value={ruleAlbumOverride} />
                </div>
                <div class="min-w-48 flex-1 space-y-2">
                  <Label for="rule-album-artist">Set album artist to <span class="text-muted-foreground font-normal">(optional)</span></Label>
                  <Input id="rule-album-artist" placeholder="101Barz" bind:value={ruleAlbumArtistOverride} />
                </div>
              </div>
              <p class="text-muted-foreground -mt-2 text-xs">
                Constants applied on top of the captured fields — leave blank to keep each song's own.
                Set both to group many different track artists into one compilation album.
              </p>

              <!-- Live preview -->
              <div class="border-border bg-secondary/30 space-y-3 rounded-lg border p-4">
                <Label for="rule-sample">Test against a sample</Label>
                <Input
                  id="rule-sample"
                  placeholder="Yung Nnelg | Wintersessie 2020 | 101Barz"
                  bind:value={testSample}
                  class="font-mono text-sm"
                />
                {#if testResult}
                  {#if !testResult.valid}
                    <div class="text-destructive flex items-start gap-2 text-xs">
                      <AlertCircle class="mt-0.5 size-3.5 shrink-0" />
                      <span>{testResult.error}</span>
                    </div>
                  {:else if !testResult.matched}
                    <div class="text-muted-foreground text-xs">No match for this sample.</div>
                  {:else}
                    <div class="flex flex-wrap gap-2">
                      {#each [['Artist', testResult.extracted?.artist ?? null, false], ['Title', testResult.extracted?.title ?? null, false], ['Album', ruleAlbumOverride.trim() || (testResult.extracted?.album ?? null), !!ruleAlbumOverride.trim()], ['Album artist', ruleAlbumArtistOverride.trim() || (testResult.extracted?.albumArtist ?? null), !!ruleAlbumArtistOverride.trim()]] as [label, value, isOverride] (label)}
                        {#if value}
                          <Badge variant="secondary" class="gap-1">
                            <span class="text-muted-foreground">{label}:</span>
                            {value}
                            {#if isOverride}<span class="text-muted-foreground">(set)</span>{/if}
                          </Badge>
                        {/if}
                      {/each}
                    </div>
                  {/if}
                {:else}
                  <p class="text-muted-foreground text-xs">
                    Type a pattern and a sample to preview the captured fields.
                  </p>
                {/if}
              </div>

              {#if matchRuleError}
                <div
                  class="border-destructive/50 bg-destructive/10 text-destructive flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
                >
                  <AlertCircle class="mt-0.5 size-4 shrink-0" />
                  <span>{matchRuleError}</span>
                </div>
              {/if}
            </div>

            <div class="border-border flex justify-end gap-2 border-t px-6 py-4">
              {#if isEditingRule}
                <Button variant="ghost" onclick={resetRuleForm} disabled={isSavingRule}>
                  <X class="mr-2 size-4" /> Cancel
                </Button>
              {/if}
              <Button onclick={handleSaveRule} disabled={isSavingRule || !ruleName.trim() || !rulePattern.trim()}>
                {#if isSavingRule}
                  <Loader2 class="mr-2 size-4 animate-spin" />
                {:else if isEditingRule}
                  <Save class="mr-2 size-4" />
                {:else}
                  <Plus class="mr-2 size-4" />
                {/if}
                {isEditingRule ? 'Save rule' : 'Add rule'}
              </Button>
            </div>
          </section>

          <section class="border-border bg-card mt-6 rounded-xl border">
            <header class="border-border border-b px-6 py-4">
              <h2 class="font-semibold">Your rules</h2>
              <p class="text-muted-foreground text-xs">
                Applied in priority order (lowest first); the first rule that matches a song wins.
                Changes take effect on the next enrichment cycle.
              </p>
            </header>

            <div class="divide-border divide-y">
              {#if matchRulesLoading}
                <div class="text-muted-foreground px-6 py-4 text-sm">Loading…</div>
              {:else if matchRules.length === 0}
                <div class="text-muted-foreground px-6 py-6 text-sm">
                  No rules yet. Add one above to start matching.
                </div>
              {:else}
                {#each matchRules as rule (rule.id)}
                  <div class="flex items-center gap-4 px-6 py-4">
                    <div class="min-w-0 flex-1">
                      <div class="flex items-center gap-2">
                        <span class="truncate text-sm font-medium">{rule.name}</span>
                        <Badge variant="outline" class="text-muted-foreground shrink-0 text-[10px]">
                          {rule.sourceField === 'filename' ? 'file name' : 'title'}
                        </Badge>
                        <span class="text-muted-foreground shrink-0 text-xs">#{rule.priority}</span>
                      </div>
                      <div class="text-muted-foreground truncate font-mono text-xs">{rule.pattern}</div>
                      {#if rule.albumOverride || rule.albumArtistOverride}
                        <div class="text-muted-foreground mt-0.5 truncate text-xs">
                          → {#if rule.albumArtistOverride}album artist <span class="text-foreground">{rule.albumArtistOverride}</span>{/if}{#if rule.albumOverride && rule.albumArtistOverride} · {/if}{#if rule.albumOverride}album <span class="text-foreground">{rule.albumOverride}</span>{/if}
                        </div>
                      {/if}
                    </div>
                    <label class="inline-flex cursor-pointer items-center gap-2" title="Enabled">
                      <input
                        type="checkbox"
                        class="peer sr-only"
                        checked={rule.enabled}
                        onchange={() => handleToggleRule(rule)}
                      />
                      <span
                        class="border-input bg-secondary peer-checked:bg-primary relative h-5 w-9 rounded-full border transition-colors after:absolute after:top-0.5 after:left-0.5 after:size-4 after:rounded-full after:bg-white after:shadow after:transition-transform peer-checked:after:translate-x-4"
                      ></span>
                    </label>
                    <Button variant="ghost" size="icon" onclick={() => startEditRule(rule)} aria-label="Edit rule">
                      <Pencil class="size-4" />
                    </Button>
                    <Button variant="ghost" size="icon" onclick={() => handleDeleteRule(rule.id)} aria-label="Delete rule">
                      <Trash2 class="size-4" />
                    </Button>
                  </div>
                {/each}
              {/if}
            </div>
          </section>
        </Tabs.Content>

        <!-- =================== PIPELINE =================== -->
        <Tabs.Content value="pipeline" class="mt-0">
          <section class="border-border bg-card rounded-xl border">
            <header class="border-border border-b px-6 py-4">
              <h2 class="font-semibold">Pipeline tuning</h2>
              <p class="text-muted-foreground text-xs">
                Confidence thresholds determine when a Spotify or AcoustID match is considered
                "Matched" instead of "NeedsReview". Worker-concurrency changes are persisted but
                apply only after the next API restart (SemaphoreSlim limits are set at startup).
              </p>
            </header>

            <div class="space-y-6 p-6">
              <div class="space-y-3">
                <div class="flex items-center justify-between">
                  <Label>Spotify matched threshold</Label>
                  <span class="text-muted-foreground font-mono text-sm"
                    >{pipeline?.spotifyApiMatchedThreshold.toFixed(2) ?? '—'}</span
                  >
                </div>
                <Slider
                  type="single"
                  min={0}
                  max={1}
                  step={0.01}
                  value={pipeline?.spotifyApiMatchedThreshold ?? 0.85}
                  onValueChange={(v) => {
                    if (pipeline && typeof v === 'number')
                      pipeline = { ...pipeline, spotifyApiMatchedThreshold: v };
                  }}
                />
                <p class="text-muted-foreground text-xs">
                  Songs below this score fall into the manual-review queue.
                </p>
              </div>

              <div class="space-y-3">
                <div class="flex items-center justify-between">
                  <Label>AcoustID score threshold</Label>
                  <span class="text-muted-foreground font-mono text-sm"
                    >{pipeline?.acoustIdScoreThreshold.toFixed(2) ?? '—'}</span
                  >
                </div>
                <Slider
                  type="single"
                  min={0}
                  max={1}
                  step={0.01}
                  value={pipeline?.acoustIdScoreThreshold ?? 0.85}
                  onValueChange={(v) => {
                    if (pipeline && typeof v === 'number')
                      pipeline = { ...pipeline, acoustIdScoreThreshold: v };
                  }}
                />
                <p class="text-muted-foreground text-xs">
                  Minimum AcoustID match score (0..1) to accept the result.
                </p>
              </div>

              <div class="space-y-3">
                <div class="flex items-center justify-between">
                  <Label>Enrichment worker concurrency</Label>
                  <span class="text-muted-foreground font-mono text-sm"
                    >{pipeline?.enrichmentWorkerConcurrency ?? '—'} workers</span
                  >
                </div>
                <Slider
                  type="single"
                  min={1}
                  max={16}
                  step={1}
                  value={pipeline?.enrichmentWorkerConcurrency ?? 2}
                  onValueChange={(v) => {
                    if (pipeline && typeof v === 'number')
                      pipeline = { ...pipeline, enrichmentWorkerConcurrency: v };
                  }}
                />
              </div>

              <div class="space-y-3">
                <div class="flex items-center justify-between">
                  <Label>Library-builder worker concurrency</Label>
                  <span class="text-muted-foreground font-mono text-sm"
                    >{pipeline?.libraryBuilderWorkerConcurrency ?? '—'} workers</span
                  >
                </div>
                <Slider
                  type="single"
                  min={1}
                  max={16}
                  step={1}
                  value={pipeline?.libraryBuilderWorkerConcurrency ?? 2}
                  onValueChange={(v) => {
                    if (pipeline && typeof v === 'number')
                      pipeline = { ...pipeline, libraryBuilderWorkerConcurrency: v };
                  }}
                />
              </div>
            </div>

            {#if pipelineResult}
              <div
                class="mx-6 mb-4 flex items-center gap-2 rounded-lg border px-4 py-2 text-sm {pipelineResult.success
                  ? 'border-[#1DB954]/50 bg-[#1DB954]/10 text-[#1DB954]'
                  : 'border-destructive/50 bg-destructive/10 text-destructive'}"
              >
                {#if pipelineResult.success}
                  <CheckCircle2 class="size-4 shrink-0" />
                {:else}
                  <AlertCircle class="size-4 shrink-0" />
                {/if}
                {pipelineResult.message}
              </div>
            {/if}

            <div class="border-border flex justify-end gap-2 border-t px-6 py-4">
              <Button onclick={handleSavePipeline} disabled={isSavingPipeline || !pipeline}>
                {#if isSavingPipeline}
                  <Loader2 class="mr-2 size-4 animate-spin" />
                {:else}
                  <Save class="mr-2 size-4" />
                {/if}
                Save pipeline
              </Button>
            </div>
          </section>
        </Tabs.Content>

        <!-- =================== SPOTIFY =================== -->
        <Tabs.Content value="spotify" class="mt-0">
          <section class="border-border bg-card rounded-xl border">
            <div class="border-border flex items-center gap-3 border-b px-6 py-4">
              <div class="flex size-8 items-center justify-center rounded-lg bg-[#1DB954]/10">
                <Music class="size-4 text-[#1DB954]" />
              </div>
              <div class="min-w-0 flex-1">
                <h2 class="font-semibold">Spotify Integration</h2>
                <p class="text-muted-foreground text-xs">
                  Connect your Spotify account to browse playlists and liked songs.
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
                      <li>Create a new app (or use an existing one).</li>
                      <li>
                        Add the redirect URI below in <em>Settings → Redirect URIs</em>. Spotify
                        does not allow <code class="bg-secondary rounded px-1 py-0.5">localhost</code> —
                        use a loopback IP like <code class="bg-secondary rounded px-1 py-0.5">127.0.0.1</code>.
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
                <div class="flex gap-2">
                  <Input
                    id="client-secret"
                    type={showSecret ? 'text' : 'password'}
                    placeholder={savedCredentials?.hasClientSecret
                      ? '••••••••••••••••'
                      : 'Enter your Spotify Client Secret'}
                    bind:value={clientSecret}
                    oninput={() => (saveResult = null)}
                    class="font-mono text-sm"
                  />
                  <Button
                    type="button"
                    variant="outline"
                    onclick={() => (showSecret = !showSecret)}
                  >
                    {showSecret ? 'Hide' : 'Show'}
                  </Button>
                </div>
              </div>

              <div class="space-y-2">
                <Label>Redirect URI</Label>
                <div class="flex gap-2">
                  <Input readonly value={redirectUri} class="font-mono text-sm" />
                  <Button type="button" variant="outline" onclick={copyRedirectUri}>
                    <Copy class="mr-2 size-4" /> Copy
                  </Button>
                </div>
                <p class="text-muted-foreground text-xs">
                  Add this exact URI to your Spotify app's Redirect URIs list. Must match
                  <code class="bg-secondary rounded px-1 py-0.5">Spotify:OAuthRedirectBaseUrl</code>
                  on the API.
                </p>
              </div>

              <div class="space-y-2">
                <Label>Scopes requested</Label>
                <div class="flex flex-wrap gap-2">
                  {#each settings?.spotify.scopes ?? [] as scope (scope)}
                    <Badge variant="secondary" class="font-mono text-xs">{scope}</Badge>
                  {/each}
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
                onclick={handleSaveCredentials}
                disabled={isSaving || !clientId.trim() || !clientSecret.trim()}
                class="w-full sm:w-auto"
              >
                {#if isSaving}
                  <Loader2 class="mr-2 size-4 animate-spin" />
                {:else}
                  <Save class="mr-2 size-4" />
                {/if}
                Save credentials
              </Button>
            </div>
          </section>
        </Tabs.Content>

        <!-- =================== DATA & RESETS =================== -->
        <Tabs.Content value="data" class="mt-0">
          <section class="border-destructive/40 bg-card rounded-xl border">
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
                        This clears every song's enrichment, lyrics, duplicate flags, and
                        library-build state, and deletes files copied to the destination folder.
                        Fingerprints and scan data are preserved so the next run skips straight to
                        enrichment. This runs in the background — you can navigate away and the
                        progress will be here when you come back. This cannot be undone.
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
                    Removes every song, provider attempt, and cached Spotify match from the
                    database, and deletes any files copied to the destination folder. Source files
                    are not touched. The next run re-scans and re-fingerprints from source.
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
                        This deletes every song record, provider attempt, and cached Spotify match,
                        and removes files that were copied to the destination folder. Source files
                        are not affected. This runs in the background — you can navigate away and
                        the progress will be here when you come back. This cannot be undone.
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
        </Tabs.Content>
      </Tabs.Root>
    {/if}
  </div>
</div>
{/if}
