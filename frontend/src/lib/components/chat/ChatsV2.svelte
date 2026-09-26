<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { Bell, BellOff, MessageCircle, SquarePen, TriangleAlert, UserRound } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import {
    listAccounts,
    fetchChatPeople,
    sendTestPush,
    startChatConversation,
    type ChatPerson
  } from '$lib/api-client';
  import { isDemo } from '$lib/auth/capabilities';
  import { switchAccountAndReload } from '$lib/auth/switch-account';
  import { conversationTitle, listTimeLabel, messagePreview } from '$lib/chat/chat-text';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { disablePush, pushState, type PushState } from '$lib/push/web-push';
  import { chatStore } from '$lib/stores/chat.svelte';
  import { cn } from '$lib/utils';
  import ChatAvatar from './ChatAvatar.svelte';
  import ChatThread from './ChatThread.svelte';
  import NotificationsRow from './NotificationsRow.svelte';
  import PeoplePicker from './PeoplePicker.svelte';

  // Chats: the conversations with the other people on this MusicHoarder, newest first, and — at
  // /chats/<id> — one of them. A phone shows one at a time (the list is the Chats tab's root, a
  // conversation is pushed on it); md+ shows both side by side, like Messages on a Mac.
  //
  // `?account=` is how a notification says which account it was for: a browser can hold several.
  // When it names a parked account rather than the active one, the page offers to switch.
  type Props = { conversationId?: string | null };
  const { conversationId = null }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);
  const user = $derived(page.data.user);
  const demo = $derived(isDemo(user));

  const conversations = $derived(chatStore.conversations);
  const unread = $derived(chatStore.unreadTotal);
  const headerMeta = $derived(
    !chatStore.loaded || demo ? undefined : unread > 0 ? `${unread} unread` : undefined
  );

  // ── A notification for another account ─────────────────────────────────────────────────────
  const forAccount = $derived(page.url.searchParams.get('account'));
  let otherAccount = $state<{ userId: string; name: string } | null>(null);
  $effect(() => {
    const wanted = forAccount;
    otherAccount = null;
    if (!wanted || !user || wanted === user.id) return;
    void listAccounts()
      .then((accounts) => {
        const match = accounts.find((a) => a.userId === wanted && !a.isActive);
        if (match) otherAccount = { userId: match.userId, name: match.displayName || match.email };
      })
      .catch(() => {});
  });

  // ── Notifications (More menu) ──────────────────────────────────────────────────────────────
  let push = $state<PushState | null>(null);
  $effect(() => {
    if (user && !demo) void pushState(user.id).then((s) => (push = s));
  });

  async function turnOffNotifications() {
    if (!user) return;
    await disablePush(user.id);
    push = 'off';
    toast.success('Notifications are off on this device');
  }

  async function testNotification() {
    try {
      const result = await sendTestPush();
      if (result.delivered === 0) toast.error('No device of yours is set up for notifications yet.');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not send a test notification.');
    }
  }

  // ── New message ────────────────────────────────────────────────────────────────────────────
  let pickerOpen = $state(false);
  let people = $state<ChatPerson[]>([]);
  let peopleLoading = $state(false);

  function newMessage() {
    pickerOpen = true;
    peopleLoading = true;
    fetchChatPeople()
      .then((list) => (people = list))
      .catch(() => (people = []))
      .finally(() => (peopleLoading = false));
  }

  async function startWith(person: ChatPerson) {
    try {
      const conversation = await startChatConversation(person.id);
      pickerOpen = false;
      chatStore.upsert(conversation);
      await goto(`/chats/${conversation.id}`);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not start the conversation.');
    }
  }

  $effect(() => {
    if (!demo) void chatStore.refresh();
  });

  const showList = $derived(!compact || conversationId == null);
  const showThread = $derived(conversationId != null);
</script>

{#snippet list()}
  <div class="min-h-0 flex-1 overflow-y-auto pb-(--mh-content-pad)">
    <PageToolbarV2 title="Chats" meta={headerMeta}>
      {#snippet actions()}
        {#if !demo}
          <Button variant="ghost" size="icon" aria-label="New message" onclick={newMessage}>
            <SquarePen aria-hidden="true" />
          </Button>
        {/if}
      {/snippet}
      {#snippet more()}
        {#if !demo && push === 'on'}
          <DropdownMenu.Item onSelect={testNotification}>
            <Bell /> Send a test notification
          </DropdownMenu.Item>
          <DropdownMenu.Item onSelect={turnOffNotifications}>
            <BellOff /> Turn off notifications here
          </DropdownMenu.Item>
        {/if}
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-3xl flex-col gap-5 pt-2 md:px-4">
      {#if demo}
        <EmptyState
          icon={MessageCircle}
          title="Chat is off for the demo"
          hint="Everyone who tries the demo shares this account, so there is nobody to talk to. On your own MusicHoarder you can send songs to the people you share it with."
        />
      {:else}
        {#if otherAccount}
          {@const target = otherAccount}
          <GroupedList.Section>
            <GroupedList.Row icon={UserRound} label={`That message was for ${target.name}`}>
              {#snippet trailing()}
                <Button
                  variant="gray"
                  class="text-primary h-11 rounded-full md:h-8"
                  onclick={() => void switchAccountAndReload(target.userId)}
                >
                  Switch
                </Button>
              {/snippet}
            </GroupedList.Row>
          </GroupedList.Section>
        {/if}

        {#if user}
          <NotificationsRow userId={user.id} onchange={(s) => (push = s)} />
        {/if}

        {#if !chatStore.loaded}
          <div class="flex flex-col" aria-busy="true">
            {#each Array(5) as _, i (i)}
              <div class="flex items-center gap-3 px-4 py-3">
                <Skeleton class="size-12 shrink-0 rounded-full" />
                <div class="min-w-0 flex-1 space-y-1.5">
                  <Skeleton class="h-4 w-1/3" />
                  <Skeleton class="h-3 w-2/3" />
                </div>
              </div>
            {/each}
          </div>
        {:else if chatStore.failed && conversations.length === 0}
          <EmptyState
            icon={TriangleAlert}
            title="Couldn’t load your chats"
            action={{ label: 'Try again', onclick: () => void chatStore.refresh() }}
          />
        {:else if conversations.length === 0}
          <EmptyState
            icon={MessageCircle}
            title="No chats yet"
            hint="Pick Send to… on a song or an album, or start a new message. Songs from Spotify and YouTube can be shared straight into MusicHoarder too."
            action={{ label: 'New message', onclick: newMessage }}
          />
        {:else}
          <ul class="flex flex-col" aria-label="Conversations">
            {#each conversations as conversation (conversation.id)}
              {@const selected = conversation.id === conversationId}
              {@const unreadHere = conversation.unreadCount > 0}
              <li>
                <a
                  href={`/chats/${conversation.id}`}
                  aria-current={selected ? 'page' : undefined}
                  class={cn(
                    'focus-visible:ring-ring flex items-center gap-3 px-4 py-2.5 outline-none focus-visible:ring-2 focus-visible:ring-inset md:rounded-xl md:px-3',
                    selected ? 'bg-secondary' : 'hover:bg-secondary/0 md:hover:bg-accent'
                  )}
                >
                  <ChatAvatar person={conversation.members[0]} size={compact ? 52 : 44} />
                  <span class="min-w-0 flex-1 border-b py-1 md:border-b-0">
                    <span class="flex items-baseline gap-2">
                      <span class={cn('text-body min-w-0 flex-1 truncate md:text-sm', unreadHere ? 'font-semibold' : 'font-medium')}>
                        {conversationTitle(conversation)}
                      </span>
                      <span class={cn('text-footnote shrink-0 tabular-nums md:text-xs', unreadHere ? 'text-primary' : 'text-muted-foreground')}>
                        {listTimeLabel(conversation.lastMessageAtUtc)}
                      </span>
                    </span>
                    <span class="flex items-center gap-2">
                      <span class={cn('text-subheadline min-w-0 flex-1 truncate md:text-[13px]', unreadHere ? 'text-foreground' : 'text-muted-foreground')}>
                        {messagePreview(conversation.lastMessage)}
                      </span>
                      {#if unreadHere}
                        <span
                          class="bg-primary text-primary-foreground text-caption-2 grid h-5 min-w-5 shrink-0 place-items-center rounded-full px-1.5 font-semibold tabular-nums"
                          aria-label={`${conversation.unreadCount} unread`}
                        >
                          {conversation.unreadCount > 99 ? '99+' : conversation.unreadCount}
                        </span>
                      {/if}
                    </span>
                  </span>
                </a>
              </li>
            {/each}
          </ul>
        {/if}
      {/if}
    </div>
  </div>
{/snippet}

<div class="flex min-h-0 flex-1">
  {#if showList}
    <div class={cn('flex min-h-0 flex-col', showThread || !compact ? 'md:w-[360px] md:shrink-0 md:border-r' : 'flex-1', compact && 'flex-1')}>
      {@render list()}
    </div>
  {/if}
  {#if showThread && conversationId}
    <div class="flex min-h-0 min-w-0 flex-1 flex-col">
      {#key conversationId}
        <ChatThread {conversationId} {compact} />
      {/key}
    </div>
  {:else if !compact}
    <div class="text-muted-foreground hidden min-w-0 flex-1 flex-col items-center justify-center gap-2 md:flex">
      <MessageCircle class="size-8" aria-hidden="true" />
      <p class="text-sm">{demo ? 'Chat is off for the demo' : 'Pick a conversation, or start a new one.'}</p>
    </div>
  {/if}
</div>

<BottomSheet.Root bind:open={pickerOpen} title="New message">
  {#snippet leading()}
    <BottomSheet.Action onclick={() => (pickerOpen = false)}>Cancel</BottomSheet.Action>
  {/snippet}
  <div class="pb-6 md:px-4">
    <PeoplePicker {people} loading={peopleLoading} onpick={startWith} />
  </div>
</BottomSheet.Root>
