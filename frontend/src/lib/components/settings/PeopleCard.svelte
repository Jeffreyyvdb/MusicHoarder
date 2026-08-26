<script lang="ts">
  import { toast } from 'svelte-sonner';
  import {
    Check,
    Copy,
    Loader2,
    Mail,
    RefreshCw,
    Trash2,
    UserRoundPlus,
    Users,
    X
  } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import { Badge } from '$lib/components/ui/badge';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
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
    type FriendGrantView,
    type FriendInviteView,
    type FriendView
  } from '$lib/api-client';
  import { Switch } from '$lib/components/ui/switch';

  /**
   * Owner-side management of friend accounts: mint/rotate/revoke invite links, and manage what
   * each friend can see. The invite URL is only ever visible right after minting (the server
   * stores a hash), so "New link" rotates the token — the previous link stops working.
   */

  let invites = $state<FriendInviteView[]>([]);
  let friends = $state<FriendView[]>([]);
  let isLoading = $state(true);

  // Freshly minted links by invite id — the only place a URL can be shown from.
  let mintedUrls = $state<Record<string, string>>({});
  let copiedId = $state<string | null>(null);

  let inviteEmail = $state('');
  let sendEmail = $state(false);
  let isCreating = $state(false);

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

  async function handleCreateInvite(email: string, opts?: { rotate?: boolean }) {
    isCreating = true;
    try {
      const created = await createFriendInvite(email, sendEmail || undefined);
      if (created.inviteUrl) mintedUrls = { ...mintedUrls, [created.id]: created.inviteUrl };
      if (created.emailSent) toast.success(`Invite emailed to ${created.email}.`);
      else if (created.emailInLogs) toast.info('No email service configured — copy the link instead.');
      else if (opts?.rotate) toast.success('New link created — the old one stopped working.');
      else toast.success('Invite link created — copy it and send it over.');
      inviteEmail = '';
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Could not create the invite.';
      toast.error(
        message.includes('400') ? "That email already belongs to this server's own account." : message
      );
    } finally {
      isCreating = false;
    }
  }

  async function copyLink(inviteId: string) {
    const url = mintedUrls[inviteId];
    if (!url) return;
    await navigator.clipboard.writeText(url);
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

  async function handleRemoveFriend(friend: FriendView) {
    try {
      await removeFriend(friend.id);
      toast.success(`${friend.email} removed. Their sessions and shares are revoked.`);
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not remove the friend.');
    }
  }

  function grantLabel(grant: FriendGrantView): string {
    if (grant.scope === 'Library') return 'Entire library';
    if (grant.scope === 'Artist') return `Artist · ${grant.artist ?? '?'}`;
    return `${grant.artist ?? '?'} — ${grant.album ?? '?'}`;
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

  let capabilityBusy = $state<string | null>(null);

  function has(friend: FriendView, capability: Capability): boolean {
    return (friend.capabilities ?? []).includes(capability);
  }

  async function setCapability(friend: FriendView, capability: Capability, enabled: boolean) {
    // Send the whole desired set, not a delta — matches the endpoint's contract and keeps the
    // request idempotent if it is retried.
    const next = new Set<Capability>(friend.capabilities ?? []);
    if (enabled) next.add(capability);
    else next.delete(capability);

    capabilityBusy = `${friend.id}:${capability}`;
    try {
      const updated = await updatePersonCapabilities(friend.id, [...next]);
      // Patch in place so the switches do not flicker through a full reload.
      friends = friends.map((f) => (f.id === updated.id ? { ...f, ...updated } : f));
      toast.success(enabled ? 'Turned on.' : 'Turned off.');
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Could not change that.';
      toast.error(
        message.includes('last_admin')
          ? 'Someone has to stay an administrator.'
          : message.includes('cannot_change_own_capabilities')
            ? 'You cannot change your own access.'
            : message
      );
      // The server refused, so re-read rather than leaving the switch showing a state it rejected.
      await refresh();
    } finally {
      capabilityBusy = null;
    }
  }
</script>

<section class="border-border bg-card rounded-lg border">
  <header class="border-border border-b px-5 py-3.5">
    <h2 class="flex items-center gap-2 text-sm font-semibold">
      <UserRoundPlus class="size-4" /> Invite a friend
    </h2>
    <p class="text-muted-foreground text-xs">
      Friends get their own listen-only account and see nothing until you share albums, artists, or
      your whole library with them. Your music stays yours.
    </p>
  </header>

  <div class="space-y-4 p-5">
    <form
      class="flex flex-wrap items-end gap-2"
      onsubmit={(e) => {
        e.preventDefault();
        const email = inviteEmail.trim();
        if (email) void handleCreateInvite(email);
      }}
    >
      <div class="min-w-0 flex-1 space-y-2">
        <Label for="invite-email">Friend's email</Label>
        <Input
          id="invite-email"
          type="email"
          placeholder="friend@example.com"
          bind:value={inviteEmail}
          disabled={isCreating}
        />
      </div>
      <label class="text-muted-foreground flex h-9 items-center gap-2 text-xs select-none">
        <input type="checkbox" bind:checked={sendEmail} class="accent-primary size-3.5" />
        Email them the link
      </label>
      <Button type="submit" disabled={isCreating || !inviteEmail.trim()}>
        {#if isCreating}
          <Loader2 class="mr-2 size-4 animate-spin" />
        {:else}
          <Mail class="mr-2 size-4" />
        {/if}
        Create invite
      </Button>
    </form>

    {#if invites.length > 0}
      <ul class="border-border divide-border divide-y rounded-lg border">
        {#each invites as invite (invite.id)}
          <li class="space-y-2 px-4 py-3">
            <div class="flex items-center gap-3">
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm">{invite.email}</div>
                <div class="text-muted-foreground text-xs">
                  Pending · expires {new Date(invite.expiresAtUtc).toLocaleDateString()}
                </div>
              </div>
              <Button
                variant="ghost"
                size="sm"
                title="Mint a fresh link (the old one stops working)"
                onclick={() => handleCreateInvite(invite.email, { rotate: true })}
              >
                <RefreshCw class="size-4" />
                <span class="hidden sm:inline">New link</span>
              </Button>
              <Button variant="ghost" size="sm" onclick={() => handleRevokeInvite(invite)}>
                <Trash2 class="size-4" />
                <span class="hidden sm:inline">Revoke</span>
              </Button>
            </div>
            {#if mintedUrls[invite.id]}
              <div class="flex items-center gap-2">
                <div
                  class="border-border bg-secondary/30 min-w-0 flex-1 truncate rounded-md border px-3 py-2 font-mono text-xs"
                >
                  {mintedUrls[invite.id]}
                </div>
                <Button
                  variant="ghost"
                  size="icon"
                  class="size-8 shrink-0"
                  aria-label="Copy invite link"
                  onclick={() => copyLink(invite.id)}
                >
                  {#if copiedId === invite.id}
                    <Check class="size-4" />
                  {:else}
                    <Copy class="size-4" />
                  {/if}
                </Button>
              </div>
              <p class="text-muted-foreground text-[11px]">
                Shown once — the server keeps only a fingerprint of this link. "New link" replaces
                it.
              </p>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </div>
</section>

<section class="border-border bg-card rounded-lg border">
  <header class="border-border border-b px-5 py-3.5">
    <h2 class="flex items-center gap-2 text-sm font-semibold">
      <Users class="size-4" /> Friends
    </h2>
    <p class="text-muted-foreground text-xs">
      Who has an account, and what each of them can play. Revoking a share hides it from them on
      their next refresh.
    </p>
  </header>

  <div class="p-5">
    {#if isLoading}
      <div class="flex items-center justify-center py-8">
        <Loader2 class="text-muted-foreground size-5 animate-spin" />
      </div>
    {:else if friends.length === 0}
      <p class="text-muted-foreground text-sm">
        No friends yet — invite someone above, and once they accept they'll show up here.
      </p>
    {:else}
      <ul class="space-y-5">
        {#each friends as friend (friend.id)}
          <li class="border-border rounded-lg border p-4">
            <div class="flex items-center gap-3">
              <div
                class="flex size-9 items-center justify-center rounded-full bg-gradient-to-br from-emerald-500/80 to-cyan-500/80 text-xs font-semibold text-white"
              >
                {(friend.displayName ?? friend.email).slice(0, 2).toUpperCase()}
              </div>
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium">
                  {friend.displayName ?? friend.email}
                </div>
                <div class="text-muted-foreground truncate text-xs">
                  {friend.email}
                  {#if friend.lastLoginAtUtc}
                    · last seen {new Date(friend.lastLoginAtUtc).toLocaleDateString()}
                  {/if}
                </div>
              </div>
              {#if friend.isAdmin}
                <Badge>Admin</Badge>
              {/if}
              {#if friend.isDisabled}
                <Badge variant="secondary">Removed</Badge>
              {/if}
              <AlertDialog.Root>
                <AlertDialog.Trigger>
                  {#snippet child({ props })}
                    <Button {...props} variant="ghost" size="sm" disabled={friend.isDisabled}>
                      <Trash2 class="size-4" />
                      <span class="hidden sm:inline">Remove</span>
                    </Button>
                  {/snippet}
                </AlertDialog.Trigger>
                <AlertDialog.Content>
                  <AlertDialog.Header>
                    <AlertDialog.Title>Remove {friend.email}?</AlertDialog.Title>
                    <AlertDialog.Description>
                      Their account is disabled, their signed-in devices are logged out, and
                      everything you shared with them is revoked. A fresh invite for the same email
                      brings them back.
                    </AlertDialog.Description>
                  </AlertDialog.Header>
                  <AlertDialog.Footer>
                    <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                    <AlertDialog.Action onclick={() => handleRemoveFriend(friend)}>
                      Remove friend
                    </AlertDialog.Action>
                  </AlertDialog.Footer>
                </AlertDialog.Content>
              </AlertDialog.Root>
            </div>

            {#if !friend.isDisabled}
              <div class="border-border/60 mt-3 space-y-2 border-t pt-3">
                <div class="text-muted-foreground text-xs font-medium">What they can do</div>
                {#each CAPABILITIES as capability (capability.key)}
                  <label class="flex items-start justify-between gap-3">
                    <span class="min-w-0">
                      <span class="block text-xs font-medium">{capability.label}</span>
                      <span class="text-muted-foreground block text-xs">{capability.hint}</span>
                    </span>
                    <Switch
                      checked={has(friend, capability.key)}
                      disabled={capabilityBusy === `${friend.id}:${capability.key}`}
                      onCheckedChange={(checked) => setCapability(friend, capability.key, checked)}
                      aria-label={`${capability.label} for ${friend.email}`}
                    />
                  </label>
                {/each}
                {#if friend.isAdmin}
                  <p class="text-muted-foreground text-xs">
                    Administrators have every permission — the switches above stay on until you
                    turn Administrator off.
                  </p>
                {/if}
              </div>

              <div class="mt-3 flex flex-wrap gap-1.5">
                {#each friend.grants as grant (grant.id)}
                  <span
                    class="border-border bg-secondary/40 inline-flex items-center gap-1 rounded-full border py-1 pr-1 pl-2.5 text-xs"
                  >
                    {grantLabel(grant)}
                    <button
                      type="button"
                      class="hover:bg-secondary text-muted-foreground hover:text-foreground rounded-full p-0.5"
                      aria-label={`Stop sharing ${grantLabel(grant)}`}
                      onclick={() => handleRevokeGrant(friend, grant)}
                    >
                      <X class="size-3" />
                    </button>
                  </span>
                {:else}
                  <span class="text-muted-foreground text-xs">Nothing shared yet.</span>
                {/each}
              </div>

              <div class="mt-3 flex flex-wrap items-center gap-2">
                <select
                  class="border-input bg-background h-8 rounded-md border px-2 text-xs"
                  value={scopeOf(friend.id)}
                  onchange={(e) =>
                    (grantScope = { ...grantScope, [friend.id]: e.currentTarget.value as GrantScope })}
                >
                  <option value="album">Share an album</option>
                  <option value="artist">Share an artist</option>
                  <option value="library">Share entire library</option>
                </select>
                {#if scopeOf(friend.id) !== 'library'}
                  <Input
                    class="h-8 w-40 text-xs"
                    placeholder="Artist"
                    value={grantArtist[friend.id] ?? ''}
                    oninput={(e) =>
                      (grantArtist = { ...grantArtist, [friend.id]: e.currentTarget.value })}
                  />
                {/if}
                {#if scopeOf(friend.id) === 'album'}
                  <Input
                    class="h-8 w-44 text-xs"
                    placeholder="Album"
                    value={grantAlbum[friend.id] ?? ''}
                    oninput={(e) =>
                      (grantAlbum = { ...grantAlbum, [friend.id]: e.currentTarget.value })}
                  />
                {/if}
                <Button
                  size="sm"
                  variant="outline"
                  class="h-8"
                  disabled={grantBusy === friend.id}
                  onclick={() => handleAddGrant(friend)}
                >
                  {#if grantBusy === friend.id}
                    <Loader2 class="size-3.5 animate-spin" />
                  {/if}
                  Share
                </Button>
              </div>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </div>
</section>
