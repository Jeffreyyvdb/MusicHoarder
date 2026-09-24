<script lang="ts">
	import { holdAppInert } from "$lib/actions/inert-app";
	import { Dialog as DialogPrimitive } from "bits-ui";
	import type { Snippet } from "svelte";
	import XIcon from "@lucide/svelte/icons/x";
	import { Button } from "$lib/components/ui/button/index.js";
	import { IsMobile } from "$lib/hooks/is-mobile.svelte.js";
	import { DISMISS_VELOCITY, EASE_PRESENT, prefersReducedMotion } from "$lib/motion.js";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";

	type Props = WithoutChildrenOrChild<DialogPrimitive.ContentProps> & {
		open?: boolean;
		/** Centred in the header row; also the dialog's accessible name. */
		title: string;
		/** A line of context under the header. */
		description?: string;
		/** Header start, e.g. a Cancel action (`BottomSheet.Action`). */
		leading?: Snippet;
		/** Header end, e.g. Done or the one prominent action. */
		trailing?: Snippet;
		/** Opened from inside Now Playing (z-60): lifts overlay and sheet to z-70. */
		nested?: boolean;
		/**
		 * md+ only: the header's own X for a sheet with no leading/trailing action. Pass false when
		 * the body already ends in a Cancel (an action sheet or a confirmation), so the dialog
		 * does not offer two ways out side by side. Compact always keeps its grabber.
		 */
		showClose?: boolean;
		/**
		 * `grouped` (default): the #F2F2F7 / #1C1C1E sheet that GroupedList cells sit on.
		 * `plain`: white in light, for a sheet whose content is not in cells.
		 */
		surface?: "grouped" | "plain";
		onOpenChange?: (open: boolean) => void;
		children: Snippet;
		/** Classes for the scrolling body (it has no side padding of its own). */
		bodyClass?: string;
	};

	let {
		open = $bindable(false),
		ref = $bindable(null),
		title,
		description,
		leading,
		trailing,
		nested = false,
		showClose = true,
		surface = "grouped",
		onOpenChange,
		children,
		class: className,
		bodyClass,
		...restProps
	}: Props = $props();

	// One boundary for "compact" across the app (below md). Compact is a bottom sheet with a
	// grabber and drag-to-dismiss; md+ is a centred dialog with the same header.
	const isMobile = new IsMobile();
	const compact = $derived(isMobile.current);

	let overlayEl: HTMLDivElement | null = $state(null);

	// Drag-to-dismiss on the grabber + header (the body keeps its own scrolling). The sheet follows
	// the finger with no transition; on release it dismisses past 25% of its height or on a flick
	// faster than DISMISS_VELOCITY, else it springs back. Transform/opacity only, written straight
	// onto the elements. Under Reduce Motion the drag fades the sheet instead of moving it.
	const DRAG_SLOP = 6;
	type Drag = {
		pointerId: number;
		startY: number;
		lastY: number;
		lastT: number;
		velocity: number;
		height: number;
		active: boolean;
		reduced: boolean;
	};
	let drag: Drag | null = null;
	let dragZone: HTMLElement | null = $state(null);

	// Moves and the release are followed on window, not the zone: before the drag is claimed (and
	// the pointer captured) a quick flick is over the body within a frame, and a mouse would lose it.
	function listen(on: boolean): void {
		if (on) {
			window.addEventListener("pointermove", onpointermove);
			window.addEventListener("pointerup", onpointerup);
			window.addEventListener("pointercancel", onpointercancel);
		} else {
			window.removeEventListener("pointermove", onpointermove);
			window.removeEventListener("pointerup", onpointerup);
			window.removeEventListener("pointercancel", onpointercancel);
		}
	}
	$effect(() => () => listen(false));

	// The grabber's 44pt strip is its 16px row plus GRAB_REACH above it, over the dimmed backdrop,
	// so it never pushes the header down. A sheet standing nearly full height (History's filters)
	// has less room than that above it, and the strip would run off the top of the screen; then it
	// takes the room there is and reaches the rest of the way down, over the header's centred title
	// (never its side buttons: the strip is only the grabber's width). Measured from layout
	// (offsetHeight, not the rect) so the slide-in transform does not count, and it does not
	// change the sheet's height, so the measurement cannot feed back into itself.
	const GRAB_REACH = 28;
	let grabUp = $state(GRAB_REACH);
	$effect(() => {
		const el = ref;
		if (!open || !compact || !el) return;
		const measure = () => {
			const room = Math.floor(window.innerHeight - el.offsetHeight);
			grabUp = Math.max(0, Math.min(GRAB_REACH, room));
		};
		measure();
		const ro = new ResizeObserver(measure);
		ro.observe(el);
		window.addEventListener("resize", measure);
		return () => {
			ro.disconnect();
			window.removeEventListener("resize", measure);
		};
	});

	// The page behind an open sheet leaves the accessibility tree (see inert-app.ts).
	$effect(() => (open ? holdAppInert() : undefined));

	function paint(offset: number, height: number, reduced: boolean): void {
		const progress = height > 0 ? Math.min(1, offset / height) : 0;
		if (ref) {
			if (reduced) ref.style.opacity = String(1 - progress);
			else ref.style.transform = offset > 0 ? `translate3d(0, ${offset}px, 0)` : "";
		}
		if (overlayEl) overlayEl.style.opacity = String(1 - progress);
	}

	function settle(el: HTMLElement | null): void {
		if (!el) return;
		el.style.transition = `transform 350ms ${EASE_PRESENT}, opacity 350ms ${EASE_PRESENT}`;
		el.style.transform = "";
		el.style.opacity = "";
		el.addEventListener("transitionend", () => (el.style.transition = ""), { once: true });
	}

	function onpointerdown(event: PointerEvent): void {
		// One finger at a time: a second pointer never takes over (or restarts) the drag.
		if (!compact || drag || !event.isPrimary || event.button !== 0) return;
		drag = {
			pointerId: event.pointerId,
			startY: event.clientY,
			lastY: event.clientY,
			lastT: event.timeStamp,
			velocity: 0,
			height: ref?.offsetHeight ?? 0,
			active: false,
			reduced: prefersReducedMotion()
		};
		listen(true);
	}

	function onpointermove(event: PointerEvent): void {
		if (!drag || event.pointerId !== drag.pointerId) return;
		const dy = event.clientY - drag.startY;
		if (!drag.active) {
			// Below the slop a press is still a tap (Cancel/Done/the grabber keep their click);
			// an upward move is not a dismiss, so let it go.
			if (dy < -DRAG_SLOP) {
				drag = null;
				listen(false);
				return;
			}
			if (dy < DRAG_SLOP) return;
			drag.active = true;
			// Capture only once it is a drag, so a tap's click still lands on the button pressed.
			dragZone?.setPointerCapture(event.pointerId);
			if (ref) ref.style.transition = "none";
			if (overlayEl) overlayEl.style.transition = "none";
		}
		const dt = event.timeStamp - drag.lastT;
		if (dt > 0) {
			const instant = (event.clientY - drag.lastY) / dt;
			drag.velocity = drag.velocity * 0.2 + instant * 0.8;
		}
		drag.lastY = event.clientY;
		drag.lastT = event.timeStamp;
		paint(Math.max(0, dy), drag.height, drag.reduced);
	}

	function onpointerup(event: PointerEvent): void {
		if (!drag || event.pointerId !== drag.pointerId) return;
		const ended = drag;
		drag = null;
		listen(false);
		if (!ended.active) return;
		const offset = Math.max(0, event.clientY - ended.startY);
		// A finger that rested before lifting is not a flick, whatever its last speed was.
		const velocity = event.timeStamp - ended.lastT > 80 ? 0 : ended.velocity;
		if (offset > ended.height * 0.25 || velocity > DISMISS_VELOCITY) {
			// Close from where the finger left it: tw-animate's exit keyframes only define `to`, so
			// the slide-out starts at the inline transform (and the overlay at its inline opacity).
			if (ref) ref.style.transition = "";
			if (overlayEl) overlayEl.style.transition = "";
			open = false;
			onOpenChange?.(false);
		} else {
			settle(ref);
			settle(overlayEl);
		}
	}

	function onpointercancel(event: PointerEvent): void {
		if (!drag || event.pointerId !== drag.pointerId) return;
		const ended = drag;
		drag = null;
		listen(false);
		if (!ended.active) return;
		settle(ref);
		settle(overlayEl);
	}
</script>

{#snippet header()}
	<!-- Leading · centred title · trailing. The side columns keep their buttons whole; the title
	     truncates between them and stays centred while there is room. -->
	<div class="grid min-h-11 grid-cols-[1fr_minmax(0,auto)_1fr] items-center gap-2 px-4">
		<div class="flex min-w-0 items-center justify-start">{@render leading?.()}</div>
		<DialogPrimitive.Title class="text-headline truncate text-center">{title}</DialogPrimitive.Title>
		<div class="flex min-w-0 items-center justify-end">
			{#if trailing}
				{@render trailing()}
			{:else if !compact && !leading && showClose}
				<!-- md+ has no grabber: give a sheet with no actions of its own a way out. -->
				<DialogPrimitive.Close>
					{#snippet child({ props })}
						<Button variant="ghost" size="icon" class="-mr-2 rounded-full" {...props}>
							<XIcon />
							<span class="sr-only">Close</span>
						</Button>
					{/snippet}
				</DialogPrimitive.Close>
			{/if}
		</div>
	</div>
{/snippet}

<DialogPrimitive.Root bind:open {onOpenChange}>
	<DialogPrimitive.Portal>
		<DialogPrimitive.Overlay
			bind:ref={overlayEl}
			data-slot="bottom-sheet-overlay"
			class={cn(
				"data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 fixed inset-0 bg-black/40 [--tw-animation-duration:350ms] [--tw-ease:cubic-bezier(0.32,0.72,0,1)]",
				nested ? "z-[70]" : "z-50"
			)}
		/>
		<DialogPrimitive.Content
			bind:ref
			data-slot="bottom-sheet-content"
			data-elevated=""
			data-compact={compact || undefined}
			class={cn(
				"text-foreground fixed flex flex-col outline-none",
				nested ? "z-[70]" : "z-50",
				surface === "plain" ? "bg-card-elevated dark:bg-sheet" : "bg-sheet",
				compact
					? "data-open:animate-in data-closed:animate-out data-open:slide-in-from-bottom data-closed:slide-out-to-bottom inset-x-0 bottom-0 max-h-[calc(100svh-env(safe-area-inset-top)-12px)] rounded-t-3xl shadow-[0_-8px_32px_rgb(0_0_0/0.12)] [--tw-animation-duration:350ms] [--tw-ease:cubic-bezier(0.32,0.72,0,1)]"
					: "data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 data-closed:zoom-out-95 data-open:zoom-in-95 ring-foreground/10 top-1/2 left-1/2 max-h-[85svh] w-[calc(100%-2rem)] max-w-lg -translate-x-1/2 -translate-y-1/2 rounded-xl shadow-[0_10px_40px_rgb(0_0_0/0.18)] ring-1 [--tw-animation-duration:200ms] [--tw-ease:cubic-bezier(0.23,1,0.32,1)]",
				className
			)}
			{...restProps}
		>
			{#if compact}
				<!-- The drag zone. touch-action:none so the browser never claims the gesture for a
				     scroll; select-none so a slow drag does not start a text selection. -->
				<div bind:this={dragZone} class="shrink-0 touch-none select-none" role="presentation" {onpointerdown}>
					<!-- The grabber doubles as a Close button (VoiceOver, Switch Control). Its 44pt hit
					     strip reaches up past the sheet's top edge, over the dimmed backdrop, instead of
					     pushing the header down — or partly down, near the top of the screen (grabUp). -->
					<DialogPrimitive.Close
						style="--grab-up: {grabUp}px; --grab-down: {GRAB_REACH - grabUp}px"
						class="relative z-10 mx-auto flex h-4 w-24 justify-center outline-none after:absolute after:inset-x-0 after:top-[calc(var(--grab-up)*-1)] after:bottom-[calc(var(--grab-down)*-1)] focus-visible:[&>span]:ring-2 focus-visible:[&>span]:ring-ring"
					>
						<span aria-hidden="true" class="bg-muted-foreground/40 mt-[5px] block h-[5px] w-9 rounded-full"></span>
						<span class="sr-only">Close</span>
					</DialogPrimitive.Close>
					{@render header()}
				</div>
			{:else}
				<div class="shrink-0 pt-2">{@render header()}</div>
			{/if}
			<div
				data-slot="bottom-sheet-body"
				class={cn(
					"min-h-0 flex-1 overflow-y-auto overscroll-contain",
					compact ? "pb-[max(16px,env(safe-area-inset-bottom))]" : "pb-4",
					bodyClass
				)}
			>
				{#if description}
					<DialogPrimitive.Description class="text-footnote text-muted-foreground px-4 pb-3 text-center text-balance">
						{description}
					</DialogPrimitive.Description>
				{/if}
				{@render children()}
			</div>
		</DialogPrimitive.Content>
	</DialogPrimitive.Portal>
</DialogPrimitive.Root>
