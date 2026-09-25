<script lang="ts">
  import { page } from '$app/state';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { storageUsage } from '$lib/stores/storage-usage.svelte';
  import { cn } from '$lib/utils';
  import AccountPanel, { avatar } from '$lib/components/v2/AccountPanel.svelte';

  // The account entry point — the App Store / Music pattern: an initials avatar that opens
  // everything about "you": the accounts this browser remembers, Appearance, Settings, the
  // admin's storage and folders, and Sign out. It replaces the sidebar's account card, the
  // top-bar theme toggle and the separate one-tap sign-out button.
  //
  // Compact (below md): a 32px avatar in a 44pt glass circle, like every other nav-bar item, that
  // presents a grouped bottom sheet. md+: a plain avatar in the desktop top bar that opens a menu
  // (no glass on desktop bars). Zero required props, so any bar can drop it in.
  type Props = { class?: string };
  const { class: className }: Props = $props();

  const user = $derived(page.data.user);
  const isMobile = new IsMobile();

  let open = $state(false);

  // Storage hands off to the breakdown dialog the shell mounts. The panel's container closes
  // first; its close-auto-focus would otherwise pull focus back to this trigger, behind the
  // dialog that is now open, so skip it for that one close.
  let handingOff = false;
  function openStorage() {
    handingOff = true;
    open = false;
    storageUsage.dialogOpen = true;
  }
  function onCloseAutoFocus(event: Event) {
    if (!handingOff) return;
    handingOff = false;
    event.preventDefault();
  }

  const label = $derived(user ? `Account: ${user.displayName?.trim() || user.email}` : 'Account');
  const name = $derived(user ? user.displayName?.trim() || user.email : '');
</script>

{#if user}
  {#if isMobile.current}
    <button
      type="button"
      aria-label={label}
      aria-haspopup="dialog"
      aria-expanded={open}
      onclick={() => (open = true)}
      class={cn(
        'mh-glass mh-chrome grid size-11 shrink-0 place-items-center rounded-full outline-none',
        'focus-visible:ring-ring transition-transform duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] focus-visible:ring-2 active:scale-[0.94]',
        className
      )}
    >
      {@render avatar(name, user.email, 'size-8 text-[13px]')}
    </button>
    <BottomSheet.Root bind:open title="Account" {onCloseAutoFocus}>
      {#snippet trailing()}
        <BottomSheet.Action prominent onclick={() => (open = false)}>Done</BottomSheet.Action>
      {/snippet}
      <AccountPanel presentation="sheet" close={() => (open = false)} onstorage={openStorage} />
    </BottomSheet.Root>
  {:else}
    <DropdownMenu.Root bind:open>
      <DropdownMenu.Trigger
        aria-label={label}
        class={cn(
          'grid size-8 shrink-0 place-items-center rounded-full outline-none',
          'focus-visible:ring-ring focus-visible:ring-2',
          className
        )}
      >
        {@render avatar(name, user.email, 'size-7 text-[11px]')}
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="end" class="w-72" {onCloseAutoFocus}>
        <AccountPanel presentation="menu" close={() => (open = false)} onstorage={openStorage} />
      </DropdownMenu.Content>
    </DropdownMenu.Root>
  {/if}
{/if}
