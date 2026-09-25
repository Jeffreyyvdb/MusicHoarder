<script lang="ts">
	import { ScrollArea as ScrollAreaPrimitive } from "bits-ui";
	import { MediaQuery } from "svelte/reactivity";
	import { Scrollbar } from "./index.js";
	import { cn, type WithoutChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		viewportRef = $bindable(null),
		class: className,
		viewportClass = "",
		orientation = "vertical",
		type,
		scrollbarXClasses = "",
		scrollbarYClasses = "",
		children,
		...restProps
	}: WithoutChild<ScrollAreaPrimitive.RootProps> & {
		orientation?: "vertical" | "horizontal" | "both" | undefined;
		/** Classes for the scrolling element itself (padding, overscroll-behavior). */
		viewportClass?: string | undefined;
		scrollbarXClasses?: string | undefined;
		scrollbarYClasses?: string | undefined;
		viewportRef?: HTMLElement | null;
	} = $props();

	// The bar shows on hover for a mouse, and while scrolling for a finger: a touch never hovers,
	// so "hover" would flash the bar at touchstart and hide it mid-fling — the scroll indicator
	// iOS draws for a native scroller is the thing this replaces.
	const coarse = new MediaQuery("pointer: coarse");
</script>

<ScrollAreaPrimitive.Root
	bind:ref
	data-slot="scroll-area"
	data-orientation={orientation}
	type={type ?? (coarse.current ? "scroll" : "hover")}
	class={cn("relative", className)}
	{...restProps}
>
	<ScrollAreaPrimitive.Viewport
		bind:ref={viewportRef}
		data-slot="scroll-area-viewport"
		class={cn(
			"cn-scroll-area-viewport focus-visible:ring-ring/50 size-full rounded-[inherit] transition-[color,box-shadow] outline-none focus-visible:ring-[3px] focus-visible:outline-1",
			viewportClass
		)}
	>
		{@render children?.()}
	</ScrollAreaPrimitive.Viewport>
	{#if orientation === "vertical" || orientation === "both"}
		<Scrollbar orientation="vertical" class={scrollbarYClasses} />
	{/if}
	{#if orientation === "horizontal" || orientation === "both"}
		<Scrollbar orientation="horizontal" class={scrollbarXClasses} />
	{/if}
	<ScrollAreaPrimitive.Corner />
</ScrollAreaPrimitive.Root>
