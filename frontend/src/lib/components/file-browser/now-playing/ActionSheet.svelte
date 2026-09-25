<script lang="ts">
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { cn } from '$lib/utils';

  /**
   * A confirmation opened from inside Now Playing, laid out as an iOS action sheet: the title and
   * what will happen, the one action, then Cancel on its own at the bottom (action-sheets.md:
   * "Place the Cancel button at the bottom"), where the thumb already is. Nested, so it opens over
   * the overlay's z-60, and in its dark media appearance. An AlertDialog would stack above it too
   * (z-80), but a centred alert is iOS's form for a warning, not for confirming an action picked
   * from a menu, and it would drop the media appearance.
   */
  type Props = {
    open: boolean;
    title: string;
    description: string;
    /** The action's label, e.g. "Reset metadata". */
    actionLabel: string;
    /** Red: the action cannot be undone. */
    destructive?: boolean;
    onAction: () => void;
  };
  let {
    open = $bindable(false),
    title,
    description,
    actionLabel,
    destructive = false,
    onAction
  }: Props = $props();
</script>

<!-- showClose off: Cancel is the way out, at the bottom; a desktop X beside it would be a second. -->
<BottomSheet.Root bind:open nested showClose={false} class="dark" {title} {description}>
  <div class="flex flex-col gap-3">
    <GroupedList.Section>
      <GroupedList.Row
        onclick={() => {
          open = false;
          onAction();
        }}
      >
        <span
          class={cn(
            'text-body text-center md:text-sm',
            destructive ? 'text-destructive-text' : 'text-primary'
          )}
        >
          {actionLabel}
        </span>
      </GroupedList.Row>
    </GroupedList.Section>
    <GroupedList.Section>
      <GroupedList.Row onclick={() => (open = false)}>
        <span class="text-body text-primary text-center font-semibold md:text-sm">Cancel</span>
      </GroupedList.Row>
    </GroupedList.Section>
  </div>
</BottomSheet.Root>
