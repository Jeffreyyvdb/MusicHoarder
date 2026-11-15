<script lang="ts" module>
  import type { SongReportReason } from '#lib/api-client.ts';

  /** The reasons, in the order the sheet offers them, with the words a person picks from. */
  export const REPORT_REASONS: readonly {
    id: SongReportReason;
    label: string;
    sublabel: string;
  }[] = [
    { id: 'WrongSong', label: 'Wrong song', sublabel: 'Another track or artist, or not music' },
    {
      id: 'WrongVersion',
      label: 'Wrong version',
      sublabel: 'Live, remix, cover, sped up, instrumental…'
    },
    { id: 'BadAudio', label: 'Bad audio', sublabel: 'Cut off, low quality, noise or silence' },
    { id: 'Other', label: 'Something else', sublabel: 'Say what in the note' }
  ];

  export function reportReasonLabel(reason: SongReportReason): string {
    return REPORT_REASONS.find((r) => r.id === reason)?.label ?? reason;
  }
</script>

<script lang="ts">
  import { Check, Loader2 } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as BottomSheet from '#lib/components/ui/bottom-sheet/index.ts';
  import * as GroupedList from '#lib/components/ui/grouped-list/index.ts';
  import { Switch } from '#lib/components/ui/switch/index.ts';
  import { reportSong } from '#lib/api-client.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';

  // "Report wrong download": says what was wrong with a track MusicHoarder downloaded and, by
  // default, takes it out of the library (its files are deleted and its wishlist item is parked, so
  // nothing fetches it again by itself). The report is kept on Manage → Reported downloads, with
  // what was asked for and where the audio came from, to learn from.
  type Props = {
    open?: boolean;
    songId: number;
    /** The track's title, for the sheet's subtitle and the toast. */
    label: string;
    /** Opened from inside Now Playing: stacks above it (z-70) in its dark media appearance. */
    nested?: boolean;
  };

  let { open = $bindable(false), songId, label, nested = false }: Props = $props();

  let reason = $state<SongReportReason | null>(null);
  let note = $state('');
  let remove = $state(true);
  let busy = $state(false);

  $effect(() => {
    if (open) {
      reason = null;
      note = '';
      remove = true;
      busy = false;
    }
  });

  async function submit() {
    if (!reason || busy) return;
    busy = true;
    try {
      await reportSong(songId, { reason, note: note.trim() || null, remove });
      open = false;
      if (remove) {
        toast.success(`Removed ${label}`, {
          description: 'Reported as a wrong download and deleted from your library.'
        });
        // Its files are gone: playing on would only fail.
        if (playerStore.currentSong?.id === songId) {
          if (playerStore.hasNext) playerStore.playNext();
          else playerStore.stop();
        }
        void songsStore.loadSongs({ silent: true });
      } else {
        toast.success('Reported', { description: `${label} stays in your library.` });
      }
    } catch (err) {
      toast.error('Could not report the track', {
        description: err instanceof Error ? err.message : undefined
      });
    } finally {
      busy = false;
    }
  }
</script>

<BottomSheet.Root
  bind:open
  {nested}
  class={nested ? 'dark' : undefined}
  title="Report Download"
  description={label}
>
  {#snippet leading()}
    <BottomSheet.Action onclick={() => (open = false)}>Cancel</BottomSheet.Action>
  {/snippet}
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={submit} disabled={!reason || busy}>
      {#if busy}<Loader2 class="size-4 animate-spin" />{/if}
      {remove ? 'Remove' : 'Report'}
    </BottomSheet.Action>
  {/snippet}

  <div class="flex flex-col gap-7 pt-1 pb-2">
    <GroupedList.Section header="What's wrong?">
      <div role="radiogroup" aria-label="What's wrong?" class="contents">
        {#each REPORT_REASONS as option (option.id)}
          <GroupedList.Row
            role="radio"
            aria-checked={reason === option.id}
            label={option.label}
            sublabel={option.sublabel}
            onclick={() => (reason = option.id)}
          >
            {#snippet trailing()}
              {#if reason === option.id}
                <Check class="text-primary size-5" strokeWidth={2.5} aria-hidden="true" />
              {/if}
            {/snippet}
          </GroupedList.Row>
        {/each}
      </div>
    </GroupedList.Section>

    <GroupedList.Section
      header="Note"
      footer="Optional — what you expected, or what you got instead."
    >
      <GroupedList.Row>
        <textarea
          bind:value={note}
          maxlength={1000}
          rows={2}
          autocapitalize="sentences"
          aria-label="Note"
          placeholder="It's the live version from 2019"
          class="placeholder:text-muted-foreground text-body field-sizing-content min-h-12 w-full min-w-0 resize-none bg-transparent py-2.5 focus-visible:outline-hidden md:py-1.5 md:text-sm"
        ></textarea>
      </GroupedList.Row>
    </GroupedList.Section>

    <GroupedList.Section
      footer={remove
        ? 'Deletes the downloaded file and its library copy. The wishlist keeps the track as failed, so it is not downloaded again unless you retry it.'
        : 'Only records the report; the track stays in your library.'}
    >
      <GroupedList.Row label="Remove from library">
        {#snippet trailing()}
          <Switch
            checked={remove}
            onCheckedChange={(v: boolean) => (remove = v)}
            disabled={busy}
            aria-label="Remove from library"
          />
        {/snippet}
      </GroupedList.Row>
    </GroupedList.Section>
  </div>
</BottomSheet.Root>
