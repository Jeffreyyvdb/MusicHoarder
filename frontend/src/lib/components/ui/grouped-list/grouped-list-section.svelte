<script lang="ts">
	import type { Snippet } from "svelte";
	import type { HTMLAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	type Props = WithElementRef<Omit<HTMLAttributes<HTMLDivElement>, "children">> & {
		/** Section header — a short sentence-case label, or a snippet for anything richer. */
		header?: string | Snippet;
		/** Explanatory footnote under the section. */
		footer?: string | Snippet;
		/** The rows (GroupedList.Row) or any cell content. */
		children: Snippet;
		/** Classes for the rounded cell container (the section's outer box takes `class`). */
		contentClass?: string;
		/**
		 * Make the header a real heading (<h2>/<h3>) on a long grouped page — a hub, Pipeline,
		 * History, Settings — so VoiceOver's rotor can jump between sections. It looks exactly the
		 * same (preflight resets a heading's size and weight). Leave it off in sheets and on short
		 * lists, and wherever the headers are navigation rather than page structure.
		 */
		headingLevel?: 2 | 3;
	};

	let {
		ref = $bindable(null),
		header,
		footer,
		children,
		class: className,
		contentClass,
		headingLevel,
		...restProps
	}: Props = $props();

	const headerId = $props.id();
</script>

<!--
	iOS inset-grouped section: a footnote header, the cells in one rounded --card container, a
	footnote footer. 16px side margins on compact and inside a BottomSheet at every width (at md+ a
	page's own column supplies the margin); the header and footer text line up with the row
	labels. On a sheet or dialog `[data-elevated]` moves --card up a step, so the same section
	reads on #F2F2F7 / #1C1C1E sheets with no prop.
-->
<div
	bind:this={ref}
	data-slot="grouped-list-section"
	class={cn("mx-4 md:mx-0 md:in-data-[slot=bottom-sheet-body]:mx-4", className)}
	{...restProps}
>
	{#if header}
		<svelte:element
			this={headingLevel ? `h${headingLevel}` : "div"}
			id={headerId}
			class="text-footnote text-muted-foreground px-4 pb-1.5"
		>
			{#if typeof header === "string"}{header}{:else}{@render header()}{/if}
		</svelte:element>
	{/if}
	<div
		role="group"
		aria-labelledby={header ? headerId : undefined}
		data-slot="grouped-list-content"
		class={cn("bg-card text-card-foreground overflow-hidden rounded-xl", contentClass)}
	>
		{@render children()}
	</div>
	{#if footer}
		<div class="text-footnote text-muted-foreground px-4 pt-1.5">
			{#if typeof footer === "string"}{footer}{:else}{@render footer()}{/if}
		</div>
	{/if}
</div>
