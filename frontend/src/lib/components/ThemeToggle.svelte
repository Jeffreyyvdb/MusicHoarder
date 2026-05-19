<script lang="ts">
  import { Check, Monitor, Moon, Sun } from '@lucide/svelte';
  import { mode, userPrefersMode, setMode } from 'mode-watcher';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { cn } from '$lib/utils';

  type Props = {
    class?: string;
    variant?: 'outline' | 'ghost';
  };
  const { class: className = '', variant = 'ghost' }: Props = $props();

  const themeOptions = [
    { value: 'light' as const, label: 'Light', icon: Sun },
    { value: 'dark' as const, label: 'Dark', icon: Moon },
    { value: 'system' as const, label: 'System', icon: Monitor }
  ];

  const sizeClasses = $derived(variant === 'ghost' ? 'size-9' : 'size-9 shrink-0');
  const outlineClasses = $derived(
    variant === 'outline'
      ? 'border-border bg-background/80 backdrop-blur-sm shadow-sm hover:bg-accent'
      : ''
  );
</script>

<DropdownMenu.Root>
  <DropdownMenu.Trigger>
    {#snippet child({ props })}
      <Button
        {...props}
        type="button"
        {variant}
        size="icon"
        class={cn(sizeClasses, outlineClasses, 'transition-colors', className)}
        aria-label="Change theme"
        title="Change theme"
      >
        {#if mode.current === 'dark'}
          <Moon class="size-[1.125rem] text-foreground" />
        {:else}
          <Sun class="size-[1.125rem] text-foreground" />
        {/if}
      </Button>
    {/snippet}
  </DropdownMenu.Trigger>
  <DropdownMenu.Content align="end" class="min-w-36">
    {#each themeOptions as { value, label, icon: Icon } (value)}
      <DropdownMenu.Item onSelect={() => setMode(value)} class="justify-between">
        <span class="flex items-center gap-2">
          <Icon class="size-4" />
          {label}
        </span>
        {#if userPrefersMode.current === value}
          <Check class="size-4 text-muted-foreground" />
        {/if}
      </DropdownMenu.Item>
    {/each}
  </DropdownMenu.Content>
</DropdownMenu.Root>
