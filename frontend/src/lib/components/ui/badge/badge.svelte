<script lang="ts" module>
	import { type VariantProps, tv } from "tailwind-variants";
	import { twMergeConfig } from "$lib/utils.js";

	export const badgeVariants = tv({
		base: "min-h-5 gap-1 rounded-full border border-transparent px-2 py-0.5 text-caption-1 transition-all has-data-[icon=inline-end]:pr-1.5 has-data-[icon=inline-start]:pl-1.5 [&>svg]:size-3! focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive group/badge inline-flex w-fit shrink-0 items-center justify-center overflow-hidden whitespace-nowrap transition-colors focus-visible:ring-[3px] [&>svg]:pointer-events-none",
		variants: {
			variant: {
				default: "bg-primary text-primary-foreground [a]:hover:bg-primary/80",
				secondary: "bg-secondary text-secondary-foreground [a]:hover:bg-secondary-hover",
				// Semantic tints, light enough that the label clears 4.5:1 on white, grouped and
				// elevated surfaces alike (destructive ≥ 4.64, warning ≥ 4.53).
				destructive: "bg-destructive/10 [a]:hover:bg-destructive/15 focus-visible:ring-destructive/20 dark:focus-visible:ring-destructive/40 text-destructive-text dark:bg-destructive/12",
				warning: "bg-warning/6 text-warning-text dark:bg-warning/15 [a]:hover:bg-warning/10 dark:[a]:hover:bg-warning/20",
				// Only for a badge that is itself a link or a button: green text means "tappable".
				// A positive state you cannot tap is `secondary` with a text-primary glyph
				// (<Badge variant="secondary"><CircleCheck class="text-primary" />Confirmed</Badge>).
				tinted: "bg-primary/12 text-primary [a]:hover:bg-primary/18",
				outline: "border-border text-foreground [a]:hover:bg-muted [a]:hover:text-muted-foreground",
				ghost: "hover:bg-muted hover:text-muted-foreground",
				link: "text-primary underline-offset-4 hover:underline",
			},
		},
		defaultVariants: {
			variant: "default",
		},
	}, { twMergeConfig });

	export type BadgeVariant = VariantProps<typeof badgeVariants>["variant"];
</script>

<script lang="ts">
	import type { HTMLAnchorAttributes } from "svelte/elements";
	import { cn, type WithElementRef } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		href,
		class: className,
		variant = "default",
		children,
		...restProps
	}: WithElementRef<HTMLAnchorAttributes> & {
		variant?: BadgeVariant;
	} = $props();
</script>

<svelte:element
	this={href ? "a" : "span"}
	bind:this={ref}
	data-slot="badge"
	{href}
	class={cn(badgeVariants({ variant }), className)}
	{...restProps}
>
	{@render children?.()}
</svelte:element>
