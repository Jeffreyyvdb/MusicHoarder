import {
  fetchChatConversations,
  markChatRead,
  openChatStream,
  type ChatConversation
} from '$lib/api-client';
import { isDemo } from '$lib/auth/capabilities';
import type { SessionUser } from '$lib/auth/session-types';

/**
 * The account's conversations and unread counts, kept live over the chat stream
 * (`/api/chat/stream`) while the app shell is mounted. The tab bar's Chats badge, the sidebar and
 * the Chats page all read it, so no two surfaces disagree about what is unread.
 *
 * Stream events only say "something changed in conversation X"; the list is re-read (debounced), and
 * an open conversation subscribes to hear about its own messages ({@link chatStore.onEvent}).
 * Never started for the demo account — strangers share it, and chat is off for it server-side.
 */

export type ChatEvent =
  | { type: 'ready' }
  | { type: 'message'; conversationId: string; messageId: number }
  | { type: 'read'; conversationId: string; userId: string; lastReadAtUtc: string };

let conversations = $state<ChatConversation[]>([]);
let loaded = $state(false);
let failed = $state(false);
let userId: string | null = null;
let closeStream: (() => void) | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | null = null;
let inFlight: Promise<void> | null = null;
let again = false;
const listeners = new Set<(event: ChatEvent) => void>();

const unreadTotal = $derived(conversations.reduce((sum, c) => sum + c.unreadCount, 0));

function emit(event: ChatEvent) {
  for (const listener of listeners) {
    try {
      listener(event);
    } catch {
      // one listener's failure is not the others'
    }
  }
}

async function load(): Promise<void> {
  if (!userId) return;
  if (inFlight) {
    again = true;
    return inFlight;
  }
  inFlight = (async () => {
    try {
      const forUser = userId;
      const list = await fetchChatConversations();
      if (forUser !== userId) return;
      conversations = list;
      failed = false;
    } catch {
      failed = true;
    } finally {
      loaded = true;
    }
  })().finally(() => {
    inFlight = null;
    if (again) {
      again = false;
      void load();
    }
  });
  return inFlight;
}

/** Re-read soon: several events in a burst cost one request. */
function scheduleRefresh(delayMs = 150) {
  if (refreshTimer) return;
  refreshTimer = setTimeout(() => {
    refreshTimer = null;
    void load();
  }, delayMs);
}

export const chatStore = {
  get conversations(): ChatConversation[] {
    return conversations;
  },
  get loaded(): boolean {
    return loaded;
  },
  get failed(): boolean {
    return failed;
  },
  /** Unread messages across every conversation: the Chats badge. */
  get unreadTotal(): number {
    return unreadTotal;
  },

  /** Idempotent per account. The shell calls it for every signed-in, non-demo session. */
  start(user: SessionUser | null | undefined): void {
    const id = user && !isDemo(user) ? user.id : null;
    if (id === userId) return;
    this.stop();
    userId = id;
    if (!id || typeof window === 'undefined') return;
    closeStream = openChatStream({
      onReady: () => {
        scheduleRefresh(0);
        emit({ type: 'ready' });
      },
      onMessage: (event) => {
        scheduleRefresh();
        emit({ type: 'message', ...event });
      },
      onRead: (event) => {
        if (event.userId === userId) scheduleRefresh();
        emit({ type: 'read', ...event });
      }
    });
  },

  stop(): void {
    closeStream?.();
    closeStream = null;
    if (refreshTimer) clearTimeout(refreshTimer);
    refreshTimer = null;
    userId = null;
    conversations = [];
    loaded = false;
    failed = false;
  },

  refresh(): Promise<void> {
    return load();
  },

  /** Hear what the stream says — an open conversation appends its own messages. */
  onEvent(listener: (event: ChatEvent) => void): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },

  /** A conversation fetched or changed on screen, reflected in the list without waiting for a re-read. */
  upsert(conversation: ChatConversation): void {
    const others = conversations.filter((c) => c.id !== conversation.id);
    conversations = [conversation, ...others].sort(
      (a, b) => Date.parse(b.lastMessageAtUtc) - Date.parse(a.lastMessageAtUtc)
    );
  },

  /** Mark read here at once, and on the server (which tells this account's other devices). */
  async markRead(conversationId: string): Promise<void> {
    const conversation = conversations.find((c) => c.id === conversationId);
    if (conversation && conversation.unreadCount > 0) {
      conversations = conversations.map((c) => (c.id === conversationId ? { ...c, unreadCount: 0 } : c));
    }
    try {
      await markChatRead(conversationId);
    } catch {
      scheduleRefresh();
    }
  }
};
