<script lang="ts" module>
	import { getContext, setContext } from "svelte";
	import type { VariantProps } from "tailwind-variants";
	import { toggleVariants } from "$lib/components/ui/toggle/index.js";

	type ToggleVariants = VariantProps<typeof toggleVariants>;

	interface ToggleGroupContext extends ToggleVariants {
		spacing?: number;
		orientation?: "horizontal" | "vertical";
	}

	export function setToggleGroupCtx(props: ToggleGroupContext) {
		setContext("toggleGroup", props);
	}

	export function getToggleGroupCtx() {
		return getContext<Required<ToggleGroupContext>>("toggleGroup");
	}
</script>

<script lang="ts">
	import { ToggleGroup as ToggleGroupPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		value = $bindable(),
		class: className,
		size = "default",
		spacing = 0,
		orientation = "horizontal",
		variant = "default",
		...restProps
	}: ToggleGroupPrimitive.RootProps &
		ToggleVariants & {
			spacing?: number;
			orientation?: "horizontal" | "vertical";
		} = $props();

	// The `spacing=0` rules below square off the inner corners so items sit flush.
	// A segmented control is the opposite idiom — pill segments inset in a track — so
	// it opts into a hairline gap, which also switches those flush-corner rules off.
	const effectiveSpacing = $derived(variant === "segmented" && spacing === 0 ? 1 : spacing);

	setToggleGroupCtx({
		get variant() {
			return variant;
		},
		get size() {
			return size;
		},
		get spacing() {
			return effectiveSpacing;
		},
		get orientation() {
			return orientation;
		},
	});
</script>

<!--
Discriminated Unions + Destructing (required for bindable) do not
get along, so we shut typescript up by casting `value` to `never`.
-->
<ToggleGroupPrimitive.Root
	bind:value={value as never}
	bind:ref
	{orientation}
	data-slot="toggle-group"
	data-variant={variant}
	data-size={size}
	data-spacing={effectiveSpacing}
	style={`--gap: ${effectiveSpacing}`}
	class={cn(
		"rounded-lg data-[size=sm]:rounded-[min(var(--radius-md),10px)] group/toggle-group flex w-fit flex-row items-center gap-[--spacing(var(--gap))] data-vertical:flex-col data-vertical:items-stretch",
		"data-[variant=segmented]:bg-foreground/[0.06] data-[variant=segmented]:rounded-full data-[variant=segmented]:p-[3px] data-[variant=segmented]:data-[size=sm]:rounded-full dark:data-[variant=segmented]:bg-white/[0.08]",
		className
	)}
	{...restProps}
/>
