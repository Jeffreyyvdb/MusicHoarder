<script lang="ts">
  import { goto } from '$app/navigation';
  import { Disc3, Loader2, Music } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { fetchChatPeople, sendChatToPeople, type ChatPerson } from '$lib/api-client';
  import { chatStore } from '$lib/stores/chat.svelte';
  import { sendTo } from '$lib/stores/send-to.svelte';
  import PeoplePicker from './PeoplePicker.svelte';

  // "Send to…" — a song or album of yours, to one or more people on this MusicHoarder, each in their
  // own chat with you. It travels as the song's share link (made now if it has none), so whoever
  // gets it can play it. One sheet for the app, opened through the sendTo store.

  let people = $state<ChatPerson[]>([]);
  let loading = $state(false);
  let selected = $state<string[]>([]);
  let note = $state('');
  let sending = $state(false);

  const item = $derived(sendTo.item);
  const isAlbum = $derived(item?.scope === 'album');

  $effect(() => {
    if (!sendTo.open) return;
    selected = [];
    note = '';
    loading = true;
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
    const current = item;
    if (!current || selected.length === 0 || sending) return;
    sending = true;
    try {
      const { messages } = await sendChatToPeople(selected, {
        songId: current.songId,
        scope: current.scope,
        text: note.trim() || null
      });
      sendTo.open = false;
      void chatStore.refresh();
      const names = people.filter((p) => selected.includes(p.id)).map((p) => p.name);
      const only = messages.length === 1 ? messages[0] : null;
      toast.success(`Sent to ${names.length === 1 ? names[0] : `${names.length} people`}`, {
        action: only ? { label: 'Open chat', onClick: () => void goto(`/chats/${only.conversationId}`) } : undefined
      });
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not send it.');
    } finally {
      sending = false;
    }
  }
</script>

<BottomSheet.Root
  bind:open={sendTo.open}
  nested={sendTo.item?.nested ?? false}
  title="Send to"
  description={isAlbum ? 'Anyone you send it to can play the whole album.' : 'Anyone you send it to can play it.'}
>
  {#snippet leading()}
    <BottomSheet.Action onclick={() => (sendTo.open = false)}>Cancel</BottomSheet.Action>
  {/snippet}
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={send} disabled={selected.length === 0 || sending}>
      {#if sending}<Loader2 class="size-4 animate-spin" aria-hidden="true" />{/if}
      Send
    </BottomSheet.Action>
  {/snippet}

  <div class="flex flex-col gap-5 pb-6 md:px-4">
    {#if item}
      <GroupedList.Section>
        <GroupedList.Row label={item.title} sublabel={[isAlbum ? 'Album' : 'Song', item.subtitle].filter(Boolean).join(' · ')}>
          {#snippet leading()}
            {#if item.coverUrl}
              <Cover
                artist={item.subtitle ?? ''}
                title={item.title}
                coverUrl={item.coverUrl}
                size={44}
                corner={6}
                dprCap={3}
                caption={false}
              />
            {:else}
              <span class="bg-muted text-muted-foreground grid size-11 place-items-center rounded-md">
                {#if isAlbum}<Disc3 class="size-5" aria-hidden="true" />{:else}<Music class="size-5" aria-hidden="true" />{/if}
              </span>
            {/if}
          {/snippet}
        </GroupedList.Row>
        <div class="px-4 py-2">
          <textarea
            bind:value={note}
            rows="2"
            maxlength="4000"
            placeholder="Add a message (optional)"
            aria-label="Message"
            autocapitalize="sentences"
            class="placeholder:text-muted-foreground w-full resize-none bg-transparent py-1 text-[16px] outline-none md:text-sm"
          ></textarea>
        </div>
      </GroupedList.Section>
    {/if}

    <PeoplePicker {people} {loading} {selected} multiple onpick={toggle} />
  </div>
</BottomSheet.Root>
