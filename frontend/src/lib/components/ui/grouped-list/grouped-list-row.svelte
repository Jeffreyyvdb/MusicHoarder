<script lang="ts">
	import type { Component, Snippet } from "svelte";
	import type { HTMLAnchorAttributes, HTMLButtonAttributes } from "svelte/elements";
	import ChevronRightIcon from "@lucide/svelte/icons/chevron-right";
	import { cn, type WithElementRef } from "$lib/utils.js";

	type Props = WithElementRef<
		Omit<HTMLButtonAttributes, "children" | "value"> & Omit<HTMLAnchorAttributes, "children">
	> & {
		/** The row's title. Omit it only when `children` supplies the whole main column. */
		label?: string;
		/** A second line under the label (the row grows to 56px+). */
		sublabel?: string;
		/** Trailing secondary value, e.g. "545 GB" or "On". */
		value?: string | number;
		/** A 29px neutral icon tile before the label (glyph in --foreground on --muted). */
		icon?: Component<{ class?: string; strokeWidth?: number }>;
		/** Overrides the icon tile's look — colour is for status only. */
		iconClass?: string;
		/** Custom leading content instead of an icon tile (album art, an avatar). */
		leading?: Snippet;
		/** Custom trailing content (a Switch, a spinner, a button) — after the value. */
		trailing?: Snippet;
		/** Extra main-column content under the label (a progress bar, a status line). */
		children?: Snippet;
		/** Show a disclosure chevron: this row navigates somewhere. */
		chevron?: boolean;
		/** Red label — a destructive action (put it last, in its own section). */
		destructive?: boolean;
		disabled?: boolean;
		/** The current item of a list used as navigation (the md+ Settings pane list). */
		selected?: boolean;
	};

	let {
		ref = $bindable(null),
		href,
		onclick,
		label,
		sublabel,
		value,
		icon: Icon,
		iconClass,
		leading,
		trailing,
		children,
		chevron = false,
		destructive = false,
		disabled = false,
		selected = false,
		class: className,
		...restProps
	}: Props = $props();

	// <a> when it navigates, <button> when it acts, a plain <div> when it only shows something (a
	// switch row: the Switch in `trailing` is the control, not the row).
	const tag = $derived(href && !disabled ? "a" : onclick ? "button" : "div");
	const interactive = $derived(tag !== "div" && !disabled);
</script>

<!--
	One iOS list cell: min 44pt (56+ with a sublabel), body text on compact and the desktop 14px at
	md+, value trailing in secondary text, a 16px chevron in the tertiary tier. A grid with fixed
	columns (leading · main · value · trailing · chevron; absent ones collapse to 0) so the hairline
	separator — the ::after, placed as a grid item from the main column to the far edge — starts at
	the label whatever the leading content is, like UITableView's; it is dropped after the last row.
	The focus ring is the full-strength tint: it is the row's only focus indicator (no border), and a
	50% ring measured 2.2–2.7:1 against the cell, under the 3:1 non-text minimum.
-->
<svelte:element
	this={tag}
	bind:this={ref}
	data-slot="grouped-list-row"
	data-selected={selected || undefined}
	href={tag === "a" ? href : undefined}
	type={tag === "button" ? "button" : undefined}
	disabled={tag === "button" ? disabled : undefined}
	aria-disabled={disabled || undefined}
	aria-current={selected && tag === "a" ? "page" : undefined}
	onclick={disabled ? undefined : onclick}
	class={cn(
		"group/grouped-row relative grid w-full grid-cols-[auto_minmax(0,1fr)_auto_auto_auto] items-center px-4 py-2 text-left outline-none",
		sublabel ? "min-h-14" : "min-h-11",
		"after:bg-separator after:col-start-2 after:col-end-6 after:row-start-1 after:-mr-4 after:-mb-2 after:h-(--hairline) after:self-end last:after:hidden",
		interactive &&
			"active:bg-accent focus-visible:ring-ring cursor-pointer transition-colors duration-100 hover:bg-accent focus-visible:ring-2 focus-visible:ring-inset",
		selected && "bg-sidebar-accent hover:bg-sidebar-accent",
		disabled && "cursor-not-allowed",
		className
	)}
	{...restProps}
>
	{#if leading}
		<span class="col-start-1 row-start-1 mr-3 flex items-center">{@render leading()}</span>
	{:else if Icon}
		<span
			aria-hidden="true"
			class={cn(
				"bg-muted text-foreground col-start-1 row-start-1 mr-3 flex size-[29px] items-center justify-center rounded-[7px]",
				iconClass
			)}
		>
			<Icon class="size-[18px]" strokeWidth={2} />
		</span>
	{/if}
	<span class="col-start-2 row-start-1 flex min-w-0 flex-col justify-center">
		{#if label}
			<span
				class={cn(
					"text-body md:text-sm",
					destructive ? "text-destructive-text" : disabled ? "text-muted-foreground-dim" : "text-foreground"
				)}
			>
				{label}
			</span>
		{/if}
		{#if sublabel}
			<span class="text-subheadline text-muted-foreground md:text-xs">{sublabel}</span>
		{/if}
		{@render children?.()}
	</span>
	{#if value != null && value !== ""}
		<span
			class="text-body text-muted-foreground col-start-3 row-start-1 ml-3 max-w-[min(50vw,16rem)] truncate text-right tabular-nums md:text-sm"
		>
			{value}
		</span>
	{/if}
	{#if trailing}
		<span class="col-start-4 row-start-1 ml-3 flex items-center">{@render trailing()}</span>
	{/if}
	{#if chevron}
		<ChevronRightIcon
			aria-hidden="true"
			class="text-muted-foreground-dim col-start-5 row-start-1 ml-2 -mr-1 size-4"
			strokeWidth={2.5}
		/>
	{/if}
</svelte:element>
