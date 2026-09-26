<script lang="ts">
  import { tick, untrack } from 'svelte';
  import { page } from '$app/state';
  import { ArrowUp, Loader2, MessageCircle, TriangleAlert } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import AddFromUrlDialog from '$lib/components/v2/AddFromUrlDialog.svelte';
  import { Button } from '$lib/components/ui/button';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import {
    ApiError,
    fetchChatConversation,
    fetchChatMessages,
    sendChatMessage,
    type ChatConversation,
    type ChatMessage
  } from '$lib/api-client';
  import {
    continuesRun,
    conversationTitle,
    dayLabel,
    mergeMessages,
    seenMessageId,
    startsDay
  } from '$lib/chat/chat-text';
  import { isAdmin } from '$lib/auth/capabilities';
  import { bottomBar } from '$lib/stores/bottom-bar.svelte';
  import { chatStore } from '$lib/stores/chat.svelte';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { cn } from '$lib/utils';
  import ChatMessageView from './ChatMessageView.svelte';

  // One conversation: its messages, oldest at the top, and the composer.
  //
  // On a phone the composer takes the tab bar's slot (the Inbox decision toolbar's arrangement, and
  // Messages' own: a pushed conversation hides the tab bar), and rides up on the keyboard. WebKit has
  // no `interactive-widget`, so the keyboard is measured off the visual viewport while it is open.
  // On md+ it sits in flow under the messages.
  //
  // New messages arrive over the chat stream (the chat store relays its events): the conversation
  // reads what came after its last message and marks it read while it is on screen, which is also
  // what keeps the push notification from firing on your other devices.
  type Props = { conversationId: string; compact: boolean };
  const { conversationId, compact }: Props = $props();

  const user = $derived(page.data.user);
  const admin = $derived(isAdmin(user));

  let conversation = $state<ChatConversation | null>(null);
  let messages = $state<ChatMessage[]>([]);
  let hasMore = $state(false);
  let loading = $state(true);
  let loadingOlder = $state(false);
  let notFound = $state(false);
  let error = $state<string | null>(null);
  let text = $state('');
  let sending = $state(false);
  let scroller = $state<HTMLElement | null>(null);
  let field = $state<HTMLTextAreaElement | null>(null);
  let addUrl = $state<string | null>(null);
  let addOpen = $state(false);

  const title = $derived(conversation ? conversationTitle(conversation) : 'Chat');
  const meta = $derived(admin ? (conversation?.members[0]?.email ?? undefined) : undefined);
  const seenId = $derived(seenMessageId(messages, conversation?.peerLastReadAtUtc));

  $effect(() => {
    if (conversation && tabMemory.titleOf(page.url) !== title) tabMemory.setTitle(page.url, title);
  });

  $effect(() => {
    const id = conversationId;
    untrack(() => void openConversation(id));
  });

  async function openConversation(id: string) {
    conversation = null;
    messages = [];
    hasMore = false;
    loading = true;
    notFound = false;
    error = null;
    try {
      const [conv, firstPage] = await Promise.all([fetchChatConversation(id), fetchChatMessages(id)]);
      if (id !== conversationId) return;
      conversation = conv;
      messages = firstPage.messages;
      hasMore = firstPage.hasMore;
      chatStore.upsert({ ...conv, unreadCount: 0 });
      await tick();
      scrollToBottom();
      markRead();
    } catch (e) {
      if (id !== conversationId) return;
      if (e instanceof ApiError && (e.status === 404 || e.status === 403)) notFound = true;
      else error = e instanceof Error ? e.message : 'Could not load this conversation.';
    } finally {
      if (id === conversationId) loading = false;
    }
  }

  function nearBottom(): boolean {
    const el = scroller;
    return !el || el.scrollHeight - el.scrollTop - el.clientHeight < 160;
  }

  function scrollToBottom() {
    if (scroller) scroller.scrollTop = scroller.scrollHeight;
  }

  function markRead() {
    if (typeof document !== 'undefined' && document.visibilityState !== 'visible') return;
    if (!conversation) return;
    void chatStore.markRead(conversationId);
  }

  /** What arrived after the newest message shown. */
  async function catchUp() {
    const id = conversationId;
    const last = messages.at(-1)?.id;
    const stick = nearBottom();
    const next = await fetchChatMessages(id, last != null ? { after: last } : {});
    if (id !== conversationId || next.messages.length === 0) return;
    messages = mergeMessages(messages, next.messages);
    if (stick) {
      await tick();
      scrollToBottom();
    }
    if (next.messages.some((m) => !m.mine)) markRead();
  }

  $effect(() =>
    chatStore.onEvent((event) => {
      if (loading || notFound) return;
      if (event.type === 'ready' || (event.type === 'message' && event.conversationId === conversationId)) {
        void catchUp().catch(() => {});
      } else if (
        event.type === 'read' &&
        event.conversationId === conversationId &&
        event.userId !== page.data.user?.id &&
        conversation
      ) {
        conversation = { ...conversation, peerLastReadAtUtc: event.lastReadAtUtc };
      }
    })
  );

  // Back to the app with the conversation open: what came in meanwhile is now read.
  $effect(() => {
    const onVisible = () => {
      if (document.visibilityState === 'visible') markRead();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  });

  async function loadOlder() {
    const first = messages[0]?.id;
    if (first == null || loadingOlder) return;
    loadingOlder = true;
    const el = scroller;
    const fromBottom = el ? el.scrollHeight - el.scrollTop : 0;
    try {
      const older = await fetchChatMessages(conversationId, { before: first });
      messages = mergeMessages(older.messages, messages);
      hasMore = older.hasMore;
      await tick();
      // Keep the message you were reading where it was.
      if (el) el.scrollTop = el.scrollHeight - fromBottom;
    } catch {
      toast.error('Could not load earlier messages.');
    } finally {
      loadingOlder = false;
    }
  }

  function resizeField() {
    const el = field;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 160)}px`;
  }

  async function send(event?: Event) {
    event?.preventDefault();
    const body = text.trim();
    if (!body || sending) return;
    sending = true;
    try {
      const sent = await sendChatMessage(conversationId, { text: body });
      text = '';
      await tick();
      resizeField();
      messages = mergeMessages(messages, [sent]);
      await tick();
      scrollToBottom();
      void chatStore.refresh();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not send the message.');
    } finally {
      sending = false;
    }
  }

  function onKeydown(event: KeyboardEvent) {
    // A hardware keyboard sends with Return (Shift+Return for a new line); a phone's Return key is
    // a new line, and the Send button sends.
    if (event.key === 'Enter' && !event.shiftKey && !compact && !event.isComposing) {
      event.preventDefault();
      void send();
    }
  }

  function addToLibrary(url: string) {
    addUrl = url;
    addOpen = true;
  }

  // ── Phone: the composer in the tab bar's slot, above the keyboard ────────────────────────────
  $effect(() => {
    if (compact && !notFound) return bottomBar.claim('toolbar');
  });

  let keyboard = $state(0);
  $effect(() => {
    if (!compact || typeof window === 'undefined' || !window.visualViewport) return;
    const vv = window.visualViewport;
    const update = () => {
      const inset = Math.max(0, window.innerHeight - vv.height - vv.offsetTop);
      const opened = inset > 80 && keyboard <= 80;
      keyboard = inset;
      if (opened && nearBottom()) void tick().then(scrollToBottom);
    };
    update();
    vv.addEventListener('resize', update);
    vv.addEventListener('scroll', update);
    return () => {
      vv.removeEventListener('resize', update);
      vv.removeEventListener('scroll', update);
    };
  });
  const composerBottom = $derived(keyboard > 80 ? `${keyboard + 8}px` : 'var(--mh-tabbar-offset)');
</script>

{#snippet composer()}
  <form
    class={cn(
      'flex items-end gap-2',
      compact
        ? 'mh-glass mh-chrome fixed right-[max(16px,env(safe-area-inset-right))] left-[max(16px,env(safe-area-inset-left))] z-40 min-h-(--mh-tabbar-h) rounded-[31px] p-2'
        : 'bg-background border-t px-4 py-3'
    )}
    style:bottom={compact ? composerBottom : undefined}
    onsubmit={send}
  >
    <textarea
      bind:this={field}
      bind:value={text}
      rows="1"
      maxlength="4000"
      placeholder="Message"
      aria-label="Message"
      enterkeyhint={compact ? 'enter' : 'send'}
      autocapitalize="sentences"
      class={cn(
        'placeholder:text-muted-foreground min-h-11 flex-1 resize-none bg-transparent px-3 py-2.5 text-[16px] leading-6 outline-none md:min-h-9 md:py-1.5 md:text-sm',
        !compact && 'bg-secondary rounded-[18px]'
      )}
      oninput={resizeField}
      onkeydown={onKeydown}
      disabled={!conversation}
    ></textarea>
    <button
      type="submit"
      aria-label="Send"
      class="bg-primary text-primary-foreground focus-visible:ring-ring grid size-11 shrink-0 place-items-center rounded-full outline-none focus-visible:ring-2 disabled:opacity-40 md:size-9"
      disabled={!text.trim() || sending || !conversation}
      onpointerdown={(e) => e.preventDefault()}
    >
      {#if sending}
        <Loader2 class="size-5 animate-spin" aria-hidden="true" />
      {:else}
        <ArrowUp class="size-5" strokeWidth={2.5} aria-hidden="true" />
      {/if}
    </button>
  </form>
{/snippet}

<div class="flex min-h-0 flex-1 flex-col md:pb-(--mh-content-pad)">
  <div bind:this={scroller} class="min-h-0 flex-1 overflow-y-auto overscroll-contain max-md:pb-(--mh-content-pad)">
    <PageToolbarV2 {title} {meta} largeTitle={false} />

    {#if loading}
      <div class="flex flex-col gap-3 px-4 py-6" aria-busy="true">
        <Skeleton class="h-9 w-2/3 rounded-[20px]" />
        <Skeleton class="ml-auto h-9 w-1/2 rounded-[20px]" />
        <Skeleton class="h-16 w-3/4 rounded-2xl" />
      </div>
    {:else if notFound}
      <EmptyState
        icon={MessageCircle}
        title="This conversation isn’t here"
        hint="It may belong to another account signed in on this device."
      />
    {:else if error}
      <EmptyState
        icon={TriangleAlert}
        title="Couldn’t load this conversation"
        hint={error}
        action={{ label: 'Try again', onclick: () => void openConversation(conversationId) }}
      />
    {:else}
      {#if hasMore}
        <div class="flex justify-center py-3">
          <Button variant="gray" class="rounded-full" onclick={loadOlder} disabled={loadingOlder}>
            {#if loadingOlder}<Loader2 class="animate-spin" aria-hidden="true" />{/if}
            Earlier messages
          </Button>
        </div>
      {/if}

      {#if messages.length === 0}
        <EmptyState
          icon={MessageCircle}
          title={`Say hi to ${title}`}
          hint="Write a message, or pick Send to… on any song or album to share it here."
        />
      {:else}
        <ol class="flex flex-col pt-2 pb-4" aria-label={`Messages with ${title}`}>
          {#each messages as message, i (message.id)}
            {@const previous = messages[i - 1]}
            {@const next = messages[i + 1]}
            {#if startsDay(previous, message)}
              <li class="text-caption-1 text-muted-foreground mt-4 mb-1 text-center font-medium md:text-xs">
                {dayLabel(message.createdAtUtc)}
              </li>
            {/if}
            <li>
              <ChatMessageView
                {message}
                continues={continuesRun(previous, message) && !startsDay(previous, message)}
                lastOfRun={!next || !continuesRun(message, next) || startsDay(message, next)}
                onadd={admin ? addToLibrary : undefined}
              />
              {#if message.id === seenId}
                <p class="text-caption-2 text-muted-foreground px-5 pt-0.5 text-right md:text-[11px]">Seen</p>
              {/if}
            </li>
          {/each}
        </ol>
      {/if}
      {#if compact && keyboard > 80}
        <!-- The keyboard is over the bottom of the list: room to scroll the last message above it. -->
        <div aria-hidden="true" style:height="{keyboard}px"></div>
      {/if}
    {/if}
  </div>

  {#if !notFound}
    {@render composer()}
  {/if}
</div>

{#if admin}
  <AddFromUrlDialog bind:open={addOpen} initialUrl={addUrl} />
{/if}
