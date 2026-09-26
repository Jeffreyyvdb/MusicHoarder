<script lang="ts">
  import { page } from '$app/state';
  import { Link2, Loader2, MessageCircle } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Button } from '$lib/components/ui/button';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { fetchChatPeople, sendChatToPeople, type ChatPerson } from '$lib/api-client';
  import { isDemo } from '$lib/auth/capabilities';
  import { linkCaption, sharedLink } from '$lib/chat/chat-text';
  import { clearPendingShare, pendingShareFrom, readPendingShare, type PendingShare } from '$lib/chat/pending-share';
  import { replaceUrl } from '$lib/navigation/replace-url';
  import { chatStore } from '$lib/stores/chat.svelte';
  import PeoplePicker from './PeoplePicker.svelte';

  // Where the phone's share sheet lands: "Share" in Spotify or YouTube, then MusicHoarder (the
  // installed app registers as a share target in its manifest). What was shared — usually a link
  // with some boilerplate around it — goes to the people picked here, each in their own chat; the
  // server turns a Spotify or YouTube link into a card with its artwork.
  const demo = $derived(isDemo(page.data.user));

  // Taken once, as the page opens: from the URL, or from what /share-target stored before a sign-in.
  // The stored copy is cleared at once — the app shell brings it back only until this page opens.
  const shared: PendingShare | null = pendingShareFrom(page.url.searchParams) ?? readPendingShare();
  clearPendingShare();
  const url = $derived(shared ? sharedLink(shared) : null);
  // With no link at all, what was shared is the message itself.
  const sharedText = $derived(shared && !url ? (shared.text ?? shared.title) : null);
  const provider = $derived.by(() => {
    if (!url) return null;
    const host = new URL(url).hostname;
    if (/(^|\.)spotify\.(com|link|app\.link)$/.test(host)) return 'spotify' as const;
    if (/(^|\.)(youtube\.com|youtu\.be)$/.test(host)) return 'youtube' as const;
    return null;
  });

  let people = $state<ChatPerson[]>([]);
  let loading = $state(true);
  let selected = $state<string[]>([]);
  let note = $state('');
  let sending = $state(false);

  $effect(() => {
    if (sharedText && !note) note = sharedText;
  });

  $effect(() => {
    if (demo) return;
    fetchChatPeople()
      .then((list) => (people = list))
      .catch(() => (people = []))
      .finally(() => (loading = false));
  });

  function toggle(person: ChatPerson) {
    selected = selected.includes(person.id)
      ? selected.filter((id) => id !== person.id)
      : [...selected, person.id];
  }

  async function send() {
    if (selected.length === 0 || sending) return;
    if (!url && !note.trim()) return;
    sending = true;
    try {
      const { messages } = await sendChatToPeople(selected, { url, text: note.trim() || null });
      clearPendingShare();
      void chatStore.refresh();
      toast.success(selected.length === 1 ? 'Sent' : `Sent to ${selected.length} people`);
      // A replace: Back from the conversation goes to the chat list, not to this page again.
      await replaceUrl(messages.length === 1 ? `/chats/${messages[0].conversationId}` : '/chats', {
        keepFocus: false,
        noScroll: false
      });
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not send it.');
    } finally {
      sending = false;
    }
  }

  function cancel() {
    clearPendingShare();
    void replaceUrl('/chats', { keepFocus: false, noScroll: false });
  }
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <div class="min-h-0 flex-1 overflow-y-auto pb-(--mh-content-pad)">
    <PageToolbarV2 title="Send to" grouped back={{ label: 'Chats', href: '/chats' }} />

    <div class="mx-auto flex w-full max-w-2xl flex-col gap-5 pt-2 pb-6 md:px-4">
      {#if demo}
        <EmptyState
          icon={MessageCircle}
          title="Chat is off for the demo"
          hint="Sign in to your own MusicHoarder to send what you shared to someone."
        />
      {:else if !shared}
        <EmptyState
          icon={Link2}
          title="Nothing to send"
          hint="Share a song from Spotify or YouTube and pick MusicHoarder in the share sheet."
          action={{ label: 'Go to Chats', onclick: cancel }}
        />
      {:else}
        <GroupedList.Section>
          {#if url}
            <GroupedList.Row
              icon={Link2}
              label={shared.title ?? url}
              sublabel={linkCaption({ url, provider, kind: null })}
            />
          {/if}
          <div class="px-4 py-2">
            <textarea
              bind:value={note}
              rows="2"
              maxlength="4000"
              placeholder={url ? 'Add a message (optional)' : 'Message'}
              aria-label="Message"
              autocapitalize="sentences"
              class="placeholder:text-muted-foreground w-full resize-none bg-transparent py-1 text-[16px] outline-none md:text-sm"
            ></textarea>
          </div>
        </GroupedList.Section>

        <PeoplePicker {people} {loading} {selected} multiple onpick={toggle} />

        <div class="flex gap-3 px-4 md:px-0">
          <Button variant="gray" class="h-12 flex-1 rounded-full md:h-10" onclick={cancel}>Cancel</Button>
          <Button
            class="h-12 flex-1 rounded-full md:h-10"
            onclick={send}
            disabled={selected.length === 0 || sending || (!url && !note.trim())}
          >
            {#if sending}<Loader2 class="animate-spin" aria-hidden="true" />{/if}
            {selected.length > 1 ? `Send to ${selected.length}` : 'Send'}
          </Button>
        </div>
      {/if}
    </div>
  </div>
</div>
