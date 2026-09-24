<script lang="ts" module>
	import { cn, twMergeConfig, type WithElementRef } from "$lib/utils.js";
	import type { HTMLAnchorAttributes, HTMLButtonAttributes } from "svelte/elements";
	import { type VariantProps, tv } from "tailwind-variants";

	export const buttonVariants = tv({
		base: "focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive dark:aria-invalid:border-destructive/50 rounded-lg border border-transparent bg-clip-padding text-sm font-medium focus-visible:ring-3 active:not-aria-[haspopup]:scale-[0.97] aria-invalid:ring-3 [&_svg:not([class*='size-'])]:size-4 group/button inline-flex shrink-0 items-center justify-center whitespace-nowrap transition-[color,background-color,border-color,box-shadow,transform,opacity] duration-[120ms] ease-out outline-none select-none disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0",
		variants: {
			variant: {
				// Prominent (iOS "filled"): the one or two most likely actions in a view.
				default: "bg-primary text-primary-foreground hover:bg-primary/90",
				// iOS "gray": a borderless fill-secondary capsule/box. `outline` is kept as an alias
				// so the ~90 existing call sites (and AlertDialog.Cancel) move off hard-edged white
				// boxes in one step; `bordered` is the old outline look for where a stroke is wanted.
				// Hover is --secondary-hover — never bg-secondary/80 or bg-accent, which are LIGHTER
				// than the translucent rest fill.
				outline: "border-transparent bg-secondary text-foreground hover:bg-secondary-hover hover:text-foreground aria-expanded:bg-secondary-hover aria-expanded:text-foreground",
				gray: "border-transparent bg-secondary text-foreground hover:bg-secondary-hover hover:text-foreground aria-expanded:bg-secondary-hover aria-expanded:text-foreground",
				// iOS "tinted": tint label on a 12% tint wash (5.04:1 over grouped, 4.59 hovered).
				tinted: "bg-primary/12 text-primary hover:bg-primary/18 aria-expanded:bg-primary/18",
				// The pre-redesign outline: an opaque stroke on the page colour. For the SSR landing
				// and anywhere a stroked control is genuinely wanted.
				bordered: "border-border bg-background text-foreground hover:bg-muted hover:text-foreground aria-expanded:bg-muted aria-expanded:text-foreground dark:bg-transparent",
				secondary: "bg-secondary text-secondary-foreground hover:bg-secondary-hover aria-expanded:bg-secondary-hover aria-expanded:text-secondary-foreground",
				ghost: "hover:bg-muted hover:text-foreground aria-expanded:bg-muted aria-expanded:text-foreground",
				// Apple-Music-style secondary action: a borderless translucent pill that
				// tints the surface it sits on instead of stamping a hard-edged white card
				// onto it. Use for in-content actions (lyrics tooling, media controls) — the
				// white wash in dark picks up the cover colour behind Now Playing, which the
				// grey --secondary fill would not.
				subtle:
					"rounded-full bg-foreground/[0.06] text-foreground hover:bg-foreground/[0.1] hover:text-foreground dark:bg-white/[0.1] dark:text-white dark:hover:bg-white/[0.16] dark:hover:text-white aria-expanded:bg-foreground/[0.1] aria-expanded:text-foreground",
				// Tinted red. The rest tint is light enough that the label clears 4.5:1 on every
				// surface it lands on, #2C2C2E dialogs included (4.64 dark, 5.10 light over grouped).
				destructive: "bg-destructive/10 hover:bg-destructive/15 focus-visible:ring-destructive/20 dark:focus-visible:ring-destructive/40 dark:bg-destructive/12 text-destructive-text focus-visible:border-destructive/40 dark:hover:bg-destructive/18 aria-expanded:bg-destructive/15",
				link: "text-primary underline-offset-4 hover:underline",
			},
			size: {
				default: "h-8 gap-1.5 px-2.5 has-data-[icon=inline-end]:pr-2 has-data-[icon=inline-start]:pl-2",
				xs: "h-6 gap-1 rounded-[min(var(--radius-md),10px)] px-2 text-xs in-data-[slot=button-group]:rounded-lg has-data-[icon=inline-end]:pr-1.5 has-data-[icon=inline-start]:pl-1.5 [&_svg:not([class*='size-'])]:size-3",
				sm: "h-7 gap-1 rounded-[min(var(--radius-md),12px)] px-2.5 text-[0.8rem] in-data-[slot=button-group]:rounded-lg has-data-[icon=inline-end]:pr-1.5 has-data-[icon=inline-start]:pl-1.5 [&_svg:not([class*='size-'])]:size-3.5",
				lg: "h-9 gap-1.5 px-2.5 has-data-[icon=inline-end]:pr-2 has-data-[icon=inline-start]:pl-2",
				icon: "size-8",
				"icon-xs": "size-6 rounded-[min(var(--radius-md),10px)] in-data-[slot=button-group]:rounded-lg [&_svg:not([class*='size-'])]:size-3",
				"icon-sm": "size-7 rounded-[min(var(--radius-md),12px)] in-data-[slot=button-group]:rounded-lg",
				"icon-lg": "size-9",
				// iOS capsule action (Play / Shuffle, sheet actions): 44pt tall, headline label.
				pill: "h-11 gap-2 rounded-full px-5 text-headline font-semibold [&_svg:not([class*='size-'])]:size-5",
			},
		},
		compoundVariants: [
			// (`rounded-full` lives here, not on the variant, because the size variants set
			// their own radius and would otherwise win the merge.)
			{ variant: "subtle", class: "rounded-full" },
		],
		defaultVariants: {
			variant: "default",
			size: "default",
		},
	}, { twMergeConfig });

	export type ButtonVariant = VariantProps<typeof buttonVariants>["variant"];
	export type ButtonSize = VariantProps<typeof buttonVariants>["size"];

	export type ButtonProps = WithElementRef<HTMLButtonAttributes> &
		WithElementRef<HTMLAnchorAttributes> & {
			variant?: ButtonVariant;
			size?: ButtonSize;
		};
</script>

<script lang="ts">
	let {
		class: className,
		variant = "default",
		size = "default",
		ref = $bindable(null),
		href = undefined,
		type = "button",
		disabled,
		children,
		...restProps
	}: ButtonProps = $props();
</script>

{#if href}
	<a
		bind:this={ref}
		data-slot="button"
		class={cn(buttonVariants({ variant, size }), className)}
		href={disabled ? undefined : href}
		aria-disabled={disabled}
		role={disabled ? "link" : undefined}
		tabindex={disabled ? -1 : undefined}
		{...restProps}
	>
		{@render children?.()}
	</a>
{:else}
	<button
		bind:this={ref}
		data-slot="button"
		class={cn(buttonVariants({ variant, size }), className)}
		{type}
		{disabled}
		{...restProps}
	>
		{@render children?.()}
	</button>
{/if}
