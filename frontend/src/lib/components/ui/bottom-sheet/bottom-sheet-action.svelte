<script lang="ts">
	import type { ComponentProps } from "svelte";
	import { Button } from "$lib/components/ui/button/index.js";
	import { cn } from "$lib/utils.js";

	type Props = Omit<ComponentProps<typeof Button>, "variant" | "size"> & {
		/** The sheet's confirming action (Done, Add): semibold, like a nav bar's done item. */
		prominent?: boolean;
	};

	let { ref = $bindable(null), prominent = false, class: className, children, ...restProps }: Props = $props();
</script>

<!--
	A text action for a BottomSheet header (`leading` / `trailing`): tint text, no fill, 44pt tall
	on compact, pulled out to the header's edge so the label lines up with the 16px margin.
-->
<Button
	bind:ref
	variant="ghost"
	class={cn(
		"text-primary hover:text-primary aria-expanded:text-primary text-body h-11 px-2 -mx-2 md:h-8 md:text-sm",
		prominent && "font-semibold",
		className
	)}
	{...restProps}
>
	{@render children?.()}
</Button>
