<script lang="ts">
	import type { Component, Snippet } from "svelte";
	import { Button } from "$lib/components/ui/button/index.js";
	import { cn } from "$lib/utils.js";

	/**
	 * The one blank-screen idiom: a muted glyph, a headline that says what happened, a hint that says
	 * what to do next, and — when there is something to do — a gray capsule that does it. Every
	 * list's "nothing here" (a search miss, a filter dead end, an empty letter) uses this, so they
	 * read and sit alike; a search miss always offers to clear the search.
	 */
	type Props = {
		icon?: Component<{ class?: string; "aria-hidden"?: boolean | "true" | "false" }>;
		title: string;
		/** One sentence of next step, as text or a snippet (for inline emphasis). */
		hint?: string | Snippet;
		/** The next step as a button: "Clear search", "Clear filters", "Show all". */
		action?: { label: string; onclick: () => void };
		class?: string;
	};
	const { icon: Icon, title, hint, action, class: className }: Props = $props();
</script>

<div
	role="status"
	class={cn("flex flex-col items-center gap-2 px-8 py-16 text-center", className)}
>
	{#if Icon}
		<Icon class="text-muted-foreground mb-1 size-7" aria-hidden="true" />
	{/if}
	<p class="text-headline text-foreground md:text-[15px]">{title}</p>
	{#if hint}
		<p class="text-subheadline text-muted-foreground max-w-xs text-balance md:text-[13px]">
			{#if typeof hint === "string"}{hint}{:else}{@render hint()}{/if}
		</p>
	{/if}
	{#if action}
		<Button
			variant="gray"
			class="text-primary mt-2 h-11 rounded-full px-5 md:h-8 md:px-3.5"
			onclick={action.onclick}
		>
			{action.label}
		</Button>
	{/if}
</div>
