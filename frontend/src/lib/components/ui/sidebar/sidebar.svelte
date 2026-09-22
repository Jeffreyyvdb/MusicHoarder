<script lang="ts">
	import * as Sheet from "$lib/components/ui/sheet/index.js";
	import { cn, type WithElementRef } from "$lib/utils.js";
	import type { HTMLAttributes } from "svelte/elements";
	import { SIDEBAR_WIDTH_MOBILE } from "./constants.js";
	import { useSidebar } from "./context.svelte.js";

	let {
		ref = $bindable(null),
		side = "left",
		variant = "sidebar",
		collapsible = "offcanvas",
		class: className,
		children,
		...restProps
	}: WithElementRef<HTMLAttributes<HTMLDivElement>> & {
		side?: "left" | "right";
		variant?: "sidebar" | "floating" | "inset";
		collapsible?: "offcanvas" | "icon" | "none";
	} = $props();

	const sidebar = useSidebar();

	// Lightweight swipe-to-dismiss for the mobile Sheet: additive to the visible close button
	// AppSidebarV2 renders in its header, not a substitute for it. Only a touch/pen drag toward
	// the closing (left) edge counts — mouse keeps its native click/Escape/scrim-tap paths, and a
	// small dead zone keeps an incidental diagonal touch from hijacking a vertical nav scroll.
	// `touch-pan-y` on the content leaves vertical scrolling to the browser but hands horizontal
	// moves to these handlers, so iOS can't claim the drag and cancel the pointer stream midway.
	let dragStartX = $state<number | null>(null);
	let dragDeltaX = $state(0);
	const DEAD_ZONE = 4;
	const CLOSE_THRESHOLD = 80;

	function onDragStart(e: PointerEvent) {
		if (e.pointerType === 'mouse') return;
		dragStartX = e.clientX;
		dragDeltaX = 0;
	}
	function onDragMove(e: PointerEvent) {
		if (dragStartX == null) return;
		const raw = e.clientX - dragStartX;
		// Follow the finger 1:1 once past the dead zone; never let it drag past the open position.
		dragDeltaX = raw <= -DEAD_ZONE ? Math.min(0, raw) : 0;
	}
	function onDragEnd() {
		if (dragStartX == null) return;
		if (dragDeltaX <= -CLOSE_THRESHOLD) sidebar.setOpenMobile(false);
		dragStartX = null;
		dragDeltaX = 0;
	}
</script>

{#if collapsible === "none"}
	<div
		class={cn(
			"bg-sidebar text-sidebar-foreground flex h-full w-(--sidebar-width) flex-col",
			className
		)}
		bind:this={ref}
		{...restProps}
	>
		{@render children?.()}
	</div>
{:else if sidebar.isMobile}
	<Sheet.Root
		bind:open={() => sidebar.openMobile, (v) => sidebar.setOpenMobile(v)}
		{...restProps}
	>
		<Sheet.Content
			bind:ref
			data-sidebar="sidebar"
			data-slot="sidebar"
			data-mobile="true"
			class={cn(
				"mh-glass [--mh-glass-solid:var(--sidebar)] touch-pan-y bg-sidebar/70 text-sidebar-foreground ring-sidebar-border w-(--sidebar-width) overflow-hidden p-0 ring-1 backdrop-blur-xl backdrop-saturate-150 [&>button]:hidden data-[side=left]:top-[calc(0.75rem_+_env(safe-area-inset-top))] data-[side=left]:bottom-[calc(0.75rem_+_env(safe-area-inset-bottom))] data-[side=left]:left-3 data-[side=left]:h-auto data-[side=left]:rounded-2xl data-[side=left]:border-r-0 data-[side=left]:shadow-[0_4px_24px_oklch(0%_0_0/0.08)] dark:data-[side=left]:shadow-[0_4px_20px_rgba(0,0,0,0.35)]",
				className
			)}
			style="--sidebar-width: {SIDEBAR_WIDTH_MOBILE};{dragDeltaX ? ` transform: translateX(${dragDeltaX}px); transition: none;` : ''}"
			onpointerdown={onDragStart}
			onpointermove={onDragMove}
			onpointerup={onDragEnd}
			onpointercancel={onDragEnd}
			{side}
		>
			<Sheet.Header class="sr-only">
				<Sheet.Title>Sidebar</Sheet.Title>
				<Sheet.Description>Displays the mobile sidebar.</Sheet.Description>
			</Sheet.Header>
			<div class="flex h-full w-full flex-col">
				{@render children?.()}
			</div>
		</Sheet.Content>
	</Sheet.Root>
{:else}
	<div
		bind:this={ref}
		class="text-sidebar-foreground group peer hidden md:block"
		data-state={sidebar.state}
		data-collapsible={sidebar.state === "collapsed" ? collapsible : ""}
		data-variant={variant}
		data-side={side}
		data-slot="sidebar"
	>
		<!-- This is what handles the sidebar gap on desktop -->
		<div
			data-slot="sidebar-gap"
			class={cn(
				"transition-[width] duration-200 ease-linear relative w-(--sidebar-width) bg-transparent",
				"group-data-[collapsible=offcanvas]:w-0",
				"group-data-[side=right]:rotate-180",
				variant === "floating" || variant === "inset"
					? "group-data-[collapsible=icon]:w-[calc(var(--sidebar-width-icon)+(--spacing(4)))]"
					: "group-data-[collapsible=icon]:w-(--sidebar-width-icon)"
			)}
		></div>
		<div
			data-slot="sidebar-container"
			class={cn(
				"fixed inset-y-0 z-10 hidden h-svh w-(--sidebar-width) transition-[left,right,width] duration-200 ease-linear md:flex",
				side === "left"
					? "start-0 group-data-[collapsible=offcanvas]:start-[calc(var(--sidebar-width)*-1)]"
					: "end-0 group-data-[collapsible=offcanvas]:end-[calc(var(--sidebar-width)*-1)]",
				// Adjust the padding for floating and inset variants.
				variant === "floating" || variant === "inset"
					? "p-2 group-data-[collapsible=icon]:w-[calc(var(--sidebar-width-icon)+(--spacing(4))+2px)]"
					: "group-data-[collapsible=icon]:w-(--sidebar-width-icon) group-data-[side=left]:border-e group-data-[side=right]:border-s",
				className
			)}
			{...restProps}
		>
			<div
				data-sidebar="sidebar"
				data-slot="sidebar-inner"
				class="mh-glass [--mh-glass-solid:var(--sidebar)] bg-sidebar group-data-[variant=floating]:bg-sidebar/70 group-data-[variant=floating]:backdrop-blur-xl group-data-[variant=floating]:backdrop-saturate-150 group-data-[variant=floating]:ring-sidebar-border group-data-[variant=floating]:overflow-hidden group-data-[variant=floating]:rounded-2xl group-data-[variant=floating]:shadow-[0_4px_24px_oklch(0%_0_0/0.08)] dark:group-data-[variant=floating]:shadow-[0_4px_20px_rgba(0,0,0,0.35)] group-data-[variant=floating]:ring-1 flex size-full flex-col"
			>
				{@render children?.()}
			</div>
		</div>
	</div>
{/if}
