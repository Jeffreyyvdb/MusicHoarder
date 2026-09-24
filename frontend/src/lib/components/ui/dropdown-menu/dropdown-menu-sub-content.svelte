<script lang="ts">
	import { DropdownMenu as DropdownMenuPrimitive } from "bits-ui";
	import { MediaQuery } from "svelte/reactivity";
	import { cn } from "$lib/utils.js";

	// A phone has no room beside a 240px menu for a second one, so on touch a submenu opens
	// below its item, over the parent — iOS shows submenus in place of the menu, not beside it.
	// A caller's explicit `side` / `align` still wins (restProps is spread after). The submenu
	// renders inside its parent menu, after the items that follow its trigger — which are
	// `relative` — so it needs its own z-index to paint over them once it overlaps the parent.
	const coarse = new MediaQuery("pointer: coarse");

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: DropdownMenuPrimitive.SubContentProps = $props();
</script>

<DropdownMenuPrimitive.SubContent
	bind:ref
	data-slot="dropdown-menu-sub-content"
	side={coarse.current ? "bottom" : undefined}
	align={coarse.current ? "end" : undefined}
	class={cn("data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 data-closed:zoom-out-95 data-open:zoom-in-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 ring-foreground/10 bg-popover text-popover-foreground min-w-[96px] pointer-coarse:min-w-60 z-50 rounded-xl p-1 shadow-[0_10px_40px_rgb(0_0_0/0.18)] ring-1 duration-100 w-auto", className)}
	{...restProps}
/>
