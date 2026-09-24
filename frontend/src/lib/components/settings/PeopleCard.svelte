<script module lang="ts">
  import type { FriendInviteView, FriendView } from '$lib/api-client';

  // The last lists this component loaded. The People list and each person are separate pages (a
  // push remounts it), so a page opens on what was just shown and refreshes behind it instead of
  // flashing a spinner on every push and Back. Minted invite links ride along: they are shown
  // once, and a trip into a person and back must not lose one. The hard reload behind every
  // account switch and sign-out clears it.
  let cache: {
    invites: FriendInviteView[];
    friends: FriendView[];
    mintedUrls: Record<string, string>;
  } | null = null;
</script>

<script lang="ts">
  import { toast } from 'svelte-sonner';
  import { Check, Copy, Ellipsis, Loader2, Mail, RefreshCw, Trash2, X } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Badge } from '$lib/components/ui/badge';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import FieldRow from '$lib/components/settings/FieldRow.svelte';
  import SwitchRow from '$lib/components/settings/SwitchRow.svelte';
  import { COPY_FAILED_MESSAGE, copyText } from '$lib/components/settings/copy-text';
  import { createOptimisticCommitter, sameSet } from '$lib/components/settings/optimistic';
  import { personHref } from '$lib/components/settings/sections';
  import { avatar } from '$lib/components/v2/AccountPanel.svelte';
  import { formatDate } from '$lib/formatters';
  import {
    createFriendGrant,
    createFriendInvite,
    listFriendInvites,
    listFriends,
    removeFriend,
    revokeFriendGrant,
    revokeFriendInvite,
    updatePersonCapabilities,
    type Capability,
    type FriendGrantView
  } from '$lib/api-client';

  /**
   * Admin-side management of people: mint/rotate/revoke invite links, and manage what each
   * person can see and do. The invite URL is only ever visible right after minting (the server
   * stores a hash), so "New link" rotates the token — the previous link stops working.
   *
   * Two pages, like Settings › a list › its detail on iOS:
   * - the People list (`personId` null): the invite form, the pending invites, then one row per
   *   person that pushes their page;
   * - one person (`?tab=people&person=<id>`): what they may do (capability switches), what is
   *   shared with them and how to share more, and — last, in red — removing them.
   * It used to be one page with every person's switches and share form inline, about 900px per
   * person on a phone. Secondary row actions sit behind a labelled ••• menu or a 44pt button with
   * a spoken name — never an icon-only button whose label is hidden on a phone.
   */
  type Props = {
    /** The person whose page to show; null shows the People list. */
    personId?: string | null;
    /** Out: the shown person's name, for the nav bar (null on the list, or until loaded). */
    title?: string | null;
  };

  let { personId = null, title = $bindable(null) }: Props = $props();

  let invites = $state<FriendInviteView[]>(cache?.invites ?? []);
  let friends = $state<FriendView[]>(cache?.friends ?? []);
  let isLoading = $state(cache === null);

  // Freshly minted links by invite id — the only place a URL can be shown from.
  let mintedUrls = $state<Record<string, string>>(cache?.mintedUrls ?? {});
  let copiedId = $state<string | null>(null);

  let inviteEmail = $state('');
  let sendEmail = $state(false);
  let isCreating = $state(false);

  $effect(() => {
    if (isLoading) return;
    cache = {
      invites: $state.snapshot(invites),
      friends: $state.snapshot(friends),
      mintedUrls: $state.snapshot(mintedUrls)
    };
  });

  async function refresh() {
    try {
      const [i, f] = await Promise.all([listFriendInvites(), listFriends()]);
      invites = i;
      friends = f;
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not load people.');
    } finally {
      isLoading = false;
    }
  }

  $effect(() => {
    void refresh();
  });

  function nameOf(friend: FriendView): string {
    return friend.displayName?.trim() || friend.email;
  }

  const person = $derived(personId ? (friends.find((f) => f.id === personId) ?? null) : null);

  $effect(() => {
    title = person ? nameOf(person) : null;
  });

  /**
   * Mint an invite link — a new one, or a fresh token for a pending invite ("New link"). Whether
   * it is emailed is always said by the caller: a rotation must not quietly inherit the invite
   * form's switch, which now sits in another section from the menu that rotates.
   */
  async function handleCreateInvite(email: string, opts: { sendEmail: boolean; rotate?: boolean }) {
    isCreating = true;
    try {
      const created = await createFriendInvite(email, opts.sendEmail || undefined);
      if (created.inviteUrl) mintedUrls = { ...mintedUrls, [created.id]: created.inviteUrl };
      if (created.emailSent) toast.success(`Invite emailed to ${created.email}.`);
      else if (created.emailInLogs)
        toast.info('No email service configured — copy the link instead.');
      else if (opts.rotate) toast.success('New link created — the old one stopped working.');
      else toast.success('Invite link created — copy it and send it over.');
      if (!opts.rotate) inviteEmail = '';
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Could not create the invite.';
      toast.error(
        message.includes('400')
          ? "That email already belongs to this server's own account."
          : message
      );
    } finally {
      isCreating = false;
    }
  }

  function submitInvite() {
    const email = inviteEmail.trim();
    if (email && !isCreating) void handleCreateInvite(email, { sendEmail });
  }

  async function copyLink(inviteId: string) {
    const url = mintedUrls[inviteId];
    if (!url) return;
    if (!(await copyText(url))) {
      toast.error(COPY_FAILED_MESSAGE);
      return;
    }
    copiedId = inviteId;
    setTimeout(() => (copiedId = null), 2000);
  }

  async function handleRevokeInvite(invite: FriendInviteView) {
    try {
      await revokeFriendInvite(invite.id);
      toast.success(`Invite for ${invite.email} revoked.`);
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not revoke the invite.');
    }
  }

  // ── grants ─────────────────────────────────────────────────────────────────
  type GrantScope = 'album' | 'artist' | 'library';
  let grantScope = $state<Record<string, GrantScope>>({});
  let grantArtist = $state<Record<string, string>>({});
  let grantAlbum = $state<Record<string, string>>({});
  let grantBusy = $state<string | null>(null);

  function scopeOf(friendId: string): GrantScope {
    return grantScope[friendId] ?? 'album';
  }

  async function handleAddGrant(friend: FriendView) {
    const scope = scopeOf(friend.id);
    const artist = (grantArtist[friend.id] ?? '').trim();
    const album = (grantAlbum[friend.id] ?? '').trim();
    if (scope !== 'library' && !artist) {
      toast.error('Enter the artist name.');
      return;
    }
    if (scope === 'album' && !album) {
      toast.error('Enter the album title.');
      return;
    }
    grantBusy = friend.id;
    try {
      await createFriendGrant(friend.id, {
        scope,
        artist: scope === 'library' ? undefined : artist,
        album: scope === 'album' ? album : undefined
      });
      grantArtist = { ...grantArtist, [friend.id]: '' };
      grantAlbum = { ...grantAlbum, [friend.id]: '' };
      toast.success('Shared.');
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not share that.');
    } finally {
      grantBusy = null;
    }
  }

  async function handleRevokeGrant(friend: FriendView, grant: FriendGrantView) {
    try {
      await revokeFriendGrant(friend.id, grant.id);
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not revoke that grant.');
    }
  }

  // Controlled, and closed by the action before its request starts, so the confirm never stays
  // up over the page it has just changed.
  let removeOpen = $state(false);

  async function handleRemoveFriend(friend: FriendView) {
    try {
      await removeFriend(friend.id);
      toast.success(`${friend.email} removed. Their sessions and shares are revoked.`);
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not remove that person.');
    }
  }

  function grantLabel(grant: FriendGrantView): string {
    if (grant.scope === 'Library') return 'Entire library';
    if (grant.scope === 'Artist') return `Artist · ${grant.artist ?? '?'}`;
    return `${grant.artist ?? '?'} — ${grant.album ?? '?'}`;
  }

  // "Sep 22" (with the year only outside this one): short enough that a row's summary stays on
  // one line beside a badge on a phone. The person's own page, with room to spare, uses the full
  // formatDate.
  function shortDate(iso: string): string {
    const date = new Date(iso);
    const thisYear = date.getFullYear() === new Date().getFullYear();
    return date.toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
      year: thisYear ? undefined : 'numeric'
    });
  }

  // A list row's last line: where each person stands, short enough for one line on a phone.
  // Everything else is one tap away, on their page.
  function summaryOf(friend: FriendView): string {
    const parts: string[] = [];
    if (!friend.isDisabled) {
      const n = friend.grants.length;
      if (friend.grants.some((g) => g.scope === 'Library')) parts.push('Entire library');
      else parts.push(n === 0 ? 'Nothing shared' : n === 1 ? '1 share' : `${n} shares`);
    }
    if (friend.lastLoginAtUtc) parts.push(`last seen ${shortDate(friend.lastLoginAtUtc)}`);
    const line = parts.join(' · ');
    return line.charAt(0).toUpperCase() + line.slice(1);
  }

  // ── capabilities ───────────────────────────────────────────────────────────
  /**
   * What each switch means, in the person's terms rather than the enum's. Order is deliberate:
   * the two everyday toggles first, then re-sharing, then the one that hands over the instance.
   */
  const CAPABILITIES: { key: Capability; label: string; hint: string }[] = [
    {
      key: 'TrackListening',
      label: 'Likes and play history',
      hint: 'Keeps their own likes and play counts. Never touches yours.'
    },
    {
      key: 'DownloadMusic',
      label: 'Request downloads',
      hint: 'Not wired up yet — the download pipeline is still yours alone.'
    },
    {
      key: 'ManageOwnShares',
      label: 'Re-share what they have',
      hint: 'Lets them pass along music you shared with them.'
    },
    {
      key: 'Administer',
      label: 'Administrator',
      hint: 'Full access: invites, capabilities, and the whole pipeline.'
    }
  ];

  // Writes in flight, by `${personId}:${capability}`.
  let capabilityBusy = $state<Record<string, boolean>>({});
  // One person's capability writes each send the whole set, so they must land in the order they
  // were made or a slow earlier set could overwrite a later one.
  let capabilityQueue: Promise<unknown> = Promise.resolve();
  // Per person: the switches move at once, and the server's answer is applied only once no later
  // flip for that person is still saving (optimistic.ts).
  const commitCapabilities = createOptimisticCommitter<Capability[]>(sameSet);

  function has(friend: FriendView, capability: Capability): boolean {
    return (friend.capabilities ?? []).includes(capability);
  }

  function capabilitiesOf(id: string): Capability[] {
    return friends.find((f) => f.id === id)?.capabilities ?? [];
  }

  async function setCapability(friend: FriendView, capability: Capability, enabled: boolean) {
    // Send the whole desired set, not a delta — matches the endpoint's contract and keeps the
    // request idempotent if it is retried. Built from the current row, which already holds any
    // earlier flip that is still saving.
    const next = new Set<Capability>(capabilitiesOf(friend.id));
    if (enabled) next.add(capability);
    else next.delete(capability);
    const desired = [...next];

    const key = `${friend.id}:${capability}`;
    capabilityBusy = { ...capabilityBusy, [key]: true };
    const run = capabilityQueue.then(async () => {
      const updated = await updatePersonCapabilities(friend.id, desired);
      // Patched in place (no reload flicker), all but the switches: an answer to an earlier flip
      // lacks any later one still saving, and would turn that switch off under the person's
      // finger. The committer applies the server's set once the last write for them is in.
      friends = friends.map((f) =>
        f.id === updated.id ? { ...f, ...updated, capabilities: f.capabilities } : f
      );
      return updated.capabilities ?? desired;
    });
    capabilityQueue = run.catch(() => {});

    // The switch moves now (and comes back if refused). No success toast: the switch's own
    // position already says it, and flipping several in a row would stack identical toasts.
    const err = await commitCapabilities(friend.id, {
      read: () => capabilitiesOf(friend.id),
      write: (caps) => {
        friends = friends.map((f) => (f.id === friend.id ? { ...f, capabilities: caps } : f));
      },
      next: desired,
      save: () => run
    });
    if (err) {
      const message = err instanceof Error ? err.message : 'Could not change that.';
      toast.error(
        message.includes('last_admin')
          ? 'Someone has to stay an administrator.'
          : message.includes('cannot_change_own_capabilities')
            ? 'You cannot change your own access.'
            : message
      );
      // The server refused, so re-read rather than trusting the local revert alone.
      await refresh();
    }
    capabilityBusy = { ...capabilityBusy, [key]: false };
  }
</script>

{#snippet badges(friend: FriendView)}
  <span class="flex items-center gap-1.5">
    <!-- Grey like "Removed": a role is a label, and green text is kept for things you can tap. -->
    {#if friend.isAdmin}
      <Badge variant="secondary">Admin</Badge>
    {/if}
    {#if friend.isDisabled}
      <Badge variant="secondary">Removed</Badge>
    {/if}
  </span>
{/snippet}

{#snippet loadingRow(label: string)}
  <GroupedList.Row {label}>
    {#snippet trailing()}
      <Loader2 class="text-muted-foreground size-5 animate-spin" aria-hidden="true" />
    {/snippet}
  </GroupedList.Row>
{/snippet}

{#if personId}
  <!-- ── One person ────────────────────────────────────────────────────────── -->
  {#if !person}
    <GroupedList.Section
      footer={isLoading ? undefined : 'They may have been removed from this server’s list.'}
    >
      {#if isLoading}
        {@render loadingRow('Loading…')}
      {:else}
        <GroupedList.Row label="This person isn’t on this server" disabled />
      {/if}
    </GroupedList.Section>
  {:else}
    {@const friend = person}
    {@const name = nameOf(friend)}
    <!-- Who they are: the nav bar carries the name, so the cell carries how to reach them. -->
    <GroupedList.Section
      footer={friend.isDisabled
        ? 'Their access was removed: their devices are signed out and nothing is shared with them. A fresh invite brings them back.'
        : undefined}
    >
      <GroupedList.Row
        label={friend.email}
        sublabel={friend.lastLoginAtUtc
          ? `Last seen ${formatDate(friend.lastLoginAtUtc)}`
          : 'Hasn’t signed in yet'}
      >
        {#snippet leading()}
          {@render avatar(name, friend.email, 'size-12 text-headline')}
        {/snippet}
        {#snippet trailing()}
          {@render badges(friend)}
        {/snippet}
      </GroupedList.Row>
    </GroupedList.Section>

    {#if !friend.isDisabled}
      <!-- Read from the switch itself rather than friend.isAdmin, so the other three settle the
           moment Administrator is flipped, not when the server answers. -->
      {@const administers = has(friend, 'Administer')}
      <GroupedList.Section headingLevel={2}
        header="Permissions"
        footer={administers
          ? 'Administrators have every permission, so the others stay on until you turn Administrator off.'
          : undefined}
      >
        {#each CAPABILITIES as capability (capability.key)}
          {@const key = `${friend.id}:${capability.key}`}
          <!-- An administrator holds every other permission by implication: those switches show
               on and are held (the footer says why) instead of offering a flip the server would
               undo. -->
          {@const implied = administers && capability.key !== 'Administer'}
          <SwitchRow
            label={capability.label}
            sublabel={capability.hint}
            checked={implied || has(friend, capability.key)}
            disabled={implied || (capabilityBusy[key] ?? false)}
            busy={capabilityBusy[key] ?? false}
            onCheckedChange={(checked) => void setCapability(friend, capability.key, checked)}
            ariaLabel={`${capability.label} for ${friend.email}`}
          />
        {/each}
      </GroupedList.Section>

      <!-- What is shared with them, and how to share more. -->
      <GroupedList.Section headingLevel={2}
        header="Shared with {name}"
        footer="Stopping a share hides it from them on their next refresh."
      >
        {#each friend.grants as grant (grant.id)}
          <GroupedList.Row label={grantLabel(grant)}>
            {#snippet trailing()}
              <Button
                variant="ghost"
                size="icon"
                class="text-muted-foreground -my-2 -mr-2 rounded-full pointer-coarse:size-11"
                aria-label={`Stop sharing ${grantLabel(grant)} with ${friend.email}`}
                title="Stop sharing"
                onclick={() => handleRevokeGrant(friend, grant)}
              >
                <X class="size-[18px]" />
              </Button>
            {/snippet}
          </GroupedList.Row>
        {:else}
          <GroupedList.Row label="Nothing shared yet" disabled />
        {/each}

        <!-- Labels above the fields rather than placeholders, which vanish on the first
             keystroke. Fields are 16px below md so iOS doesn't zoom the page on focus. -->
        <FieldRow label="Share" for="grant-scope-{friend.id}">
          <select
            id="grant-scope-{friend.id}"
            class="bg-input focus-visible:ring-ring/50 h-11 w-full rounded-lg border border-transparent px-3 text-base outline-none focus-visible:ring-3 md:h-8 md:px-2.5 md:text-sm"
            value={scopeOf(friend.id)}
            onchange={(e) =>
              (grantScope = { ...grantScope, [friend.id]: e.currentTarget.value as GrantScope })}
          >
            <option value="album">An album</option>
            <option value="artist">An artist</option>
            <option value="library">The entire library</option>
          </select>
        </FieldRow>
        {#if scopeOf(friend.id) !== 'library'}
          <FieldRow label="Artist" for="grant-artist-{friend.id}">
            <Input
              id="grant-artist-{friend.id}"
              autocomplete="off"
              enterkeyhint="next"
              value={grantArtist[friend.id] ?? ''}
              oninput={(e) =>
                (grantArtist = { ...grantArtist, [friend.id]: e.currentTarget.value })}
            />
          </FieldRow>
        {/if}
        {#if scopeOf(friend.id) === 'album'}
          <FieldRow label="Album" for="grant-album-{friend.id}">
            <Input
              id="grant-album-{friend.id}"
              autocomplete="off"
              enterkeyhint="done"
              value={grantAlbum[friend.id] ?? ''}
              oninput={(e) => (grantAlbum = { ...grantAlbum, [friend.id]: e.currentTarget.value })}
            />
          </FieldRow>
        {/if}
        <GroupedList.Row onclick={() => handleAddGrant(friend)} disabled={grantBusy === friend.id}>
          <span class="text-body text-primary md:text-sm">Share with {name}</span>
          {#snippet trailing()}
            {#if grantBusy === friend.id}
              <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
            {/if}
          {/snippet}
        </GroupedList.Row>
      </GroupedList.Section>

      <!-- Last, in its own section, in red. -->
      <GroupedList.Section>
        <AlertDialog.Root bind:open={removeOpen}>
          <AlertDialog.Trigger>
            {#snippet child({ props })}
              <GroupedList.Row {...props} label="Remove access…" destructive />
            {/snippet}
          </AlertDialog.Trigger>
          <AlertDialog.Content>
            <AlertDialog.Header>
              <AlertDialog.Title>Remove {friend.email}?</AlertDialog.Title>
              <AlertDialog.Description>
                Their account is disabled, their devices are signed out and everything you shared is
                revoked. A fresh invite brings them back.
              </AlertDialog.Description>
            </AlertDialog.Header>
            <AlertDialog.Footer>
              <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
              <AlertDialog.Action
                variant="destructive"
                onclick={() => {
                  removeOpen = false;
                  void handleRemoveFriend(friend);
                }}
              >
                Remove access
              </AlertDialog.Action>
            </AlertDialog.Footer>
          </AlertDialog.Content>
        </AlertDialog.Root>
      </GroupedList.Section>
    {/if}
  {/if}
{:else}
  <!-- ── Invite ─────────────────────────────────────────────────────────────── -->
  <form
    novalidate
    onsubmit={(e) => {
      e.preventDefault();
      submitInvite();
    }}
  >
    <GroupedList.Section headingLevel={2}
      header="Invite someone"
      footer="They get their own listen-only account and see nothing until you share albums, artists or your whole library with them. Your music stays yours."
    >
      <FieldRow label="Email" for="invite-email">
        <Input
          id="invite-email"
          type="email"
          autocomplete="off"
          enterkeyhint="send"
          placeholder="name@example.com"
          bind:value={inviteEmail}
          disabled={isCreating}
        />
      </FieldRow>
      <!-- A switch row, not a 14px checkbox: the whole row is the target. -->
      <SwitchRow
        label="Email the invite link"
        checked={sendEmail}
        disabled={isCreating}
        onCheckedChange={(v) => (sendEmail = v)}
      />
      <GroupedList.Row onclick={submitInvite} disabled={isCreating || !inviteEmail.trim()}>
        <span
          class="text-body md:text-sm {isCreating || !inviteEmail.trim()
            ? 'text-muted-foreground-dim'
            : 'text-primary'}">Create invite</span
        >
        {#snippet trailing()}
          {#if isCreating}
            <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
          {/if}
        {/snippet}
      </GroupedList.Row>
    </GroupedList.Section>
  </form>

  <!-- ── Pending invites ─────────────────────────────────────────────────────── -->
  {#if invites.length > 0}
    <GroupedList.Section headingLevel={2} header="Pending invites">
      {#each invites as invite (invite.id)}
        <GroupedList.Row
          label={invite.email}
          sublabel="Expires {formatDate(invite.expiresAtUtc)}"
        >
          {#snippet trailing()}
            <DropdownMenu.Root>
              <DropdownMenu.Trigger>
                {#snippet child({ props })}
                  <Button
                    {...props}
                    variant="ghost"
                    size="icon"
                    class="-my-2 -mr-2 pointer-coarse:size-11"
                    aria-label="Actions for the invite to {invite.email}"
                  >
                    <Ellipsis class="size-5" />
                  </Button>
                {/snippet}
              </DropdownMenu.Trigger>
              <DropdownMenu.Content align="end" class="min-w-56">
                <!-- Two rotations, each saying whether it emails: the invite form's switch is
                     for new invites only. -->
                <DropdownMenu.Item
                  onclick={() =>
                    handleCreateInvite(invite.email, { sendEmail: false, rotate: true })}
                >
                  <RefreshCw />
                  New link
                </DropdownMenu.Item>
                <DropdownMenu.Item
                  onclick={() =>
                    handleCreateInvite(invite.email, { sendEmail: true, rotate: true })}
                >
                  <Mail />
                  Email a new link
                </DropdownMenu.Item>
                <DropdownMenu.Separator />
                <DropdownMenu.Item variant="destructive" onclick={() => handleRevokeInvite(invite)}>
                  <Trash2 />
                  Revoke invite
                </DropdownMenu.Item>
              </DropdownMenu.Content>
            </DropdownMenu.Root>
          {/snippet}
        </GroupedList.Row>
        {#if mintedUrls[invite.id]}
          <FieldRow
            label="Invite link"
            hint="Shown once — the server keeps only a fingerprint of this link. New link replaces it."
          >
            <div class="flex items-center gap-1">
              <p class="text-body min-w-0 flex-1 truncate font-mono select-all md:text-xs">
                {mintedUrls[invite.id]}
              </p>
              <Button
                variant="ghost"
                size="icon"
                class="shrink-0 pointer-coarse:size-11"
                aria-label={copiedId === invite.id
                  ? 'Copied'
                  : `Copy the invite link for ${invite.email}`}
                onclick={() => copyLink(invite.id)}
              >
                {#if copiedId === invite.id}
                  <Check class="text-primary size-4" />
                {:else}
                  <Copy class="size-4" />
                {/if}
              </Button>
            </div>
          </FieldRow>
        {/if}
      {/each}
    </GroupedList.Section>
  {/if}

  <!-- ── People ──────────────────────────────────────────────────────────────── -->
  {#if isLoading}
    <GroupedList.Section headingLevel={2} header="People">
      {@render loadingRow('Loading people…')}
    </GroupedList.Section>
  {:else if friends.length === 0}
    <GroupedList.Section headingLevel={2}
      header="People"
      footer="Invite someone above; once they accept, they show up here."
    >
      <GroupedList.Row label="No one yet" disabled />
    </GroupedList.Section>
  {:else}
    <GroupedList.Section headingLevel={2}
      header="People"
      footer="Open someone to choose what they can do, share music with them, or remove their access."
    >
      {#each friends as friend (friend.id)}
        {@const name = nameOf(friend)}
        <!-- The email under a display name: it is what they sign in with, and the one thing
             that tells two people with the same name apart without opening each. (With no
             display name the email already is the title.) -->
        <GroupedList.Row
          href={personHref(friend.id)}
          label={name}
          sublabel={name !== friend.email ? friend.email : undefined}
          chevron
        >
          <span class="text-subheadline text-muted-foreground md:text-xs">{summaryOf(friend)}</span>
          {#snippet leading()}
            {@render avatar(name, friend.email, 'size-10 text-[15px]')}
          {/snippet}
          {#snippet trailing()}
            {@render badges(friend)}
          {/snippet}
        </GroupedList.Row>
      {/each}
    </GroupedList.Section>
  {/if}
{/if}
