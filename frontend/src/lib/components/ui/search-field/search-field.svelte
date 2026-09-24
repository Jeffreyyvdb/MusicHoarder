<script lang="ts">
	import type { HTMLInputAttributes } from "svelte/elements";
	import SearchIcon from "@lucide/svelte/icons/search";
	import XIcon from "@lucide/svelte/icons/x";
	import { cn, type WithElementRef } from "$lib/utils.js";

	type Props = WithElementRef<Omit<HTMLInputAttributes, "type" | "value" | "class">, HTMLInputElement> & {
		value?: string;
		/** Accessible name; also the placeholder unless one is given. */
		label: string;
		/** Called after the clear button empties the field. */
		onclear?: () => void;
		/** Classes for the capsule (width, margins). */
		class?: string;
	};

	let {
		ref = $bindable(null),
		value = $bindable(""),
		label,
		placeholder,
		onclear,
		class: className,
		...restProps
	}: Props = $props();

	function clear(): void {
		value = "";
		ref?.focus();
		onclear?.();
	}
</script>

<!--
	The iOS search field: a filled capsule with the magnifier leading and, once there is a query, a
	clear button trailing (a 44pt hit area around the small filled x). 44pt tall and 16px text on
	compact so iOS never zooms on focus; the desktop 32px / 14px at md+. The iOS keyboard contract
	(search keyboard, Search return key, no autocapitalise/autocorrect) is set here, callers can
	still override it. WebKit's own cancel glyph is hidden in favour of the clear button.
-->
<div
	data-slot="search-field"
	class={cn(
		"bg-input has-[input:focus-visible]:ring-ring relative flex h-11 min-w-0 items-center rounded-full transition-shadow has-[input:focus-visible]:ring-2 md:h-8",
		className
	)}
>
	<SearchIcon
		aria-hidden="true"
		class="text-muted-foreground pointer-events-none absolute left-3 size-[18px] md:left-2.5 md:size-4"
	/>
	<input
		bind:this={ref}
		bind:value
		type="search"
		aria-label={label}
		placeholder={placeholder ?? label}
		inputmode="search"
		enterkeyhint="search"
		autocapitalize="off"
		autocorrect="off"
		spellcheck={false}
		class="placeholder:text-muted-foreground h-full w-full min-w-0 bg-transparent pr-11 pl-10 text-base outline-none md:pr-8 md:pl-8 md:text-sm [&::-webkit-search-cancel-button]:appearance-none"
		{...restProps}
	/>
	{#if value}
		<button
			type="button"
			aria-label="Clear search"
			class="group/clear absolute right-0 grid size-11 place-items-center rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring md:size-8"
			onclick={clear}
		>
			<span class="bg-muted-foreground-dim group-hover/clear:bg-muted-foreground text-background grid size-[18px] place-items-center rounded-full md:size-4">
				<XIcon class="size-3 md:size-2.5" strokeWidth={3} />
			</span>
		</button>
	{/if}
</div>
