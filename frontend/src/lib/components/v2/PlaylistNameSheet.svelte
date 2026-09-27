<script lang="ts">
  import { Loader2 } from '@lucide/svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';

  // Name a playlist: "New Playlist" and "Rename". iOS sheet conventions, like Add from link: Cancel
  // leading, the one prominent action trailing, the field as an inset-grouped cell at 16px so iOS
  // never zooms on focus. The sheet stays open on a failure and says why.
  type Props = {
    open?: boolean;
    title: string;
    actionLabel: string;
    /** The name the field starts with (a rename's current name). */
    initialName?: string;
    description?: string;
    /** Opened from inside Now Playing: stacks above it (z-70) in its dark media appearance. */
    nested?: boolean;
    /** Resolves when done; a throw keeps the sheet open with the message. */
    onsubmit: (name: string) => Promise<void>;
  };

  let {
    open = $bindable(false),
    title,
    actionLabel,
    initialName = '',
    description,
    nested = false,
    onsubmit
  }: Props = $props();

  let name = $state('');
  let saving = $state(false);
  let error = $state<string | null>(null);
  let input = $state<HTMLInputElement | null>(null);

  $effect(() => {
    if (open) {
      name = initialName;
      error = null;
      saving = false;
    }
  });

  const canSave = $derived(!saving && name.trim().length > 0 && name.trim() !== initialName.trim());

  async function submit() {
    if (!canSave) return;
    saving = true;
    error = null;
    try {
      await onsubmit(name.trim());
      open = false;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Something went wrong.';
    } finally {
      saving = false;
    }
  }
</script>

<BottomSheet.Root
  bind:open
  {nested}
  class={nested ? 'dark' : undefined}
  {title}
  {description}
  onOpenAutoFocus={(e) => {
    e.preventDefault();
    input?.focus();
    input?.select();
  }}
>
  {#snippet leading()}
    <BottomSheet.Action onclick={() => (open = false)}>Cancel</BottomSheet.Action>
  {/snippet}
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={submit} disabled={!canSave}>
      {#if saving}
        <Loader2 class="size-4 animate-spin" />
      {/if}
      {actionLabel}
    </BottomSheet.Action>
  {/snippet}

  <div class="pt-1 pb-2">
    <GroupedList.Section footer={error ?? undefined}>
      <GroupedList.Row>
        <input
          bind:this={input}
          bind:value={name}
          type="text"
          maxlength={200}
          autocapitalize="sentences"
          autocomplete="off"
          enterkeyhint="done"
          aria-label="Playlist name"
          placeholder="Playlist name"
          class="placeholder:text-muted-foreground text-body w-full min-w-0 bg-transparent py-2.5 outline-none md:py-1.5 md:text-sm"
          onkeydown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              void submit();
            }
          }}
        />
      </GroupedList.Row>
    </GroupedList.Section>
  </div>
</BottomSheet.Root>
