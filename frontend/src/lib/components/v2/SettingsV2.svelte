<script lang="ts">
  import { invalidateAll } from '$app/navigation';
  import { replaceUrl } from '$lib/navigation/replace-url';
  import { page } from '$app/state';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import { Badge } from '$lib/components/ui/badge';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import PurgeStatusBanner, {
    purgeAnnouncement
  } from '$lib/components/settings/PurgeStatusBanner.svelte';
  import MusicVideoAuditCard from '$lib/components/settings/MusicVideoAuditCard.svelte';
  import StagedSourceReleaseBanner, {
    releaseAnnouncement
  } from '$lib/components/settings/StagedSourceReleaseBanner.svelte';
  import PairDeviceCard from '$lib/components/settings/PairDeviceCard.svelte';
  import InstallAppCard from '$lib/components/settings/InstallAppCard.svelte';
  import PeopleCard from '$lib/components/settings/PeopleCard.svelte';
  import SwitchRow from '$lib/components/settings/SwitchRow.svelte';
  import FieldRow from '$lib/components/settings/FieldRow.svelte';
  import StatusLine from '$lib/components/settings/StatusLine.svelte';
  import { createOptimisticCommitter } from '$lib/components/settings/optimistic';
  import { COPY_FAILED_MESSAGE, copyText } from '$lib/components/settings/copy-text';
  import {
    resolveSettingsView,
    sectionHref,
    visibleSections,
    type SectionId
  } from '$lib/components/settings/sections';
  import {
    defaultPasskeyName,
    passkeyPlatform,
    passkeyUnlockPhrase,
    type PasskeyPlatform
  } from '$lib/components/settings/passkey-copy';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { avatar } from '$lib/components/v2/AccountPanel.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
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
    fetchStagedSourcePreview,
    fetchStagedSourceStatus,
    startStagedSourceRelease,
    registerPasskey,
    listPasskeys,
    listAccounts,
    deletePasskey,
    updateDisplayName,
    sync,
    soulseek,
    type AccountView,
    type PasskeyView,
    type SyncStatus,
    type SoulseekStatus,
    type PurgeMode,
    type PurgeSnapshot,
    type StagedSourceReleasePreview,
    type StagedSourceReleaseSnapshot,
    type SettingsResponse,
    type SettingsProvidersView,
    type SettingsQualityGradingView,
    type SpotifyCredentialsResponse,
    type SpotifyStatusResponse
  } from '$lib/api-client';
  import { signOutAndReset } from '$lib/auth/sign-out';
  import { switchAccountAndReload } from '$lib/auth/switch-account';
  import { isAdmin, isDemo, roleLabel } from '$lib/auth/capabilities';
  import { formatDate, formatFileSize } from '$lib/formatters';
  import { toast } from 'svelte-sonner';
  import {
    Check,
    CircleAlert,
    CircleArrowUp,
    CircleCheck,
    Copy,
    FolderInput,
    FolderOutput,
    KeyRound,
    Loader2,
    Plus,
    Radar,
    Trash2,
    Users
  } from '@lucide/svelte';

  const formatBytes = (bytes: number) => (bytes > 0 ? formatFileSize(bytes) : '0 B');

  // ── sections ─────────────────────────────────────────────────────────────────
  // Which section shows is a pure function of the URL (sections.ts): on a phone a section is a
  // page pushed over the Settings root — a row tap is a real navigation to ?tab=X, so the nav
  // bar's Back takes it away again — and on a desktop the pane list beside it rewrites ?tab= in
  // place. Nothing about the section lives in component state, so Back, a reload and every deep
  // link (?tab=people|sources|account|updates) land in the same place.
  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);
  const user = $derived(page.data.user);
  const admin = $derived(isAdmin(user));
  const demo = $derived(isDemo(user));
  const version = $derived(page.data.appVersion as string | null | undefined);

  const visible = $derived(visibleSections(user));
  // Two panes need room for both. Below this width the section column would be narrower than a
  // phone's (768–1023px with the app sidebar open left it about 330px), so the page stacks
  // instead — the root list, each section pushed over it — as iPadOS Settings does in a narrow
  // window. Measured on the page itself, so a collapsed app sidebar gets the panes back.
  // (Before the first measurement the breakpoint decides; it lands before the first paint.)
  const PANES_MIN_WIDTH = 720;
  let pageWidth = $state(0);
  const stacked = $derived(compact || (pageWidth > 0 && pageWidth < PANES_MIN_WIDTH));
  const view = $derived(resolveSettingsView(page.url, user, stacked));
  const section = $derived(view.section);
  // One person's page under People (?tab=people&person=<id>), pushed over the People list.
  const person = $derived(view.person);
  let personTitle = $state<string | null>(null);
  const canSee = (id: SectionId) => visible.some((s) => s.id === id);
  const labelOf = (id: SectionId) => visible.find((s) => s.id === id)?.label ?? 'Settings';

  // A URL naming a section this person cannot see is corrected in place (a replace, so Back
  // does not return to it): to the root on a phone, the first section on a desktop.
  $effect(() => {
    const target = view.redirect;
    if (target) void replaceUrl(target, { noScroll: false, keepFocus: false });
  });

  // A member has one section, so the desktop gets no pane list for them (a list of one row is
  // not navigation) — and the page keeps the name they reached it by.
  const showPaneList = $derived(!stacked && visible.length > 1);
  const barTitle = $derived(
    person
      ? (personTitle ?? 'Person')
      : visible.length === 1 || section === null
        ? 'Settings'
        : labelOf(section)
  );
  // A person's page names its list at every width (a desktop bar otherwise has no Back, and a
  // phone that deep-linked here would go back to the Settings root, skipping People). A stacked
  // desktop section names the root it was pushed from: a desktop bar has no Back of its own.
  const barBack = $derived(
    person
      ? { label: 'People', href: sectionHref('people') }
      : !compact && stacked && section !== null && visible.length > 1
        ? { label: 'Settings', href: '/settings' }
        : undefined
  );
  // A section or a person names its page, so "Back to Playback" (not "Back to Settings") leads
  // back from anything pushed from it, and the browser-tab title the route announcer reads says
  // where this is. The root and a member's single section keep the nav's own "Settings", and a
  // person waits for their name. titleOf is read first so the effect runs again once the
  // navigation is recorded — until then the entry does not exist and setTitle has nothing to patch.
  const ownTitle = $derived(
    person ? personTitle : section !== null && visible.length > 1 ? labelOf(section) : null
  );
  $effect(() => {
    if (ownTitle && tabMemory.titleOf(page.url) !== ownTitle) tabMemory.setTitle(page.url, ownTitle);
  });

  // Account opens on its own header (avatar, name, email), which is the page's visual title, so
  // the bar keeps an inline title rather than stacking a large one above it — as iOS does for
  // the Apple Account page. A person's page is titled by a name or an email, which a large title
  // would wrap or cut.
  const largeTitle = $derived(section !== 'account' && !person);

  // Desktop pane list: a selection replaces the URL rather than pushing, like picking a pane
  // in System Settings. Modified clicks still open the link in a new tab.
  function selectPane(event: MouseEvent, id: SectionId) {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }
    event.preventDefault();
    // Compared by URL, not by section: from a person's page, People's own row goes back to the
    // People list.
    const target = sectionHref(id);
    if (page.url.pathname + page.url.search !== target) void replaceUrl(target);
  }

  // The selected pane is marked the way the app sidebar beside it marks the current page — a grey
  // fill with the label and icon in the tint — so the window shows one selection style, not two.
  // The fill is a step stronger than the sidebar's (--secondary, not --sidebar-accent): on a white
  // card GroupedList's own `selected` grey all but vanishes (about 1.13:1 in light). No hairline
  // runs through or against the selected row.
  const PANE_ROW = '[&:has(+[data-selected])]:after:hidden';
  const PANE_SELECTED =
    'bg-secondary hover:bg-secondary active:bg-secondary after:hidden [&_.text-foreground]:text-primary [&_.text-foreground]:font-medium';
  const PANE_TILE_SELECTED = 'text-primary';

  // ── account ────────────────────────────────────────────────────────────────────
  const displayName = $derived(user ? user.displayName?.trim() || user.email : '');

  async function handleSignOut(allSessions = false) {
    await signOutAndReset(allSessions);
  }
  let signOutEverywhereOpen = $state(false);

  // ── accounts on this browser (account switcher) ────────────────────────────────
  let browserAccounts = $state<AccountView[] | null>(null);
  let accountsFailed = $state(false);
  let accountSwitchingTo = $state<string | null>(null);
  // Not reactive on purpose: it only guards against a second request while one is in flight.
  let accountsInFlight = false;

  function loadBrowserAccounts() {
    if (accountsInFlight) return;
    accountsInFlight = true;
    void (async () => {
      try {
        browserAccounts = await listAccounts();
      } catch {
        accountsFailed = true;
      } finally {
        accountsInFlight = false;
      }
    })();
  }

  // Fetched lazily, the first time the Account section shows — and again after "Try again",
  // which only clears the failure so this is the one place a fetch starts.
  $effect(() => {
    if (section !== 'account' || browserAccounts !== null || accountsFailed) return;
    loadBrowserAccounts();
  });

  async function handleSwitchAccount(account: AccountView) {
    if (account.isActive || accountSwitchingTo) return;
    accountSwitchingTo = account.userId;
    try {
      await switchAccountAndReload(account.userId);
    } catch {
      accountSwitchingTo = null;
    }
  }

  // ── account name (admin-only; demo/member writes are rejected server-side) ─────
  let isEditingName = $state(false);
  let nameDraft = $state('');
  let isSavingName = $state(false);
  let nameError = $state<string | null>(null);

  function startEditName() {
    nameDraft = user?.displayName ?? '';
    nameError = null;
    isEditingName = true;
  }

  async function handleSaveName() {
    if (isSavingName) return;
    isSavingName = true;
    nameError = null;
    try {
      await updateDisplayName(nameDraft.trim() || null);
      isEditingName = false;
      // Session user lives in page.data — re-run the layout load so the account button and
      // this page pick up the new name.
      await invalidateAll();
    } catch (err) {
      nameError = err instanceof Error ? err.message : 'Could not update the account name.';
    } finally {
      isSavingName = false;
    }
  }

  // Focus the field when editing starts (a tap on the Name row), as iOS does when a cell turns
  // into a text field.
  function focusOnMount(node: HTMLInputElement) {
    node.focus();
    node.select();
  }

  // ── passkeys (admin + member, each for their own account) ──────────────────────
  // A member enrols too: the ceremony endpoints act on the caller's own account and never take a
  // user id from the request, and without a passkey of their own the Android client's passkey
  // sign-in would be admin-only. The demo stays out — its credentials are shared by every visitor.
  let passkeySupported = $state(false);
  let platform = $state<PasskeyPlatform>('other');
  let passkeys = $state<PasskeyView[]>([]);
  let passkeysLoading = $state(false);
  let newPasskeyName = $state('');
  let isAddingPasskey = $state(false);
  let passkeyError = $state<string | null>(null);
  // The passkey whose Remove confirm is open: one confirm per row, so the rows share this id.
  let removingPasskeyId = $state<string | null>(null);

  const canUsePasskeys = $derived(!demo);

  $effect(() => {
    passkeySupported = isPasskeySupported();
    platform = passkeyPlatform(navigator.userAgent ?? '', navigator.maxTouchPoints ?? 0);
  });

  $effect(() => {
    if (!canUsePasskeys) return;
    let cancelled = false;
    void (async () => {
      passkeysLoading = true;
      try {
        const list = await listPasskeys();
        if (!cancelled) passkeys = list;
      } catch {
        // non-fatal; the section just shows none
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
      // Left empty, the passkey is named after the device it was made on ("iPhone"), which is
      // what the list needs to tell two of them apart later.
      const created = await registerPasskey(newPasskeyName.trim() || defaultPasskeyName(platform));
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
      // The row just vanishes otherwise — say it was the removal, not a glitch.
      toast.success('Passkey removed');
    } catch (err) {
      passkeyError = err instanceof Error ? err.message : 'Could not remove passkey.';
    }
  }

  // ── soulseek & sync status (admin-only, read-only) ────────────────────────────
  let soulseekStatus = $state<SoulseekStatus | null>(null);
  let syncStatus = $state<SyncStatus | null>(null);
  let soulseekSyncLoaded = $state(false);

  $effect(() => {
    if (!admin) return;
    let cancelled = false;
    void (async () => {
      const [slsk, syncResp] = await Promise.all([
        soulseek.getStatus().catch(() => null),
        sync.getStatus().catch(() => null)
      ]);
      if (cancelled) return;
      soulseekStatus = slsk;
      syncStatus = syncResp;
      soulseekSyncLoaded = true;
    })();
    return () => {
      cancelled = true;
    };
  });

  const soulseekValue = $derived.by(() => {
    if (!soulseekSyncLoaded) return 'Loading…';
    if (!soulseekStatus) return 'Unavailable';
    if (!soulseekStatus.configured) return 'Not configured';
    return soulseekStatus.connected ? 'Connected' : 'Disconnected';
  });
  const syncValue = $derived.by(() => {
    if (!soulseekSyncLoaded) return 'Loading…';
    return syncStatus ? syncStatus.mode : 'Unavailable';
  });

  // ── settings + spotify state ───────────────────────────────────────────────────
  let isLoading = $state(true);
  let settings = $state<SettingsResponse | null>(null);
  // The settings document could not be read: Sources, Providers and Library output say so (with
  // a retry) rather than showing "Not set" paths and switches that are disabled for no reason.
  let settingsFailed = $state(false);
  let loadAttempt = $state(0);
  let providers = $state<SettingsProvidersView | null>(null);
  let qualityGrading = $state<SettingsQualityGradingView | null>(null);

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
  let disconnectOpen = $state(false);
  let redirectCopied = $state(false);

  // Purge state
  let purgeSnapshot = $state<PurgeSnapshot | null>(null);
  let purgeStartError = $state<string | null>(null);
  // The start request is out: both rows stay unavailable until it answers, so a second tap
  // cannot send a second purge.
  let purgeStarting = $state(false);
  const purgeRunning = $derived(purgeSnapshot?.status === 'running');
  let resetDialogOpen = $state(false);

  // Staged-source release: downloads are copied into the library; once verified, the staged copy is
  // dead weight. Toggle = hourly sweep; "Release now" = one immediate run with progress.
  let stagedPreview = $state<StagedSourceReleasePreview | null>(null);
  let stagedSnapshot = $state<StagedSourceReleaseSnapshot | null>(null);
  let stagedToggleBusy = $state(false);
  let stagedToggleError = $state<string | null>(null);
  let stagedStartError = $state<string | null>(null);
  let releaseDialogOpen = $state(false);
  let releaseStarting = $state(false);
  const stagedRunning = $derived(stagedSnapshot?.status === 'running');
  const releaseDisabled = $derived(
    stagedRunning ||
      releaseStarting ||
      purgeRunning ||
      !stagedPreview ||
      !!stagedPreview.unavailableReason ||
      stagedPreview.eligible === 0
  );

  async function refreshStagedPreview() {
    try {
      stagedPreview = await fetchStagedSourcePreview();
    } catch {
      // leave the last preview in place
    }
  }

  async function onToggleReleaseStagedSources(next: boolean) {
    if (!settings) return;
    stagedToggleBusy = true;
    stagedToggleError = null;
    const setReleaseStaged = (value: boolean) => {
      if (settings) {
        settings = {
          ...settings,
          downloads: { ...settings.downloads, releaseStagedSources: value }
        };
      }
    };
    const err = await commitSetting('releaseStagedSources', {
      read: () => settings?.downloads.releaseStagedSources ?? false,
      write: setReleaseStaged,
      next,
      save: async () => {
        await inOrder(() => updateSettings({ downloads: { releaseStagedSources: next } }));
      }
    });
    if (err) {
      stagedToggleError = err instanceof Error ? err.message : 'Could not change that setting.';
    }
    stagedToggleBusy = false;
  }

  async function handleReleaseStagedSources() {
    if (releaseStarting) return;
    stagedStartError = null;
    releaseStarting = true;
    const response = await startStagedSourceRelease()
      .catch(() => ({ ok: false as const, message: 'Couldn’t reach the server. Try again.' }))
      .finally(() => (releaseStarting = false));
    if (!response.ok) {
      stagedStartError = response.message;
      return;
    }
    stagedSnapshot = {
      status: 'running',
      mode: 'manual',
      jobId: response.jobId,
      startedAt: new Date().toISOString(),
      completedAt: null,
      candidates: 0,
      released: 0,
      alreadyMissing: 0,
      skippedVerification: 0,
      raced: 0,
      failed: 0,
      bytesReclaimed: 0,
      error: null
    };
  }

  // "Purge all data" is the most destructive action in the app — it requires an explicit typed
  // acknowledgment (not just a click-through Cancel/Confirm) before the dialog's action enables.
  let purgeAllDialogOpen = $state(false);
  let purgeAllConfirmText = $state('');
  const PURGE_ALL_CONFIRM_WORD = 'DELETE';
  const purgeAllConfirmed = $derived(
    purgeAllConfirmText.trim().toUpperCase() === PURGE_ALL_CONFIRM_WORD
  );
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
    // Spotify creds, purge state, and the settings document are all instance configuration —
    // a member's Settings is just their own account, so skip the fetches (the demo's would be
    // refused; its sections say so rather than showing blanks). Runs again on "Try again".
    void loadAttempt;
    if (!admin) {
      isLoading = false;
      return;
    }
    let cancelled = false;
    void (async () => {
      isLoading = true;
      try {
        const [creds, status, purge, settingsResp, staged] = await Promise.all([
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
          fetchSettings().catch(() => null),
          fetchStagedSourceStatus().catch(() => null)
        ]);
        if (cancelled) return;
        savedCredentials = creds;
        spotifyStatus = status;
        if (creds.clientId) clientId = creds.clientId;
        if (purge) purgeSnapshot = purge;
        if (staged) stagedSnapshot = staged;
        settingsFailed = settingsResp === null;
        if (settingsResp) {
          settings = settingsResp;
          providers = { ...settingsResp.providers };
          qualityGrading = { ...settingsResp.qualityGrading };
          if (settingsResp.downloads.enabled) void refreshStagedPreview();
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

  // Poll the staged-source release while running; refresh the preview once it settles.
  $effect(() => {
    if (stagedSnapshot?.status !== 'running') return;
    let cancelled = false;
    const tick = async () => {
      try {
        const snap = await fetchStagedSourceStatus();
        if (cancelled) return;
        stagedSnapshot = snap;
        if (snap.status !== 'running') void refreshStagedPreview();
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
    if (purgeStarting) return;
    purgeStartError = null;
    purgeStarting = true;
    const response = await (mode === 'post-fingerprint' ? purgePostFingerprint() : purgeAll())
      .catch(() => ({ ok: false as const, message: 'Couldn’t reach the server. Try again.' }))
      .finally(() => (purgeStarting = false));
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
      saveResult = { success: true, message: 'Spotify credentials saved.' };
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

  const spotifyValue = $derived(
    spotifyStatus?.connected
      ? 'Connected'
      : savedCredentials?.hasClientSecret
        ? 'Credentials set'
        : demo
          ? 'Hidden in the demo'
          : 'Not configured'
  );

  // ── switches apply as they are flipped ─────────────────────────────────────────
  // Every switch in Settings commits on change, iOS-style: providers and AI grading used to flip
  // locally and wait for a separate Save button, while the staging and People switches applied at
  // once — so a phone user who flipped a provider and navigated away lost the change silently.
  // The switch moves at once (applyOptimistically); a spinner shows while the write is in flight;
  // a refusal puts it back and says why under the section. Writes are chained so two quick flips
  // land in the order they were made.
  let writeQueue: Promise<unknown> = Promise.resolve();
  function inOrder(write: () => Promise<unknown>): Promise<unknown> {
    const run = writeQueue.then(write, write);
    writeQueue = run.catch(() => {});
    return run;
  }
  // Remembers what the server last confirmed per switch, so two quick flips that both fail end
  // on the server's value rather than the first flip's (optimistic.ts).
  const commitSetting = createOptimisticCommitter<boolean>();

  type ProviderKey = keyof SettingsProvidersView;
  // Writes in flight per switch (a count: a second flip must not clear the first one's spinner).
  let providersSaving = $state<Partial<Record<ProviderKey, number>>>({});
  let providersError = $state<string | null>(null);

  async function toggleProvider(key: ProviderKey, value: boolean) {
    if (!providers) return;
    providersError = null;
    providersSaving = { ...providersSaving, [key]: (providersSaving[key] ?? 0) + 1 };
    const err = await commitSetting(`provider:${key}`, {
      read: () => providers?.[key] ?? false,
      write: (v) => {
        if (providers) providers = { ...providers, [key]: v };
      },
      next: value,
      save: async () => {
        await inOrder(() => updateSettings({ providers: { [key]: value } }));
      }
    });
    if (err) providersError = err instanceof Error ? err.message : 'Failed to save providers.';
    providersSaving = { ...providersSaving, [key]: (providersSaving[key] ?? 1) - 1 };
  }

  let gradingSaving = $state(0);
  let gradingError = $state<string | null>(null);

  async function toggleQualityGrading(value: boolean) {
    if (!qualityGrading) return;
    gradingError = null;
    gradingSaving += 1;
    const err = await commitSetting('qualityGrading', {
      read: () => qualityGrading?.enabled ?? false,
      write: (v) => {
        if (qualityGrading) qualityGrading = { ...qualityGrading, enabled: v };
      },
      next: value,
      save: async () => {
        await inOrder(() => updateSettings({ qualityGrading: { enabled: value } }));
      }
    });
    if (err) {
      gradingError = err instanceof Error ? err.message : 'Failed to save the AI grading setting.';
    }
    gradingSaving -= 1;
  }

  // A failed copy says so (plain-http LAN hosts have no async clipboard); the text is selectable.
  async function copyRedirectUri() {
    if (!(await copyText(redirectUri))) {
      toast.error(COPY_FAILED_MESSAGE);
      return;
    }
    redirectCopied = true;
    setTimeout(() => (redirectCopied = false), 2000);
  }

  // ── updates ──────────────────────────────────────────────────────────────────
  const UPDATE_CMD = 'docker compose pull && docker compose up -d';
  let updateCmdCopied = $state(false);

  async function copyUpdateCommand() {
    if (!(await copyText(UPDATE_CMD))) {
      toast.error(COPY_FAILED_MESSAGE);
      return;
    }
    updateCmdCopied = true;
    setTimeout(() => (updateCmdCopied = false), 2000);
  }

  // ── provider catalog (real provider keys) ───────────────────────────────────────
  // No leading dot: grey for all but Spotify's brand green, it carried nothing and read as a
  // status light. What a provider needs is said in words on its second line.
  const PROVIDER_CATALOG: {
    key: ProviderKey;
    name: string;
    desc: string;
    auth: string;
  }[] = [
    {
      key: 'acoustId',
      name: 'AcoustID',
      desc: 'Fingerprint → MusicBrainz recording match',
      auth: 'Free'
    },
    {
      key: 'spotifyApi',
      name: 'Spotify API',
      desc: 'Artist + title catalog search with ISRC verification',
      auth: 'OAuth'
    },
    {
      key: 'deezer',
      name: 'Deezer',
      desc: 'ISRC-first, then artist + title catalog search (no sign-in)',
      auth: 'Free'
    },
    {
      key: 'appleMusic',
      name: 'Apple Music',
      desc: 'iTunes artist + title catalog search (no sign-in)',
      auth: 'Free'
    },
    {
      key: 'musicBrainzWeb',
      name: 'MusicBrainz web',
      desc: 'Direct ISRC / artist+title lookups against MusicBrainz',
      auth: 'Free'
    },
    {
      key: 'tracker',
      name: 'Community trackers',
      desc: 'Juice WRLD unreleased / leak files (best-effort)',
      auth: 'Community'
    }
  ];

  // A provider's second line: what it needs, then what it does. Only Spotify's credentials are
  // known here (the others are free, or configured on the server), so only Spotify can say it
  // still needs them — in words, where a coloured dot used to sit.
  function providerSublabel(p: (typeof PROVIDER_CATALOG)[number]): string {
    const needsCredentials =
      p.key === 'spotifyApi' &&
      savedCredentials != null &&
      !(savedCredentials.clientId && savedCredentials.hasClientSecret);
    return `${needsCredentials ? 'Needs credentials (Sources)' : p.auth} · ${p.desc}`;
  }

  // The trailing value on the Providers row of the list: how many are on.
  const providersValue = $derived(
    providers
      ? `${PROVIDER_CATALOG.filter((p) => providers?.[p.key]).length} of ${PROVIDER_CATALOG.length}`
      : undefined
  );

  // A disclosure's summary line in a section footer: the 18px footnote line plus py-2 is 34px,
  // so a pseudo-element grows the hit area to 44pt without spacing the footer out.
  const SUMMARY =
    'text-primary relative w-fit cursor-pointer py-2 select-none after:absolute after:inset-x-0 after:-inset-y-[5px]';
  // A link inside running footer text: the type stays put and the hit area grows to 44pt.
  const INLINE_LINK =
    'text-primary relative underline-offset-2 hover:underline after:absolute after:-inset-x-1 after:-inset-y-3.5';

  // A read-only server path, or why there is none to show. The demo cannot read the settings
  // document (it is admin-only), so it says so instead of rendering an empty field.
  function pathText(value: string | null | undefined): string {
    if (value) return value;
    if (demo) return 'Hidden in the demo';
    if (settingsFailed) return 'Unavailable';
    return isLoading ? 'Loading…' : 'Not set';
  }

  // ── progress announcements ─────────────────────────────────────────────────────
  // One polite live region for the page, mounted from the start and outside the {#key} below, so
  // a section change never re-creates it: a status region inserted already holding its text is
  // not announced, which is why the banners' own could only ever say how a job ended. Only
  // changes seen on this visit are spoken — a purge that finished last week, loaded with the
  // page, is not news.
  let liveMessage = $state('');
  let purgeSeen: string | null = null;
  let stagedSeen: string | null = null;

  $effect(() => {
    const snapshot = purgeSnapshot;
    if (isLoading) return;
    const status = snapshot?.status ?? 'idle';
    if (purgeSeen !== null && status !== purgeSeen && snapshot) {
      liveMessage = purgeAnnouncement(snapshot) || liveMessage;
    }
    purgeSeen = status;
  });

  $effect(() => {
    const snapshot = stagedSnapshot;
    if (isLoading) return;
    const status = snapshot?.status ?? 'idle';
    if (stagedSeen !== null && status !== stagedSeen && snapshot) {
      liveMessage = releaseAnnouncement(snapshot) || liveMessage;
    }
    stagedSeen = status;
  });
</script>

<!-- ─────────────────────────────── shared cells ─────────────────────────────── -->

{#snippet actionRow(
  label: string,
  onclick: () => void,
  opts: { disabled?: boolean; busy?: boolean; destructive?: boolean; ariaLabel?: string } = {}
)}
  <!-- An action in a list: tint text (red for a destructive one), dimmed while unavailable. -->
  <GroupedList.Row {onclick} disabled={opts.disabled || opts.busy} aria-label={opts.ariaLabel}>
    <span
      class="text-body md:text-sm {opts.disabled
        ? 'text-muted-foreground-dim'
        : opts.destructive
          ? 'text-destructive-text'
          : 'text-primary'}">{label}</span
    >
    {#snippet trailing()}
      {#if opts.busy}
        <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
      {/if}
    {/snippet}
  </GroupedList.Row>
{/snippet}

{#snippet pathRow(label: string, value: string | null | undefined)}
  <GroupedList.Row {label}>
    <span class="text-subheadline text-muted-foreground font-mono break-all select-text md:text-xs"
      >{pathText(value)}</span
    >
  </GroupedList.Row>
{/snippet}

{#snippet copyButton(copied: boolean, onclick: () => void, label: string)}
  <!-- 44pt on touch, 32px with a mouse. -->
  <Button
    variant="ghost"
    size="icon"
    class="shrink-0 pointer-coarse:size-11"
    {onclick}
    aria-label={copied ? 'Copied' : label}
    title={label}
  >
    {#if copied}
      <Check class="text-primary size-4" />
    {:else}
      <Copy class="size-4" />
    {/if}
  </Button>
{/snippet}

{#snippet settingsFailedSection()}
  <!-- The settings document did not load: say so once, at the top of each section built from
       it, with the way out. -->
  {#if settingsFailed}
    <GroupedList.Section
      footer="The paths, providers and switches here come from this server’s settings. Check that the server is running, then try again."
    >
      <GroupedList.Row
        onclick={() => (loadAttempt += 1)}
        label="Couldn’t load this server’s settings"
      >
        {#snippet leading()}
          <CircleAlert class="text-destructive-text size-[22px]" aria-hidden="true" />
        {/snippet}
        {#snippet trailing()}
          <span class="text-body text-primary md:text-sm">Try again</span>
        {/snippet}
      </GroupedList.Row>
    </GroupedList.Section>
  {/if}
{/snippet}

{#snippet loadingSection()}
  <div class="flex items-center justify-center py-16" role="status" aria-label="Loading">
    <Loader2 class="text-muted-foreground size-6 animate-spin" />
  </div>
{/snippet}

<!-- ─────────────────────── the section list (root / pane) ───────────────────── -->

{#snippet sectionList(pane: boolean)}
  <!-- One list, two presentations: the phone's Settings root (rows push ?tab=X) and the
       desktop pane list (rows select a pane in place, the current one highlighted). Built from
       the visible sections, so role gating lives in one place (sections.ts). -->
  {#snippet row(id: SectionId, icon?: typeof Users, value?: string)}
    {#if canSee(id)}
      {@const selected = pane && section === id}
      <GroupedList.Row
        href={sectionHref(id)}
        onclick={pane ? (e: MouseEvent) => selectPane(e, id) : undefined}
        {icon}
        iconClass={selected ? PANE_TILE_SELECTED : undefined}
        label={labelOf(id)}
        {value}
        {selected}
        chevron={!pane}
        class={pane ? `${PANE_ROW} ${selected ? PANE_SELECTED : ''}` : undefined}
      />
    {/if}
  {/snippet}

  {#if user}
    {@const selected = pane && section === 'account'}
    <!-- The account, as its own cell at the top — the Apple Account row of iOS Settings. -->
    <GroupedList.Section>
      <GroupedList.Row
        href={sectionHref('account')}
        onclick={pane ? (e: MouseEvent) => selectPane(e, 'account') : undefined}
        label={displayName}
        sublabel={demo ? 'Demo account' : 'Account, passkeys and mobile app'}
        {selected}
        chevron={!pane}
        class={selected ? PANE_SELECTED : undefined}
        aria-label="Account, {displayName}"
      >
        {#snippet leading()}
          {@render avatar(
            displayName,
            user.email,
            pane ? 'size-9 text-[14px]' : 'size-12 text-headline'
          )}
        {/snippet}
      </GroupedList.Row>
    </GroupedList.Section>
  {/if}

  <!-- Section headers are <h2>s under the nav bar's <h1> (VoiceOver's rotor jumps between them on
       a page that runs to 2000px on a phone) — except in the desktop pane list, which is
       navigation (a <nav> landmark) and must not put "Library" ahead of the page's own <h1>. -->
  <GroupedList.Section headingLevel={pane ? undefined : 2} header="Library">
    {@render row('sources', FolderInput)}
    {@render row('providers', Radar, providersValue)}
    {@render row('output', FolderOutput)}
  </GroupedList.Section>

  {#if canSee('people')}
    <GroupedList.Section headingLevel={pane ? undefined : 2} header="Sharing">
      {@render row('people', Users)}
    </GroupedList.Section>
  {/if}

  <GroupedList.Section>
    {@render row('updates', CircleArrowUp, version ? `v${version}` : undefined)}
  </GroupedList.Section>
{/snippet}

<!-- ───────────────────────────────── sections ───────────────────────────────── -->

{#snippet sourcesSection()}
  {#snippet sourcesFooter()}
    <p>
      Where MusicHoarder reads raw files — scanned, fingerprinted and enriched, never modified, only
      copied. fpcalc is the Chromaprint tool used for fingerprinting; it must be on
      <code class="bg-muted rounded px-1">$PATH</code> or an absolute path.
    </p>
    <!-- The how-to is for the one reader who runs the server; everyone else can stop above. -->
    <details class="mt-1">
      <summary class={SUMMARY}>How to change these</summary>
      <p>
        They are the server's <code class="bg-muted rounded px-1">source-directory</code> and
        <code class="bg-muted rounded px-1">destination-directory</code> parameters (AppHost user-secrets
        in development, environment variables in a compose deployment). Edit them and restart the server.
      </p>
    </details>
  {/snippet}

  <GroupedList.Section headingLevel={2} header="Source directories" footer={sourcesFooter}>
    {@render pathRow('Primary source', settings?.paths.sourceDirectory)}
    {@render pathRow('fpcalc binary', settings?.paths.fpcalcPath)}
  </GroupedList.Section>

  <!-- Spotify (sync source) -->
  {#snippet spotifyFooter()}
    <div class="flex flex-col gap-2">
      {#if saveResult}
        <StatusLine tone={saveResult.success ? 'success' : 'error'}>{saveResult.message}</StatusLine
        >
      {/if}
      {#if spotifyError}
        <StatusLine tone="error">{spotifyError}</StatusLine>
      {/if}
      <p>Pulls liked songs, playlists and release metadata. Audio is never streamed.</p>
      <!-- Open until Spotify is connected: the steps are what someone setting it up needs. -->
      <details open={!spotifyStatus?.connected}>
        <summary class={SUMMARY}>How to get Spotify API credentials</summary>
        <ol class="list-inside list-decimal space-y-1">
          <li>
            Open the
            <a
              href="https://developer.spotify.com/dashboard"
              target="_blank"
              rel="noopener noreferrer"
              class={INLINE_LINK}>Spotify Developer Dashboard</a
            >.
          </li>
          <li>Create a new app (or use an existing one).</li>
          <li>
            Add the redirect URI above under <em>Settings → Redirect URIs</em>. Spotify does not
            allow <code class="bg-muted rounded px-1">localhost</code> — use a loopback IP like
            <code class="bg-muted rounded px-1">127.0.0.1</code>.
          </li>
          <li>Copy the Client ID and Client Secret here.</li>
        </ol>
      </details>
    </div>
  {/snippet}

  <GroupedList.Section headingLevel={2} header="Spotify sync source" footer={spotifyFooter}>
    <!-- When the connection was made is said here, not in the Spotify page's header. -->
    <GroupedList.Row
      label="Status"
      value={spotifyValue}
      sublabel={spotifyStatus?.connected && spotifyStatus.connectedAt
        ? `Since ${formatDate(spotifyStatus.connectedAt)}`
        : undefined}
    >
      {#snippet trailing()}
        {#if spotifyStatus?.connected}
          <CircleCheck class="text-primary size-5" aria-hidden="true" />
        {/if}
      {/snippet}
    </GroupedList.Row>
    <FieldRow label="Client ID" for="client-id">
      <Input
        id="client-id"
        type="text"
        autocapitalize="off"
        autocorrect="off"
        spellcheck={false}
        enterkeyhint="next"
        placeholder="Your Spotify Client ID"
        bind:value={clientId}
        oninput={() => (saveResult = null)}
        disabled={!admin}
        class="font-mono"
      />
    </FieldRow>
    <FieldRow
      label="Client secret"
      for="client-secret"
      hint={savedCredentials?.hasClientSecret && !clientSecret
        ? 'Already saved — enter a new value to replace it.'
        : undefined}
    >
      <div class="flex items-center gap-2">
        <Input
          id="client-secret"
          type={showSecret ? 'text' : 'password'}
          autocapitalize="off"
          autocorrect="off"
          spellcheck={false}
          autocomplete="off"
          placeholder={savedCredentials?.hasClientSecret
            ? '••••••••••••••••'
            : 'Your Spotify Client Secret'}
          bind:value={clientSecret}
          oninput={() => (saveResult = null)}
          disabled={!admin}
          class="min-w-0 flex-1 font-mono"
        />
        <Button
          type="button"
          variant="gray"
          class="h-11 shrink-0 rounded-full px-4 md:h-8 md:px-3"
          onclick={() => (showSecret = !showSecret)}
          aria-pressed={showSecret}
        >
          {showSecret ? 'Hide' : 'Show'}
        </Button>
      </div>
    </FieldRow>
    <!-- Only from the settings document: before it loads (and for the demo, which cannot read
         it) the fallback would be a made-up loopback address, not this server's. -->
    {#if settings}
      <!-- Shown whole (wrapping, select-all) rather than in a read-only field that cut it off at
           a phone's or a tablet pane's width: it has to be typed or pasted exactly. -->
      <FieldRow label="Redirect URI" hint="Add this exact URI to your Spotify app’s redirect URIs.">
        <div class="flex items-center gap-1">
          <p class="text-body min-w-0 flex-1 font-mono break-all select-all md:text-xs">
            {redirectUri}
          </p>
          {@render copyButton(redirectCopied, copyRedirectUri, 'Copy redirect URI')}
        </div>
      </FieldRow>
    {/if}
    {#if (settings?.spotify.scopes ?? []).length > 0}
      <FieldRow label="Scopes requested">
        <div class="flex flex-wrap gap-1.5">
          {#each settings?.spotify.scopes ?? [] as scope (scope)}
            <Badge variant="secondary" class="font-mono">{scope}</Badge>
          {/each}
        </div>
      </FieldRow>
    {/if}
    {@render actionRow('Save credentials', handleSaveCredentials, {
      disabled: !admin || !clientId.trim() || !clientSecret.trim(),
      busy: isSaving
    })}
    {#if spotifyStatus?.connected}
      {@render actionRow('Disconnect Spotify…', () => (disconnectOpen = true), {
        destructive: true
      })}
    {:else}
      {@render actionRow('Connect Spotify', handleConnectSpotify, {
        disabled: !savedCredentials?.hasClientSecret,
        busy: isConnecting
      })}
    {/if}
  </GroupedList.Section>

  <AlertDialog.Root bind:open={disconnectOpen}>
    <AlertDialog.Content>
      <AlertDialog.Header>
        <AlertDialog.Title>Disconnect Spotify?</AlertDialog.Title>
        <AlertDialog.Description>
          Liked songs and playlists stop syncing until you connect again.
        </AlertDialog.Description>
      </AlertDialog.Header>
      <AlertDialog.Footer>
        <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
        <!-- Every Action here closes its dialog before starting its work (the Action would close
             it after the handler anyway), so a failure's toast never lands under a stale confirm
             or leaves a purge armed for a second one. -->
        <AlertDialog.Action
          variant="destructive"
          onclick={() => {
            disconnectOpen = false;
            void handleDisconnectSpotify();
          }}
        >
          Disconnect
        </AlertDialog.Action>
      </AlertDialog.Footer>
    </AlertDialog.Content>
  </AlertDialog.Root>

  <!-- Soulseek & sync (read-only status) -->
  {#if admin}
    <GroupedList.Section headingLevel={2}
      header="Soulseek and sync"
      footer="Read-only: both are configured with server environment variables."
    >
      <GroupedList.Row
        label="Soulseek (slskd)"
        sublabel={soulseekStatus?.configured
          ? soulseekStatus.version
            ? `slskd ${soulseekStatus.version}`
            : undefined
          : soulseekSyncLoaded && soulseekStatus
            ? 'Searches the Soulseek network for better-quality copies of your tracks.'
            : undefined}
        value={soulseekValue}
      >
        {#snippet trailing()}
          {#if soulseekStatus?.configured && soulseekStatus.connected}
            <CircleCheck class="text-primary size-5" aria-hidden="true" />
          {/if}
        {/snippet}
      </GroupedList.Row>
      <GroupedList.Row
        label="Library sync"
        sublabel={!soulseekSyncLoaded || !syncStatus
          ? undefined
          : syncStatus.mode === 'Push'
            ? 'Pushing built tracks to the receiving deployment.'
            : syncStatus.mode === 'Receive'
              ? 'Receiving tracks pushed from another deployment.'
              : 'This deployment neither pushes nor receives.'}
        value={syncValue}
      />
      {#if soulseekSyncLoaded && syncStatus?.mode === 'Push'}
        <GroupedList.Row label="Synced" value={syncStatus.outbox.synced.toLocaleString()} />
        <GroupedList.Row
          label="Pending"
          value={(syncStatus.outbox.pending + syncStatus.outbox.uploading).toLocaleString()}
        />
        <GroupedList.Row label="Failed" value={syncStatus.outbox.failed.toLocaleString()} />
        <GroupedList.Row
          label="Remote better"
          value={syncStatus.outbox.skippedRemoteBetter.toLocaleString()}
        />
      {/if}
    </GroupedList.Section>
  {/if}

  <!-- Download staging (admin-only housekeeping) -->
  {#if admin && settings?.downloads.enabled}
    {#snippet stagingFooter()}
      <div class="flex flex-col gap-2">
        {#if stagedToggleError}
          <StatusLine tone="error">{stagedToggleError}</StatusLine>
        {/if}
        {#if stagedStartError}
          <StatusLine tone="error">{stagedStartError}</StatusLine>
        {/if}
        <p>
          Every download is indexed from the staging folder and copied into your library, so it is
          stored twice. Releasing deletes the staged copy once the library copy has been verified
          (present, readable, matching duration and size) — the library copy is then the only one. A
          release runs in the background; its progress is here when you come back.
        </p>
      </div>
    {/snippet}

    <GroupedList.Section headingLevel={2} header="Download staging" footer={stagingFooter}>
      <SwitchRow
        label="Release staged copies automatically"
        sublabel="An hourly sweep releases downloads built more than {stagedPreview?.graceMinutes ??
          15} minutes ago. Off by default."
        checked={settings.downloads.releaseStagedSources}
        disabled={stagedToggleBusy}
        busy={stagedToggleBusy}
        onCheckedChange={(v) => void onToggleReleaseStagedSources(v)}
      />
      <GroupedList.Row label="Staged copies waiting">
        <span class="text-subheadline text-muted-foreground md:text-xs">
          {#if !stagedPreview}
            Loading…
          {:else if stagedPreview.unavailableReason}
            Not available on this deployment ({stagedPreview.unavailableReason}).
          {:else}
            <span class="tabular-nums">{stagedPreview.eligible.toLocaleString()}</span> files ·
            {formatBytes(stagedPreview.eligibleBytes)} reclaimable ·
            <span class="tabular-nums">{stagedPreview.released.toLocaleString()}</span> already
            released ({formatBytes(stagedPreview.releasedBytes)})
          {/if}
        </span>
      </GroupedList.Row>
      <!-- Red: it deletes the staged copies for good, leaving the library copy the only one. -->
      {@render actionRow('Release now…', () => (releaseDialogOpen = true), {
        destructive: true,
        disabled: releaseDisabled,
        busy: stagedRunning
      })}
      {#if stagedSnapshot && stagedSnapshot.status !== 'idle'}
        <StagedSourceReleaseBanner snapshot={stagedSnapshot} />
      {/if}
    </GroupedList.Section>

    <AlertDialog.Root bind:open={releaseDialogOpen}>
      <AlertDialog.Content>
        <AlertDialog.Header>
          <AlertDialog.Title>Release staged copies now?</AlertDialog.Title>
          <AlertDialog.Description>
            {stagedPreview?.eligible.toLocaleString() ?? 0} staged files are deleted once each library
            copy checks out. This cannot be undone.
          </AlertDialog.Description>
        </AlertDialog.Header>
        <AlertDialog.Footer>
          <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
          <AlertDialog.Action
            variant="destructive"
            onclick={() => {
              releaseDialogOpen = false;
              void handleReleaseStagedSources();
            }}
          >
            Release
          </AlertDialog.Action>
        </AlertDialog.Footer>
      </AlertDialog.Content>
    </AlertDialog.Root>
  {/if}
{/snippet}

{#snippet providersSection()}
  {#snippet providersFooter()}
    <div class="flex flex-col gap-2">
      {#if providersError}
        <StatusLine tone="error">{providersError}</StatusLine>
      {/if}
      <p>
        Queried in parallel during match. A change applies from the next enrichment cycle; songs
        already attempted keep their per-provider history.
      </p>
      <!-- What used to be two "Soon" sections (and a whole Filename rules page), kept as the one
           line of news it is. -->
      <p>
        The auto-accept threshold, single-source minimum and per-provider weights are set in the
        server’s MusicEnricher configuration; an in-app editor for them is coming, and so are
        filename rules that turn the parts of a filename into metadata for music public databases
        don’t track.
      </p>
    </div>
  {/snippet}

  <!-- No section header: it would only repeat the page title. -->
  <GroupedList.Section footer={providersFooter}>
    {#each PROVIDER_CATALOG as p (p.key)}
      {@const sublabel = providerSublabel(p)}
      {#if demo || settingsFailed}
        <!-- The demo cannot read the settings document (and it may have failed to load): six
             switches all off would say every provider is off. -->
        <GroupedList.Row
          label={p.name}
          {sublabel}
          value={demo ? 'Hidden in the demo' : 'Unavailable'}
        />
      {:else}
        <SwitchRow
          label={p.name}
          {sublabel}
          checked={providers?.[p.key] ?? false}
          disabled={!providers}
          busy={(providersSaving[p.key] ?? 0) > 0}
          onCheckedChange={(v) => void toggleProvider(p.key, v)}
        />
      {/if}
    {/each}
  </GroupedList.Section>

  <!-- AI quality grading (real toggle) -->
  {#snippet gradingFooter()}
    <div class="flex flex-col gap-2">
      {#if gradingError}
        <StatusLine tone="error">{gradingError}</StatusLine>
      {/if}
      <p>
        An LLM grades each enrichment result so you can benchmark and debug the algorithm. Turn it
        off to stop the background grading sweep.
      </p>
    </div>
  {/snippet}

  <GroupedList.Section footer={gradingFooter}>
    {#if demo || settingsFailed}
      <GroupedList.Row
        label="AI quality grading"
        sublabel="Grades enriched songs in the background and powers the AI quality page."
        value={demo ? 'Hidden in the demo' : 'Unavailable'}
      />
    {:else}
      <SwitchRow
        label="AI quality grading"
        checked={qualityGrading?.enabled ?? false}
        disabled={!qualityGrading}
        busy={gradingSaving > 0}
        onCheckedChange={(v) => void toggleQualityGrading(v)}
      >
        {#snippet sublabel()}
          {#if qualityGrading && !qualityGrading.configured}
            No API key on the server — also set
            <code class="bg-muted rounded px-1 font-mono">QUALITY_GRADING_API_KEY</code> for grading to
            run.
          {:else}
            Grades enriched songs in the background and powers the AI quality page.
          {/if}
        {/snippet}
      </SwitchRow>
    {/if}
  </GroupedList.Section>
{/snippet}

{#snippet outputSection()}
  {#snippet outputFooter()}
    <p>
      Where the organised library is written after enrichment and tagging, as
      <!-- Breaks only after a slash, never inside a {placeholder}. -->
      <code class="bg-muted rounded px-1 font-mono"
        >{'{albumartist}/'}<wbr />{'{album}/'}<wbr />{'{track:02} - {title}'}</code
      >. Set by whoever runs this server; changing it needs a restart. Editable folder and filename
      templates are coming.
    </p>
    <details class="mt-1">
      <summary class={SUMMARY}>How to change it</summary>
      <p>
        It is the server's <code class="bg-muted rounded px-1">destination-directory</code>
        parameter (AppHost user-secrets in development, an environment variable in a compose deployment).
        Edit it and restart the server.
      </p>
    </details>
  {/snippet}

  <GroupedList.Section footer={outputFooter}>
    {@render pathRow('Destination', settings?.paths.destinationDirectory)}
  </GroupedList.Section>

  <MusicVideoAuditCard {admin} />

  <!-- Danger zone (real purge actions). Here rather than under Account: both delete what the
       pipeline built — enrichment state and the destination copies — not anything personal. -->
  {#if admin}
    {#snippet dangerFooter()}
      <div class="flex flex-col gap-2">
        {#if purgeStartError}
          <StatusLine tone="error">{purgeStartError}</StatusLine>
        {/if}
        <p>
          <span class="text-foreground">Reset enrichment data</span> keeps your scanned files and
          fingerprints, and clears enrichment results, provider attempts, lyrics, duplicate
          detection and library-build status for every song.
          <span class="text-foreground">Purge all data</span> removes every song, provider attempt and
          cached Spotify match, so the next run re-scans and re-fingerprints from source. Both delete
          the files copied to the destination folder; source files are never touched — but a download
          whose staged copy was released has no other copy. Make sure no job is running.
        </p>
      </div>
    {/snippet}

    <GroupedList.Section headingLevel={2} header="Danger zone" footer={dangerFooter}>
      {@render actionRow('Reset enrichment data…', () => (resetDialogOpen = true), {
        destructive: true,
        disabled: purgeRunning || purgeStarting,
        busy: purgeRunning && purgeSnapshot?.mode === 'post-fingerprint'
      })}
      {@render actionRow('Purge all data…', () => (purgeAllDialogOpen = true), {
        destructive: true,
        disabled: purgeRunning || purgeStarting,
        busy: purgeRunning && purgeSnapshot?.mode === 'all'
      })}
      {#if purgeSnapshot && purgeSnapshot.status !== 'idle'}
        <PurgeStatusBanner snapshot={purgeSnapshot} />
      {/if}
    </GroupedList.Section>

    <!-- Alert bodies stay one or two sentences (the full consequences are in the footer above),
         so they never scroll at larger text sizes. -->
    <AlertDialog.Root bind:open={resetDialogOpen}>
      <AlertDialog.Content>
        <AlertDialog.Header>
          <AlertDialog.Title>Reset enrichment data?</AlertDialog.Title>
          <AlertDialog.Description>
            Every song is re-enriched from its fingerprint, and the destination copies are deleted.
            This cannot be undone.
          </AlertDialog.Description>
        </AlertDialog.Header>
        <AlertDialog.Footer>
          <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
          <AlertDialog.Action
            variant="destructive"
            onclick={() => {
              resetDialogOpen = false;
              void handlePurge('post-fingerprint');
            }}
          >
            Reset
          </AlertDialog.Action>
        </AlertDialog.Footer>
      </AlertDialog.Content>
    </AlertDialog.Root>

    <AlertDialog.Root bind:open={purgeAllDialogOpen} onOpenChange={onPurgeAllDialogOpenChange}>
      <AlertDialog.Content>
        <AlertDialog.Header>
          <AlertDialog.Title>Purge all data?</AlertDialog.Title>
          <AlertDialog.Description>
            Every song record and the destination copies are deleted, and any grading history with
            them. This cannot be undone.
          </AlertDialog.Description>
        </AlertDialog.Header>
        <div class="flex flex-col gap-1.5">
          <!-- One span: Label is a flex box, and loose words around a <b> become flex items with the
               gap on both sides ("Type  DELETE  to confirm"). -->
          <Label
            for="purge-all-confirm"
            class="text-footnote text-muted-foreground font-normal md:text-xs"
          >
            <span
              >Type <b class="text-foreground font-semibold">{PURGE_ALL_CONFIRM_WORD}</b> to confirm</span
            >
          </Label>
          <Input
            id="purge-all-confirm"
            autocomplete="off"
            autocapitalize="characters"
            autocorrect="off"
            spellcheck={false}
            bind:value={purgeAllConfirmText}
            placeholder={PURGE_ALL_CONFIRM_WORD}
          />
        </div>
        <AlertDialog.Footer>
          <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
          <!-- Closing also clears the typed word, so the next purge asks for it again. -->
          <AlertDialog.Action
            variant="destructive"
            disabled={!purgeAllConfirmed}
            onclick={() => {
              onPurgeAllDialogOpenChange(false);
              void handlePurge('all');
            }}
          >
            Purge all data
          </AlertDialog.Action>
        </AlertDialog.Footer>
      </AlertDialog.Content>
    </AlertDialog.Root>
  {/if}
{/snippet}

{#snippet accountSection()}
  {#if user}
    <!-- Who you are, centred like the head of an Apple Account page. -->
    <div class="flex flex-col items-center gap-1 px-4 pt-2 text-center">
      {@render avatar(displayName, user.email, 'size-20 text-title-1')}
      <p class="text-title-2 mt-2 max-w-full break-words">{displayName}</p>
      {#if displayName !== user.email}
        <p class="text-subheadline text-muted-foreground max-w-full break-all">{user.email}</p>
      {/if}
    </div>

    {#snippet profileFooter()}
      <div class="flex flex-col gap-2">
        {#if nameError}
          <StatusLine tone="error">{nameError}</StatusLine>
        {/if}
        {#if demo}
          <p>
            You’re signed in as the demo account. You can browse and play the seeded library, but
            the demo can’t change settings — scanning, editing, approving, deleting and the rest are
            turned off.
          </p>
        {:else}
          <p>You sign in with an emailed link or a passkey — there are no passwords.</p>
        {/if}
        {#if admin}
          <!-- Was a Privacy section holding one "Soon" row; the news fits in a sentence. -->
          <p>
            MusicHoarder ships no telemetry — track names, artists and paths never leave your
            server. An opt-in for anonymous pipeline-performance stats is coming.
          </p>
        {/if}
      </div>
    {/snippet}

    <GroupedList.Section footer={profileFooter}>
      {#if isEditingName}
        <FieldRow label="Name" for="account-name">
          <Input
            id="account-name"
            bind:value={nameDraft}
            placeholder="Account name"
            maxlength={100}
            autocomplete="name"
            enterkeyhint="done"
            disabled={isSavingName}
            onkeydown={(e) => {
              if (e.key === 'Enter') void handleSaveName();
              if (e.key === 'Escape') isEditingName = false;
            }}
            {@attach focusOnMount}
          />
          <div class="flex justify-end gap-2 pt-1">
            <Button
              variant="gray"
              class="h-11 rounded-full px-5 md:h-8 md:px-3"
              onclick={() => (isEditingName = false)}
              disabled={isSavingName}
            >
              Cancel
            </Button>
            <Button
              class="h-11 rounded-full px-5 md:h-8 md:px-3"
              onclick={handleSaveName}
              disabled={isSavingName}
            >
              {#if isSavingName}
                <Loader2 class="size-4 animate-spin" />
              {/if}
              Save
            </Button>
          </div>
        </FieldRow>
      {:else if admin}
        <GroupedList.Row
          label="Name"
          value={user.displayName?.trim() || 'Not set'}
          onclick={startEditName}
          aria-label="Name, {user.displayName?.trim() || 'not set'}. Edit"
          chevron
        />
      {:else}
        <GroupedList.Row label="Name" value={user.displayName?.trim() || user.email} />
      {/if}
      <GroupedList.Row label="Role" value={roleLabel(user.role)} />
    </GroupedList.Section>

    <GroupedList.Section headingLevel={2}
      header="Accounts on this device"
      footer="Sign in to each account once, then switch between them without signing out."
    >
      {#if browserAccounts === null}
        {#if accountsFailed}
          <GroupedList.Row
            onclick={() => (accountsFailed = false)}
            label="Couldn’t load the account list"
          >
            {#snippet trailing()}
              <span class="text-body text-primary md:text-sm">Try again</span>
            {/snippet}
          </GroupedList.Row>
        {:else}
          <GroupedList.Row label="Loading accounts…">
            {#snippet trailing()}
              <Loader2 class="text-muted-foreground size-5 animate-spin" aria-hidden="true" />
            {/snippet}
          </GroupedList.Row>
        {/if}
      {:else}
        {#each browserAccounts as account (account.userId)}
          {@const accountName = account.displayName?.trim() || account.email}
          <GroupedList.Row
            onclick={account.isActive ? undefined : () => handleSwitchAccount(account)}
            disabled={accountSwitchingTo !== null && accountSwitchingTo !== account.userId}
            label={accountName}
            sublabel="{account.email} · {roleLabel(account.role)}"
            aria-label={account.isActive ? undefined : `Switch to ${accountName}`}
          >
            {#snippet leading()}
              {@render avatar(accountName, account.email, 'size-8 text-[13px]')}
            {/snippet}
            {#snippet trailing()}
              {#if account.isActive}
                <span class="text-body text-muted-foreground md:text-sm">Active</span>
              {:else if accountSwitchingTo === account.userId}
                <Loader2 class="text-muted-foreground size-5 animate-spin" aria-hidden="true" />
                <span class="sr-only">Switching…</span>
              {:else}
                <span class="text-body text-primary md:text-sm">Switch</span>
              {/if}
            {/snippet}
          </GroupedList.Row>
        {/each}
      {/if}
      <GroupedList.Row
        onclick={() => location.assign('/login?switch')}
        disabled={accountSwitchingTo !== null}
      >
        {#snippet leading()}
          <span aria-hidden="true" class="bg-muted grid size-8 place-items-center rounded-full">
            <Plus class="text-primary size-[18px]" strokeWidth={2.25} />
          </span>
        {/snippet}
        <span class="text-body text-primary md:text-sm">Add account</span>
      </GroupedList.Row>
    </GroupedList.Section>

    {#if canUsePasskeys}
      {#snippet passkeysFooter()}
        <div class="flex flex-col gap-2">
          {#if passkeyError}
            <StatusLine tone="error">{passkeyError}</StatusLine>
          {/if}
          <p>
            Sign in with {passkeyUnlockPhrase(platform)} — no email needed. Add one on each device you
            use; the Android app signs in with the same passkey. Keep an email that can receive sign-in
            links so you can always get back in.
          </p>
        </div>
      {/snippet}

      <GroupedList.Section headingLevel={2} header="Passkeys" footer={passkeysFooter}>
        {#if !passkeySupported}
          <GroupedList.Row label="This browser doesn’t support passkeys" />
        {:else}
          {#if passkeysLoading}
            <GroupedList.Row label="Loading passkeys…">
              {#snippet trailing()}
                <Loader2 class="text-muted-foreground size-5 animate-spin" aria-hidden="true" />
              {/snippet}
            </GroupedList.Row>
          {:else if passkeys.length === 0}
            <GroupedList.Row label="No passkeys yet" disabled />
          {:else}
            {#each passkeys as passkey (passkey.id)}
              <GroupedList.Row
                icon={KeyRound}
                label={passkey.displayName}
                sublabel="Added {formatDate(passkey.createdAtUtc)}{passkey.lastUsedAtUtc
                  ? ` · last used ${formatDate(passkey.lastUsedAtUtc)}`
                  : ''}"
              >
                {#snippet trailing()}
                  <!-- A credential, not a list row: removing the last one can lock a passkey-only
                       person out, so it confirms like the purges. -->
                  <AlertDialog.Root
                    bind:open={
                      () => removingPasskeyId === passkey.id,
                      (open) => (removingPasskeyId = open ? passkey.id : null)
                    }
                  >
                    <AlertDialog.Trigger>
                      {#snippet child({ props })}
                        <Button
                          {...props}
                          variant="ghost"
                          size="icon"
                          class="text-destructive-text hover:text-destructive-text -my-2 -mr-2 pointer-coarse:size-11"
                          aria-label={`Remove passkey ${passkey.displayName}`}
                        >
                          <Trash2 class="size-[18px]" />
                        </Button>
                      {/snippet}
                    </AlertDialog.Trigger>
                    <AlertDialog.Content>
                      <AlertDialog.Header>
                        <AlertDialog.Title>Remove this passkey?</AlertDialog.Title>
                        <AlertDialog.Description>
                          “{passkey.displayName}” will no longer sign you in to this account.
                          {#if passkeys.length === 1}
                            It's your only passkey: after this you sign in with an emailed link.
                          {/if}
                        </AlertDialog.Description>
                      </AlertDialog.Header>
                      <AlertDialog.Footer>
                        <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                        <AlertDialog.Action
                          variant="destructive"
                          onclick={() => {
                            removingPasskeyId = null;
                            void handleRemovePasskey(passkey.id);
                          }}
                        >
                          Remove passkey
                        </AlertDialog.Action>
                      </AlertDialog.Footer>
                    </AlertDialog.Content>
                  </AlertDialog.Root>
                {/snippet}
              </GroupedList.Row>
            {/each}
          {/if}
          <FieldRow label="New passkey name" for="passkey-name">
            <Input
              id="passkey-name"
              placeholder={defaultPasskeyName(platform)}
              autocapitalize="words"
              enterkeyhint="done"
              bind:value={newPasskeyName}
              oninput={() => (passkeyError = null)}
              onkeydown={(e) => {
                if (e.key === 'Enter' && !isAddingPasskey) void handleAddPasskey();
              }}
            />
          </FieldRow>
          {@render actionRow('Add a passkey', handleAddPasskey, { busy: isAddingPasskey })}
        {/if}
      </GroupedList.Section>
    {/if}

    {#if !demo}
      <!-- Renders only in Safari on iPhone/iPad: the iOS counterpart of the pairing section. -->
      <InstallAppCard />
      <!-- Members pair phones too: the token rides their own session, and the server's member
           allowlist deliberately permits POST /api/auth/device-token. -->
      <PairDeviceCard />
    {/if}

    <GroupedList.Section
      footer="Sign out ends this account’s session here; if another account is remembered on this device you switch to it. Sign out everywhere ends all of this account’s sessions — paired phones included — and forgets the other accounts remembered here."
    >
      <GroupedList.Row onclick={() => handleSignOut(false)} label="Sign out" destructive />
      <GroupedList.Row
        onclick={() => (signOutEverywhereOpen = true)}
        label="Sign out everywhere…"
        destructive
      />
    </GroupedList.Section>

    <AlertDialog.Root bind:open={signOutEverywhereOpen}>
      <AlertDialog.Content>
        <AlertDialog.Header>
          <AlertDialog.Title>Sign out everywhere?</AlertDialog.Title>
          <AlertDialog.Description>
            Every session of this account ends, paired phones included.
          </AlertDialog.Description>
        </AlertDialog.Header>
        <AlertDialog.Footer>
          <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
          <AlertDialog.Action
            variant="destructive"
            onclick={() => {
              signOutEverywhereOpen = false;
              void handleSignOut(true);
            }}
          >
            Sign out everywhere
          </AlertDialog.Action>
        </AlertDialog.Footer>
      </AlertDialog.Content>
    </AlertDialog.Root>
  {/if}
{/snippet}

{#snippet peopleSection()}
  {#if admin}
    <PeopleCard personId={person} bind:title={personTitle} />
  {:else}
    <GroupedList.Section>
      <GroupedList.Row label="Only an administrator can invite people and share music." />
    </GroupedList.Section>
  {/if}
{/snippet}

{#snippet updatesSection()}
  {#if version}
    <GroupedList.Section>
      <GroupedList.Row label="Current version" value="v{version}" />
    </GroupedList.Section>
  {/if}

  <GroupedList.Section headingLevel={2}
    header="Update command"
    footer="Run this in your compose stack’s directory. It pulls the latest images and recreates the containers — your database and library are untouched."
  >
    <div class="flex items-center gap-2 py-1 pr-2 pl-4">
      <code class="text-subheadline min-w-0 flex-1 py-2 font-mono break-words select-all md:text-xs"
        >{UPDATE_CMD}</code
      >
      {@render copyButton(updateCmdCopied, copyUpdateCommand, 'Copy update command')}
    </div>
  </GroupedList.Section>

  <GroupedList.Section headingLevel={2}
    header="Automatic updates"
    footer="When a newer release is out, a banner across the app says so — this page is the reference copy of the same instructions."
  >
    <p class="text-subheadline px-4 py-3 md:text-sm">
      Running <a
        href="https://github.com/ofkm/arcane"
        target="_blank"
        rel="noopener noreferrer"
        class={INLINE_LINK}>Arcane</a
      >? Add the <code class="bg-muted rounded px-1 font-mono">arcane.stack.auto-update</code> label to
      your compose stack to let it pull and redeploy new releases for you.
    </p>
  </GroupedList.Section>
{/snippet}

<!-- ───────────────────────────────── the page ───────────────────────────────── -->

<div class="bg-background-grouped flex min-h-0 flex-1" bind:clientWidth={pageWidth}>
  <p class="sr-only" role="status">{liveMessage}</p>
  {#if showPaneList}
    <!-- Desktop: the pane list, like macOS System Settings' sidebar. Its own scroller; its
         header lines up with the nav bar's 48px row and hairline across the split. -->
    <nav
      aria-label="Settings sections"
      class="border-separator flex w-[212px] shrink-0 flex-col border-r lg:w-[260px]"
    >
      <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
        <div class="border-separator bg-background-grouped sticky top-0 z-10 shrink-0 border-b">
          <p class="flex h-12 items-center px-4 text-[17px] leading-[22px] font-semibold lg:px-5">
            Settings
          </p>
        </div>
        <div class="flex flex-col gap-6 px-2 pt-4 pb-6 lg:px-3">
          {@render sectionList(true)}
        </div>
      </ScrollArea>
    </nav>
  {/if}

  <!-- The page's primary scroller, with the nav bar as its first child so the large title
       scrolls away and the bar collapses. Keyed on the section and person: stacked (a phone, a
       narrow window) each is a new page and must open at its top, not at the previous page's
       scroll offset. -->
  {#key `${section}|${person ?? ''}`}
    <ScrollArea class="min-h-0 min-w-0 flex-1" viewportClass="overscroll-contain">
      <PageToolbarV2 title={barTitle} back={barBack} {largeTitle} grouped />

      <!-- md+ padding stays modest until lg: at 768–1023 the app sidebar and the pane list
           already take ~470px of the window. -->
      <div class="mx-auto flex w-full max-w-2xl flex-col gap-8 pt-2 pb-8 md:px-5 md:pt-6 lg:px-8">
        {#if section === null}
          {@render sectionList(false)}
        {:else if section === 'account'}
          {@render accountSection()}
        {:else if section === 'people'}
          {@render peopleSection()}
        {:else if section === 'updates'}
          {@render updatesSection()}
        {:else if isLoading}
          {@render loadingSection()}
        {:else}
          {@render settingsFailedSection()}
          {#if section === 'sources'}
            {@render sourcesSection()}
          {:else if section === 'providers'}
            {@render providersSection()}
          {:else if section === 'output'}
            {@render outputSection()}
          {/if}
        {/if}
      </div>
    </ScrollArea>
  {/key}
</div>
