<script lang="ts">
  import { toast } from 'svelte-sonner';
  import * as AlertDialog from '#lib/components/ui/alert-dialog/index.ts';
  import { fetchSettings, updateSettings } from '#lib/api-client.ts';
  import { shouldAskAboutAutoSync } from '#lib/lyrics/auto-sync-choice.ts';

  /**
   * Asks the admin, once, whether the background sweep may give songs AI-timed lyrics. It sends whole
   * songs to the transcription provider, which bills per minute unless it is a free tier, so the server
   * keeps it off until someone answers. Shown only while the server says nobody has
   * (`autoSyncChosen: false`); dismissing it without an answer asks again on the next visit. The answer
   * can be changed in Settings.
   */
  const ASK_DELAY_MS = 2000;

  let open = $state(false);

  $effect(() => {
    let cancelled = false;
    // The library's cold load goes first; this question can wait a moment.
    const timer = setTimeout(() => {
      fetchSettings()
        .then((settings) => {
          if (!cancelled) open = shouldAskAboutAutoSync(settings.lyricsTranscription);
        })
        .catch(() => {});
    }, ASK_DELAY_MS);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  });

  async function answer(autoSync: boolean) {
    open = false;
    try {
      await updateSettings({ lyricsTranscription: { autoSync } });
      toast.success(
        autoSync
          ? 'AI-timed lyrics are on. Change it any time in Settings.'
          : 'AI-timed lyrics stay off. Turn them on any time in Settings.'
      );
    } catch {
      toast.error('Couldn’t save that. MusicHoarder will ask again next time.');
    }
  }
</script>

<AlertDialog.Root bind:open>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Give songs AI-timed lyrics?</AlertDialog.Title>
      <AlertDialog.Description>
        MusicHoarder can send songs without synced lyrics to your transcription provider in the
        background, a few at a time. Official words get timed to the music, and songs with none get
        the words the AI hears. Part of the provider's allowance is always kept for the AI lyrics
        button. If your provider bills per minute, every song costs money. You can change this later
        in Settings.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={() => void answer(false)}>Leave off</AlertDialog.Cancel>
      <AlertDialog.Action onclick={() => void answer(true)}>Turn on</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
