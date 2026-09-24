<script lang="ts">
	import { Toaster as Sonner, type ToasterProps as SonnerProps } from "svelte-sonner";
	import { mode } from "mode-watcher";
	import Loader2Icon from '@lucide/svelte/icons/loader-2';
	import CircleCheckIcon from '@lucide/svelte/icons/circle-check';
	import OctagonXIcon from '@lucide/svelte/icons/octagon-x';
	import InfoIcon from '@lucide/svelte/icons/info';
	import TriangleAlertIcon from '@lucide/svelte/icons/triangle-alert';

	let { ...restProps }: SonnerProps = $props();

	// svelte-sonner holds a toast's countdown only while a pointer hovers or presses the stack, so
	// a keyboard or screen-reader user who moves to an Undo toast watches it vanish as they get
	// there. Focus inside the stack is made to count as hovering it: focus arriving replays the
	// list's own `mouseenter` (it expands the stack and holds every timer, remaining time kept) and
	// focus leaving replays `mouseleave`. Both are non-bubbling events the list listens to itself,
	// so nothing else in the app sees them. The two positions differ by more than a pixel because
	// the list ignores a mouseleave that did not move (a Firefox workaround). A toast that is
	// removed while focused (its Undo pressed) takes the focus with it without a focusout, so a
	// change to the held list's children also checks whether focus is still inside.
	$effect(() => {
		let held: HTMLElement | null = null;
		const listOf = (target: EventTarget | null) =>
			target instanceof Element ? target.closest<HTMLElement>("[data-sonner-toaster]") : null;
		const replay = (list: HTMLElement, type: "mouseenter" | "mouseleave", at: number) =>
			list.dispatchEvent(new MouseEvent(type, { clientX: at, clientY: at }));
		const observer = new MutationObserver(() => {
			if (held && !held.contains(document.activeElement)) release();
		});
		function hold(list: HTMLElement) {
			if (held === list) return;
			release();
			held = list;
			replay(list, "mouseenter", -1);
			observer.observe(list, { childList: true, subtree: true });
		}
		function release() {
			const list = held;
			if (!list) return;
			held = null;
			observer.disconnect();
			if (list.isConnected) replay(list, "mouseleave", 1);
		}
		const onFocusIn = (event: FocusEvent) => {
			const list = listOf(event.target);
			if (list) hold(list);
			else release();
		};
		const onFocusOut = (event: FocusEvent) => {
			if (held && listOf(event.relatedTarget) !== held) release();
		};
		document.addEventListener("focusin", onFocusIn);
		document.addEventListener("focusout", onFocusOut);
		return () => {
			document.removeEventListener("focusin", onFocusIn);
			document.removeEventListener("focusout", onFocusOut);
			release();
		};
	});
</script>

<!--
	Token colours only. `richColors` is forced off after the spread: its green/red/amber toast
	fills are neither the app's tokens nor contrast-checked; status reads from the tinted icon.
-->
<Sonner
	theme={mode.current}
	class="toaster group"
	style="--normal-bg: var(--color-popover); --normal-text: var(--color-popover-foreground); --normal-border: var(--color-border); --border-radius: var(--radius-xl);"
	{...restProps}
	richColors={false}
>
	{#snippet loadingIcon()}
		<Loader2Icon class="size-4 animate-spin" />
	{/snippet}
	{#snippet successIcon()}
		<CircleCheckIcon class="text-primary size-4" />
	{/snippet}
	{#snippet errorIcon()}
		<OctagonXIcon class="text-destructive-text size-4" />
	{/snippet}
	{#snippet infoIcon()}
		<InfoIcon class="size-4" />
	{/snippet}
	{#snippet warningIcon()}
		<TriangleAlertIcon class="text-warning-text size-4" />
	{/snippet}
</Sonner>
