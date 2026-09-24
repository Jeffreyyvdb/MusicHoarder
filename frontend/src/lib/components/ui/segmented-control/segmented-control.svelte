<script lang="ts" module>
	export type SegmentedControlItem<T extends string = string> = {
		value: T;
		label: string;
		/** Shown after the label in a lighter weight, e.g. "Playlists 12". */
		count?: number | string;
		disabled?: boolean;
	};
</script>

<script lang="ts" generics="T extends string">
	import { cn } from "$lib/utils.js";

	type Props = {
		items: SegmentedControlItem<T>[];
		value?: T;
		/** Accessible name of the control (it is a tablist). */
		label: string;
		onValueChange?: (value: T) => void;
		class?: string;
	};

	let {
		items,
		value = $bindable(),
		label,
		onValueChange,
		class: className,
	}: Props = $props();

	// iOS's rule for a segmented control: two to five short, equal-weight choices. More than that
	// (Settings' seven sections, say) wants a list or a tab strip — say so loudly while developing.
	$effect(() => {
		if (import.meta.env.DEV && items.length > 5) {
			console.error(
				`SegmentedControl "${label}": ${items.length} segments — use at most 5 (a list or SectionTabsV2 for more).`
			);
		}
	});

	const selectedIndex = $derived(items.findIndex((item) => item.value === value));

	let buttons: HTMLButtonElement[] = $state([]);

	function select(index: number, focus = false): void {
		const item = items[index];
		if (!item || item.disabled) return;
		if (focus) buttons[index]?.focus();
		if (item.value === value) return;
		value = item.value;
		onValueChange?.(item.value);
	}

	// Arrow keys move the selection (and focus) like a native tablist, wrapping at the ends;
	// Home/End jump to the ends. Disabled segments are skipped. Only the selected segment is in
	// the Tab order.
	function onkeydown(event: KeyboardEvent): void {
		const n = items.length;
		const enabled = (i: number) => !items[i]?.disabled;
		const steps: Record<string, number> = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 };
		const step = steps[event.key] ?? 0;
		let target = -1;
		if (step !== 0) {
			const from = Math.max(0, selectedIndex);
			for (let i = 1; i <= n && target < 0; i++) {
				const candidate = (((from + step * i) % n) + n) % n;
				if (enabled(candidate)) target = candidate;
			}
		} else if (event.key === "Home") {
			for (let i = 0; i < n && target < 0; i++) if (enabled(i)) target = i;
		} else if (event.key === "End") {
			for (let i = n - 1; i >= 0 && target < 0; i--) if (enabled(i)) target = i;
		}
		if (target < 0) return;
		event.preventDefault();
		select(target, true);
	}
</script>

{#if items.length > 1}
	<!--
		One thumb that slides under the labels (transform only), equal-width segments, 32px visual.
		Each segment's hit area grows to 44pt vertically on touch without growing the control.
	-->
	<div
		role="tablist"
		aria-label={label}
		tabindex={-1}
		data-slot="segmented-control"
		class={cn(
			"bg-muted relative grid w-full min-h-8 auto-cols-fr grid-flow-col rounded-[9px] p-0.5 select-none",
			className
		)}
		{onkeydown}
	>
		{#if selectedIndex >= 0}
			<span
				aria-hidden="true"
				data-slot="segmented-control-thumb"
				class="bg-segmented-thumb pointer-events-none absolute inset-y-0.5 left-0.5 rounded-[7px] shadow-[0_1px_2px_rgb(0_0_0/0.12),0_0_0_0.5px_rgb(0_0_0/0.04)] transition-transform duration-200 ease-[cubic-bezier(0.23,1,0.32,1)]"
				style:width="calc((100% - 4px) / {items.length})"
				style:transform="translateX({selectedIndex * 100}%)"
			></span>
		{/if}
		{#each items as item, i (item.value)}
			{@const selected = i === selectedIndex}
			<button
				bind:this={buttons[i]}
				type="button"
				role="tab"
				aria-selected={selected}
				tabindex={selected || (selectedIndex < 0 && i === 0) ? 0 : -1}
				disabled={item.disabled}
				data-state={selected ? "active" : "inactive"}
				class="text-foreground focus-visible:ring-ring relative z-10 flex min-h-7 min-w-0 items-center justify-center gap-1 rounded-[7px] px-2 text-subheadline font-semibold outline-none after:absolute after:inset-x-0 after:inset-y-0 pointer-coarse:after:-inset-y-2 focus-visible:ring-2 disabled:cursor-not-allowed disabled:opacity-50 md:text-[13px] md:leading-4 md:font-medium"
				onclick={() => select(i)}
			>
				<span class="truncate">{item.label}</span>
				{#if item.count != null}
					<!-- Muted on the track; inherits on the thumb, where muted would fall to 2.3:1 on
					     the dark #636366 thumb. -->
					<span class={cn("font-normal tabular-nums", !selected && "text-muted-foreground")}>{item.count}</span>
				{/if}
			</button>
		{/each}
	</div>
{/if}
