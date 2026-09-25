<script lang="ts">
	import type { HTMLInputAttributes, HTMLInputTypeAttribute } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	type InputType = Exclude<HTMLInputTypeAttribute, "file">;

	type Props = WithElementRef<
		Omit<HTMLInputAttributes, "type"> &
			({ type: "file"; files?: FileList } | { type?: InputType; files?: undefined })
	>;

	let {
		ref = $bindable(null),
		value = $bindable(),
		type,
		files = $bindable(),
		class: className,
		"data-slot": dataSlot = "input",
		...restProps
	}: Props = $props();

	// iOS filled field: a --input FILL with no stroke (the old 1px --input border measured
	// ~1.1:1), 44pt tall below md and the desktop 32px above it, 16px text below md so iOS
	// never zooms on focus. The border stays (transparent) so focus and invalid states can
	// colour it without the field shifting. A search field is a capsule, as on iOS.
	const fieldClass =
		"bg-input border-transparent focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive dark:aria-invalid:border-destructive/50 h-11 md:h-8 rounded-lg border px-3 md:px-2.5 py-1 text-base transition-colors file:h-6 file:text-sm file:font-medium focus-visible:ring-3 aria-invalid:ring-3 md:text-sm file:text-foreground placeholder:text-muted-foreground w-full min-w-0 outline-none file:inline-flex file:border-0 file:bg-transparent disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50";

	// iOS keyboard contract, derived from the type so hand-written call sites can't forget it:
	// search fields get the search keyboard and a Search return key and are never capitalised or
	// autocorrected ("jay-z" must not arrive as "Jay-z"); email fields likewise skip autocorrect.
	// Anything the caller passes explicitly overrides these (restProps is spread after).
	const keyboardDefaults = $derived(
		type === "search"
			? { inputmode: "search", enterkeyhint: "search", autocapitalize: "off", autocorrect: "off", spellcheck: false }
			: type === "email"
				? { inputmode: "email", autocapitalize: "off", autocorrect: "off", spellcheck: false }
				: {}
	) as Record<string, unknown>;
</script>

{#if type === "file"}
	<input
		bind:this={ref}
		data-slot={dataSlot}
		class={cn(fieldClass, className)}
		type="file"
		bind:files
		bind:value
		{...restProps}
	/>
{:else}
	<input
		bind:this={ref}
		data-slot={dataSlot}
		class={cn(fieldClass, type === "search" && "rounded-full px-4 md:px-3", className)}
		{type}
		bind:value
		{...keyboardDefaults}
		{...restProps}
	/>
{/if}
