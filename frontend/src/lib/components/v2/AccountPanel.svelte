<script lang="ts" module>
  import { cn } from '$lib/utils';
  import { listAccounts, type AccountView } from '$lib/api-client';

  /** Two letters for an avatar — the display name's (or the email's) first two characters. */
  export function initialsOf(name: string | null | undefined, email: string): string {
    return (name?.trim() || email).slice(0, 2).toUpperCase();
  }

  // Module scope, not component scope: the sheet and the menu mount this panel only while open,
  // and the desktop top bar and a phone's tab roots each carry their own AccountButton. One fetch
  // per page load serves all of them. The list only changes through a login, a switch or a
  // logout, and each of those hard-reloads the page, which is what empties this cache.
  let accounts = $state<AccountView[] | null>(null);
  let accountsError = $state(false);
  let accountsLoading = false;
  let switchingTo = $state<string | null>(null);

  function loadAccounts(): void {
    if (accounts !== null || accountsLoading) return;
    accountsLoading = true;
    accountsError = false;
    listAccounts()
      .then((list) => (accounts = list))
      .catch(() => (accountsError = true))
      .finally(() => (accountsLoading = false));
  }

  // The initials avatar is shared with AccountButton's trigger (declared in the markup below; it
  // touches only module scope, which is what lets it be exported).
  export { avatar };
</script>

<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { setMode, userPrefersMode } from 'mode-watcher';
  import { DropdownMenu as DropdownMenuPrimitive } from 'bits-ui';
  import {
    Check,
    FolderSync,
    HardDrive,
    Loader2,
    LogOut,
    Plus,
    Settings,
    UserPlus
  } from '@lucide/svelte';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import {
    SegmentedControl,
    type SegmentedControlItem
  } from '$lib/components/ui/segmented-control';
  import { isAdmin, isDemo, roleLabel } from '$lib/auth/capabilities';
  import { signOutAndReset } from '$lib/auth/sign-out';
  import { switchAccountAndReload } from '$lib/auth/switch-account';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { storageUsage } from '$lib/stores/storage-usage.svelte';
  import { storageSummary, watchedFolders, watchingLabel } from '$lib/storage-usage-meta';

  // The account panel: who you are, the other accounts this browser remembers, the
  // appearance override, the admin's storage and folder status, the version, and sign out. One
  // body, two presentations — a grouped sheet on a phone (AccountButton wraps it in a
  // BottomSheet) and a menu on desktop (a DropdownMenu) — so the two can't drift in what they
  // offer. Members reach Settings, account switching and sign out ONLY through here and the
  // palette, so nothing below may be gated off for them except the admin status rows.
  type Props = {
    presentation: 'sheet' | 'menu';
    /** Close the sheet / menu (a row that navigates closes its container first). */
    close: () => void;
    /** Hand off to the storage breakdown dialog (the container closes, the dialog opens). */
    onstorage: () => void;
  };

  const { presentation, close, onstorage }: Props = $props();

  const user = $derived(page.data.user);
  const admin = $derived(isAdmin(user));
  const version = $derived(page.data.appVersion as string | null | undefined);
  const name = $derived(user ? user.displayName?.trim() || user.email : '');

  // Admin and demo get the Settings root (demo sees every section but People, read-only); a
  // member has exactly one section, so go straight to it rather than to a root of one row.
  const settingsHref = $derived(admin || isDemo(user) ? '/settings' : '/settings?tab=account');

  // Lazy: nothing is fetched until the panel first opens. Storage and folders are admin-only
  // chrome — the chrome treats Demo as a non-admin (their endpoints would 403 or read empty). The
  // folders come from the overview the shell already fetched (it hands it to the pipeline store),
  // so opening this never sends a second /overview.
  $effect(() => {
    untrack(() => {
      loadAccounts();
      if (admin) storageUsage.ensureLoaded();
    });
  });

  async function handleSwitch(account: AccountView) {
    if (account.isActive || switchingTo) return;
    switchingTo = account.userId;
    try {
      await switchAccountAndReload(account.userId);
    } catch {
      switchingTo = null;
    }
  }

  function addAccount() {
    // A hard navigation: /login?switch parks this session, then the sign-in reloads the app.
    location.assign('/login?switch');
  }

  function signOut() {
    // Sign-out without a parked account to fall back to is a soft navigation to /login, so the
    // module cache would outlive the session; drop it with the rest of the user's state.
    accounts = null;
    void signOutAndReset();
  }

  function retryAccounts() {
    accountsError = false;
    loadAccounts();
  }

  // ── storage (admin) ─────────────────────────────────────────────────────────
  // The same snapshot, figure and bar as the sidebar footer and the Manage hub (storageSummary).
  const storage = $derived(storageSummary(storageUsage.snapshot, storageUsage.computing));
  const storageLabel = $derived(storage?.label ?? '');
  const storageSegments = $derived(storage?.segments ?? []);

  // ── watched folders (admin) ─────────────────────────────────────────────────
  const folders = $derived(watchedFolders(pipelineOverlay.overview));
  const watching = $derived(watchingLabel(folders.length));

  // ── appearance ──────────────────────────────────────────────────────────────
  // A deliberate HIG departure kept from the old top bar (CLAUDE.md): it defaults to System and
  // keeps that option, so the OS stays the source of truth unless someone overrides it.
  type Appearance = 'system' | 'light' | 'dark';
  const appearanceItems: SegmentedControlItem<Appearance>[] = [
    { value: 'system', label: 'System' },
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' }
  ];
  const appearance = $derived<Appearance>(userPrefersMode.current ?? 'system');

  function accountSubtitle(account: AccountView): string {
    return isAdmin(account) ? account.email : `${account.email} · ${roleLabel(account.role)}`;
  }
</script>

{#snippet avatar(label: string, email: string, sizeClass: string)}
  <!-- The initials avatar every account surface uses. A darker end of the old cyan ramp so the
       white initials stay legible (the old cyan-300 end put them near 2:1). -->
  <span
    aria-hidden="true"
    class={cn(
      'grid shrink-0 place-items-center rounded-full bg-gradient-to-br from-cyan-800 to-cyan-500 font-semibold text-white select-none',
      sizeClass
    )}>{initialsOf(label, email)}</span
  >
{/snippet}

{#snippet storageBar(heightClass: string)}
  <span
    class={cn('bg-muted flex w-full overflow-hidden rounded-full', heightClass)}
    aria-hidden="true"
  >
    {#each storageSegments as segment (segment.key)}
      <span class="{segment.color} h-full" style:width="{segment.pct}%"></span>
    {/each}
  </span>
{/snippet}

{#snippet watchingFooter()}
  <!-- The folder paths as text, not a tooltip: a finger can't hover. -->
  <span class="block">{watching}</span>
  {#each folders as folder (folder.label)}
    <span class="block break-all"
      >{folder.label}: <span class="text-caption-1 font-mono">{folder.path}</span></span
    >
  {/each}
{/snippet}

{#if user}
  {#if presentation === 'sheet'}
    <div class="flex flex-col gap-7 pt-2">
      <!-- Who you are. Not a row: the Settings row below is where it leads. -->
      <GroupedList.Section>
        <div class="flex items-center gap-3.5 px-4 py-3">
          {@render avatar(name, user.email, 'size-14 text-title-3')}
          <div class="flex min-w-0 flex-col">
            <p class="text-title-3 truncate">{name}</p>
            {#if name !== user.email}
              <p class="text-subheadline text-muted-foreground truncate">{user.email}</p>
            {/if}
            {#if !admin}
              <p class="text-footnote text-muted-foreground">{roleLabel(user.role)}</p>
            {/if}
          </div>
        </div>
      </GroupedList.Section>

      <GroupedList.Section header="Accounts on this device">
        {#if accounts === null}
          {#if accountsError}
            <GroupedList.Row onclick={retryAccounts} label="Couldn’t load accounts">
              {#snippet trailing()}
                <span class="text-body text-primary">Try again</span>
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
          {#each accounts as account (account.userId)}
            {@const accountName = account.displayName?.trim() || account.email}
            <GroupedList.Row
              onclick={account.isActive ? undefined : () => handleSwitch(account)}
              disabled={switchingTo !== null && switchingTo !== account.userId}
              label={accountName}
              sublabel={accountSubtitle(account)}
              aria-label={account.isActive ? undefined : `Switch to ${accountName}`}
            >
              {#snippet leading()}
                {@render avatar(accountName, account.email, 'size-8 text-[13px]')}
              {/snippet}
              {#snippet trailing()}
                {#if account.isActive}
                  <Check class="text-primary size-5" strokeWidth={2.5} aria-hidden="true" />
                  <span class="sr-only">Signed in</span>
                {:else if switchingTo === account.userId}
                  <Loader2 class="text-muted-foreground size-5 animate-spin" aria-hidden="true" />
                  <span class="sr-only">Switching…</span>
                {/if}
              {/snippet}
            </GroupedList.Row>
          {/each}
        {/if}
        <GroupedList.Row onclick={addAccount} disabled={switchingTo !== null}>
          {#snippet leading()}
            <span aria-hidden="true" class="bg-muted grid size-8 place-items-center rounded-full">
              <Plus class="text-primary size-[18px]" strokeWidth={2.25} />
            </span>
          {/snippet}
          <span class="text-body text-primary">Add account</span>
        </GroupedList.Row>
      </GroupedList.Section>

      <!-- A segmented control is a control, not a cell: it sits straight on the sheet under its
           header rather than boxed in a card of its own. -->
      <div class="mx-4">
        <p class="text-footnote text-muted-foreground px-4 pb-1.5" aria-hidden="true">Appearance</p>
        <SegmentedControl
          items={appearanceItems}
          value={appearance}
          label="Appearance"
          onValueChange={(v) => setMode(v)}
        />
      </div>

      <GroupedList.Section footer={admin && folders.length ? watchingFooter : undefined}>
        <GroupedList.Row
          href={settingsHref}
          onclick={close}
          icon={Settings}
          label="Settings"
          chevron
        />
        {#if admin}
          <GroupedList.Row
            onclick={onstorage}
            icon={HardDrive}
            label="Storage"
            sublabel={storageLabel || undefined}
            chevron
            aria-label={storageLabel
              ? `Storage, ${storageLabel}. Show breakdown`
              : 'Storage breakdown'}
          >
            <!-- The bar under the figure, the full width of the text column (iPhone Storage). -->
            {#if storageSegments.length}
              <span class="mt-1.5 mb-0.5 block">{@render storageBar('h-1.5')}</span>
            {/if}
          </GroupedList.Row>
        {/if}
      </GroupedList.Section>

      <div class="flex flex-col gap-1">
        <GroupedList.Section>
          <GroupedList.Row onclick={signOut} label="Sign out" destructive />
        </GroupedList.Section>
        {@render versionLine()}
      </div>
    </div>
  {:else}
    <!-- Desktop menu. Same content, menu idiom: items for the actions, headings for the groups. -->
    <div class="flex items-center gap-3 px-2 py-2">
      {@render avatar(name, user.email, 'size-10 text-sm')}
      <div class="flex min-w-0 flex-col">
        <span class="truncate text-sm font-semibold">{name}</span>
        {#if name !== user.email}
          <span class="text-muted-foreground truncate text-xs">{user.email}</span>
        {/if}
        {#if !admin}
          <span class="text-muted-foreground text-xs">{roleLabel(user.role)}</span>
        {/if}
      </div>
    </div>

    <DropdownMenu.Separator />
    <DropdownMenu.Group>
      <DropdownMenu.GroupHeading class="text-muted-foreground px-2 py-1 text-xs font-medium">
        Accounts on this device
      </DropdownMenu.GroupHeading>
      {#if accounts === null}
        {#if accountsError}
          <DropdownMenu.Item closeOnSelect={false} onSelect={retryAccounts} class="text-xs">
            Couldn’t load accounts — try again
          </DropdownMenu.Item>
        {:else}
          <div class="text-muted-foreground flex items-center gap-2 px-2 py-1.5 text-xs">
            <Loader2 class="size-3.5 animate-spin" aria-hidden="true" /> Loading accounts…
          </div>
        {/if}
      {:else}
        {#each accounts as account (account.userId)}
          {@const accountName = account.displayName?.trim() || account.email}
          <!-- closeOnSelect off: the spinner has to stay visible until the reload lands. -->
          <DropdownMenu.Item
            closeOnSelect={false}
            onSelect={() => handleSwitch(account)}
            disabled={switchingTo !== null && switchingTo !== account.userId}
            class="gap-2.5"
          >
            {@render avatar(accountName, account.email, 'size-7 text-[11px]')}
            <span class="flex min-w-0 flex-1 flex-col">
              <span class="truncate text-[13px] font-medium">{accountName}</span>
              <span class="text-muted-foreground truncate text-[11px]"
                >{accountSubtitle(account)}</span
              >
            </span>
            {#if account.isActive}
              <Check class="text-primary size-4" aria-hidden="true" />
              <span class="sr-only">Signed in</span>
            {:else if switchingTo === account.userId}
              <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
              <span class="sr-only">Switching…</span>
            {/if}
          </DropdownMenu.Item>
        {/each}
      {/if}
      <DropdownMenu.Item onSelect={addAccount} disabled={switchingTo !== null}>
        <UserPlus /> Add account
      </DropdownMenu.Item>
    </DropdownMenu.Group>

    <DropdownMenu.Separator />
    <!-- Appearance as a segmented control that is still a menu: three radio items laid out as
         segments, so arrow keys reach them and each announces as a checked option. A plain
         SegmentedControl can't live in a menu — Tab closes a menu, so its buttons would be
         unreachable from the keyboard. -->
    <DropdownMenuPrimitive.RadioGroup
      value={appearance}
      onValueChange={(v) => setMode(v as Appearance)}
      aria-label="Appearance"
      class="px-2 pt-1 pb-2"
    >
      <div class="text-muted-foreground pb-1.5 text-xs font-medium" aria-hidden="true">
        Appearance
      </div>
      <div class="bg-muted grid grid-cols-3 gap-0.5 rounded-[9px] p-0.5">
        {#each appearanceItems as item (item.value)}
          <DropdownMenuPrimitive.RadioItem
            value={item.value}
            closeOnSelect={false}
            class="data-highlighted:ring-ring data-[state=checked]:bg-segmented-thumb flex h-7 cursor-default items-center justify-center rounded-[7px] text-[13px] font-medium outline-none select-none data-highlighted:ring-2 data-[state=checked]:shadow-[0_1px_2px_rgb(0_0_0/0.12),0_0_0_0.5px_rgb(0_0_0/0.04)] pointer-coarse:h-9"
          >
            {item.label}
          </DropdownMenuPrimitive.RadioItem>
        {/each}
      </div>
    </DropdownMenuPrimitive.RadioGroup>

    <DropdownMenu.Separator />
    <DropdownMenu.Item>
      {#snippet child({ props })}
        <a href={settingsHref} {...props}><Settings /> Settings</a>
      {/snippet}
    </DropdownMenu.Item>
    {#if admin}
      <DropdownMenu.Item onSelect={onstorage} class="flex-col items-stretch gap-1.5 py-1.5">
        <span class="flex items-center gap-1.5">
          <HardDrive />
          <span class="flex-1">Storage</span>
          <span class="text-muted-foreground text-xs tabular-nums">{storageLabel}</span>
        </span>
        {#if storageSegments.length}
          {@render storageBar('h-1')}
        {/if}
      </DropdownMenu.Item>
      {#if folders.length}
        <DropdownMenu.Item>
          {#snippet child({ props })}
            <a
              href="/settings?tab=sources"
              title={folders.map((f) => `${f.label}: ${f.path}`).join('\n')}
              {...props}><FolderSync /> {watching}</a
            >
          {/snippet}
        </DropdownMenu.Item>
      {/if}
    {/if}

    <DropdownMenu.Separator />
    <DropdownMenu.Item variant="destructive" onSelect={signOut}>
      <LogOut /> Sign out
    </DropdownMenu.Item>
    <DropdownMenu.Separator />
    {@render versionLine()}
  {/if}
{/if}

{#snippet versionLine()}
  {@const text = version ? `v${version}` : null}
  {#if admin && presentation === 'menu'}
    <DropdownMenu.Item class="text-muted-foreground text-[11px]">
      {#snippet child({ props })}
        <a href="/settings?tab=updates" {...props}
          >MusicHoarder {#if text}<span class="text-primary">{text}</span>{/if} · self-hosted</a
        >
      {/snippet}
    </DropdownMenu.Item>
  {:else if admin}
    <a
      href="/settings?tab=updates"
      onclick={close}
      class="text-footnote text-muted-foreground focus-visible:ring-ring mx-auto flex min-h-11 items-center justify-center rounded-lg px-3 outline-none focus-visible:ring-2"
      >MusicHoarder&nbsp;{#if text}<span class="text-primary">{text}</span>&nbsp;{/if}· self-hosted</a
    >
  {:else}
    <p
      class={cn(
        'text-muted-foreground text-center',
        presentation === 'menu' ? 'px-2 py-1.5 text-[11px]' : 'text-footnote px-4 py-3'
      )}
    >
      MusicHoarder{text ? ` ${text}` : ''} · self-hosted
    </p>
  {/if}
{/snippet}
