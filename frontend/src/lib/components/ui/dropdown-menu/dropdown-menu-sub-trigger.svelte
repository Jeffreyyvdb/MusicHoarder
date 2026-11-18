<script lang="ts">
  import { DropdownMenu as DropdownMenuPrimitive } from 'bits-ui';
  import ChevronRightIcon from '@lucide/svelte/icons/chevron-right';
  import { cn } from '#lib/utils.js';

  let {
    ref = $bindable(null),
    class: className,
    inset,
    children,
    ...restProps
  }: DropdownMenuPrimitive.SubTriggerProps & {
    inset?: boolean;
  } = $props();
</script>

<DropdownMenuPrimitive.SubTrigger
  bind:ref
  data-slot="dropdown-menu-sub-trigger"
  data-inset={inset}
  class={cn(
    "focus:bg-accent focus:text-accent-foreground data-open:bg-accent data-open:text-accent-foreground not-data-[variant=destructive]:focus:**:text-accent-foreground flex cursor-default items-center gap-1.5 rounded-md px-1.5 py-1 text-sm select-none focus:outline-hidden data-inset:pl-7 data-[inset]:pl-8 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
    'pointer-coarse:text-body pointer-coarse:min-h-11 pointer-coarse:gap-3 pointer-coarse:rounded-lg pointer-coarse:px-3 pointer-coarse:py-2 pointer-coarse:data-inset:pl-10 pointer-coarse:[&>svg]:order-last pointer-coarse:[&>svg]:ml-auto pointer-coarse:[&>svg]:size-5 pointer-coarse:[&>svg:last-child]:size-4 pointer-coarse:[&>svg~svg]:ml-0',
    className
  )}
  {...restProps}
>
  {@render children?.()}
  <ChevronRightIcon class="pointer-coarse:text-muted-foreground ml-auto pointer-coarse:size-4" />
</DropdownMenuPrimitive.SubTrigger>
