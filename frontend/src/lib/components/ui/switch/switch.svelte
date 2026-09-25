<script lang="ts">
	import { Switch as SwitchPrimitive } from "bits-ui";
	import { cn, type WithoutChildrenOrChild } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		checked = $bindable(false),
		size = "default",
		...restProps
	}: WithoutChildrenOrChild<SwitchPrimitive.RootProps> & {
		size?: "sm" | "default";
	} = $props();
</script>

<!--
	iOS switch. Touch gets the iOS geometry (51×31 track, 27px white thumb with its two-layer
	shadow); a mouse keeps the compact desktop size. The off track is --switch-off at every
	pointer size — state is carried by the thumb's position plus the tint fill, as on iOS, not by
	the off track's contrast; Increase Contrast rings the off track and edges the thumb.
-->
<SwitchPrimitive.Root
	bind:ref
	bind:checked
	data-slot="switch"
	data-size={size}
	class={cn(
		"data-[state=checked]:bg-primary data-[state=unchecked]:bg-switch-off focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive dark:aria-invalid:border-destructive/50 shrink-0 rounded-full border border-transparent focus-visible:ring-3 aria-invalid:ring-3 not-pointer-coarse:data-[size=default]:h-[18.4px] not-pointer-coarse:data-[size=default]:w-[32px] not-pointer-coarse:data-[size=sm]:h-[14px] not-pointer-coarse:data-[size=sm]:w-[24px] pointer-coarse:h-[31px] pointer-coarse:w-[51px] pointer-coarse:p-px contrast-more:data-[state=unchecked]:ring-1 contrast-more:data-[state=unchecked]:ring-inset contrast-more:data-[state=unchecked]:ring-border peer group/switch relative inline-flex items-center transition-all duration-200 ease-[cubic-bezier(0.23,1,0.32,1)] outline-none after:absolute after:-inset-x-3 after:-inset-y-2 data-disabled:cursor-not-allowed data-disabled:opacity-50",
		className
	)}
	{...restProps}
>
	<SwitchPrimitive.Thumb
		data-slot="switch-thumb"
		class="pointer-events-none block rounded-full bg-white shadow-[0_1px_3px_rgb(0_0_0/0.18),0_1px_1px_rgb(0_0_0/0.06)] ring-0 transition-transform duration-200 ease-[cubic-bezier(0.23,1,0.32,1)] not-pointer-coarse:group-data-[size=default]/switch:size-4 not-pointer-coarse:group-data-[size=sm]/switch:size-3 not-pointer-coarse:data-[state=checked]:translate-x-[calc(100%-2px)] not-pointer-coarse:rtl:data-[state=checked]:translate-x-[calc(-100%)] pointer-coarse:size-[27px] pointer-coarse:shadow-[0_3px_8px_rgb(0_0_0/0.15),0_3px_1px_rgb(0_0_0/0.06)] pointer-coarse:data-[state=checked]:translate-x-5 pointer-coarse:rtl:data-[state=checked]:-translate-x-5 data-[state=unchecked]:translate-x-0 contrast-more:border contrast-more:border-border"
	/>
</SwitchPrimitive.Root>
