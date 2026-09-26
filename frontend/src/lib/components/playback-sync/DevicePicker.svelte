<script lang="ts">
  import { Check } from '@lucide/svelte';
  import type { Snippet } from 'svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { playbackSync, type DeviceEntry } from '$lib/stores/playback-sync.svelte';
  import { cn } from '$lib/utils';
  import { DEVICE_ICONS } from './device-icons';

  /**
   * Where the account's music plays: "This device" first, then the other devices that are open
   * right now, the one holding the session marked Playing / Paused. Picking this device pulls the
   * music here at its position (same queue, same station); picking another asks that one to take
   * over. A grouped bottom sheet on a phone, a menu on md+ — the account menu's split.
   *
   * The caller renders the control that opens it (`trigger`), spreading the props it is handed
   * onto a `<button>`: on md+ they are the menu trigger's, on a phone a plain opener's.
   */
  type Props = {
    trigger: Snippet<[Record<string, unknown>]>;
    /** Opened from inside Now Playing: above its z-60, in its dark media appearance. */
    nested?: boolean;
    /** md+ menu placement against the trigger. */
    side?: 'top' | 'bottom';
    align?: 'start' | 'center' | 'end';
  };
  const { trigger, nested = false, side = 'top', align = 'center' }: Props = $props();

  const isMobile = new IsMobile();
  let open = $state(false);

  const entries = $derived(playbackSync.entries);
  const lonely = $derived(!playbackSync.hasOtherDevices);
  const FOOTNOTE = 'Other devices appear here while MusicHoarder is open on them.';

  function choose(entry: DeviceEntry) {
    open = false;
    playbackSync.choose(entry);
  }

  function statusLabel(entry: DeviceEntry): string | null {
    if (entry.isThis) return entry.detail;
    if (entry.status === 'playing') return 'Playing';
    if (entry.status === 'paused') return 'Paused';
    return null;
  }
</script>

{#if isMobile.current}
  {@render trigger({
    onclick: () => (open = true),
    'aria-haspopup': 'dialog',
    'aria-expanded': open
  })}
  <!-- `dark` inside Now Playing: its sheets keep the media appearance whatever the app theme. -->
  <BottomSheet.Root bind:open title="Play on" {nested} class={nested ? 'dark' : undefined}>
    {#snippet trailing()}
      <BottomSheet.Action prominent onclick={() => (open = false)}>Done</BottomSheet.Action>
    {/snippet}
    <GroupedList.Section footer={lonely ? FOOTNOTE : undefined}>
      {#each entries as entry (entry.deviceId)}
        {@const Icon = DEVICE_ICONS[entry.kind]}
        {@const status = statusLabel(entry)}
        <GroupedList.Row
          onclick={() => choose(entry)}
          label={entry.label}
          aria-current={entry.current ? 'true' : undefined}
        >
          {#snippet leading()}
            <span
              aria-hidden="true"
              class="bg-muted text-foreground flex size-[29px] items-center justify-center rounded-[7px]"
            >
              <Icon class="size-[18px]" />
            </span>
          {/snippet}
          {#if status}
            <!-- Live state is tinted (the one playing); everything else is secondary text. -->
            <span
              class={cn(
                'text-subheadline truncate',
                entry.status === 'playing' && !entry.isThis ? 'text-primary' : 'text-muted-foreground'
              )}
            >
              {status}
            </span>
          {/if}
          {#snippet trailing()}
            {#if entry.current}
              <Check class="text-primary size-5" strokeWidth={2.5} />
            {/if}
          {/snippet}
        </GroupedList.Row>
      {/each}
    </GroupedList.Section>
  </BottomSheet.Root>
{:else}
  <DropdownMenu.Root bind:open>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        {@render trigger(props)}
      {/snippet}
    </DropdownMenu.Trigger>
    <!-- Inside Now Playing the menu takes its dark media appearance and opens above its z-60. -->
    <DropdownMenu.Content
      {side}
      {align}
      sideOffset={8}
      class={cn('w-72', nested && 'dark z-[70]')}
    >
      <DropdownMenu.Label>Play on</DropdownMenu.Label>
      {#each entries as entry (entry.deviceId)}
        {@const Icon = DEVICE_ICONS[entry.kind]}
        {@const status = statusLabel(entry)}
        <DropdownMenu.Item
          onSelect={() => choose(entry)}
          aria-current={entry.current ? 'true' : undefined}
          class="gap-2.5 py-1.5"
        >
          <!-- The check leads (as in a macOS menu); a span, so a touch pointer's trailing-icon
               rule leaves it where it is. -->
          <span aria-hidden="true" class="flex size-4 shrink-0 items-center justify-center">
            {#if entry.current}<Check class="text-primary size-4" strokeWidth={2.5} />{/if}
          </span>
          <span class="flex min-w-0 flex-1 flex-col">
            <span class="truncate">{entry.label}</span>
            {#if status}
              <span
                class={cn(
                  'truncate text-xs',
                  entry.status === 'playing' && !entry.isThis
                    ? 'text-primary'
                    : 'text-muted-foreground'
                )}
              >
                {status}
              </span>
            {/if}
          </span>
          <Icon class="text-muted-foreground size-4" />
        </DropdownMenu.Item>
      {/each}
      {#if lonely}
        <p class="text-muted-foreground px-1.5 pt-1 pb-1.5 text-xs pointer-coarse:px-3">
          {FOOTNOTE}
        </p>
      {/if}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/if}
