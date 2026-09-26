<script lang="ts">
  import { Bell, BellOff, Share } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { Button } from '$lib/components/ui/button';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { enablePush, pushState, type PushState } from '$lib/push/web-push';

  // The ask for chat notifications, at the top of the chat list until they are on (or cannot be).
  // Asked from a button, never on load: iOS shows the permission prompt only inside a tap, and an
  // unprompted ask is how a site gets blocked for good. On an iPhone in a Safari tab the answer is
  // to add the app to the Home Screen first — web push only exists there.
  type Props = { userId: string; onchange?: (state: PushState) => void };
  const { userId, onchange }: Props = $props();

  let status = $state<PushState | null>(null);
  let working = $state(false);
  let dismissed = $state(false);

  const DISMISS_KEY = 'mh:push-ask-dismissed';

  $effect(() => {
    try {
      dismissed = localStorage.getItem(DISMISS_KEY) === userId;
    } catch {
      dismissed = false;
    }
    void pushState(userId).then((s) => (status = s));
  });

  async function turnOn() {
    working = true;
    try {
      status = await enablePush(userId);
      onchange?.(status);
      if (status === 'on') toast.success('Notifications are on');
      else if (status === 'denied') toast.error('Notifications are blocked for this site in your browser settings.');
      else if (status === 'unavailable') toast.error('Notifications are switched off on this server.');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not turn on notifications.');
    } finally {
      working = false;
    }
  }

  function notNow() {
    dismissed = true;
    try {
      localStorage.setItem(DISMISS_KEY, userId);
    } catch {
      // asked again next time
    }
  }
</script>

{#if status === 'off' && !dismissed}
  <GroupedList.Section footer="MusicHoarder tells you when someone sends you a message or a song.">
    <GroupedList.Row icon={Bell} label="Turn on notifications">
      {#snippet trailing()}
        <div class="flex items-center gap-1">
          <Button variant="ghost" class="text-muted-foreground h-11 md:h-8" onclick={notNow}>Not now</Button>
          <Button variant="gray" class="text-primary h-11 rounded-full md:h-8" onclick={turnOn} disabled={working}>
            Turn on
          </Button>
        </div>
      {/snippet}
    </GroupedList.Row>
  </GroupedList.Section>
{:else if status === 'install' && !dismissed}
  <GroupedList.Section>
    <GroupedList.Row
      icon={Share}
      label="Get notified on this iPhone"
      sublabel="Add MusicHoarder to your Home Screen (Share → Add to Home Screen), then turn notifications on there."
    >
      {#snippet trailing()}
        <Button variant="ghost" class="text-muted-foreground h-11 md:h-8" onclick={notNow}>Not now</Button>
      {/snippet}
    </GroupedList.Row>
  </GroupedList.Section>
{:else if status === 'denied' && !dismissed}
  <GroupedList.Section>
    <GroupedList.Row
      icon={BellOff}
      label="Notifications are blocked"
      sublabel="Allow notifications for this site in your browser’s settings to hear about new messages."
    >
      {#snippet trailing()}
        <Button variant="ghost" class="text-muted-foreground h-11 md:h-8" onclick={notNow}>OK</Button>
      {/snippet}
    </GroupedList.Row>
  </GroupedList.Section>
{/if}
