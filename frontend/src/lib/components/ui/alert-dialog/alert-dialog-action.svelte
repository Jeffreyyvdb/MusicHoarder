<script lang="ts">
	import { AlertDialog as AlertDialogPrimitive } from "bits-ui";
	import {
		buttonVariants,
		type ButtonVariant,
		type ButtonSize,
	} from "$lib/components/ui/button/index.js";
	import { cn } from "$lib/utils.js";
	import { getAlertDialogClose } from "./alert-dialog.svelte";

	let {
		ref = $bindable(null),
		class: className,
		variant = "default",
		size = "default",
		onclick,
		...restProps
	}: AlertDialogPrimitive.ActionProps & {
		variant?: ButtonVariant;
		size?: ButtonSize;
	} = $props();

	// The action closes its dialog once its own onclick has run, as an alert's buttons do on
	// iOS; an onclick that must keep the dialog up (say, a validation failure) calls
	// preventDefault(). See alert-dialog.svelte for why bits does not do this itself.
	const close = getAlertDialogClose();
	function handleClick(event: MouseEvent & { currentTarget: EventTarget & HTMLButtonElement }) {
		onclick?.(event);
		if (!event.defaultPrevented) close?.();
	}
</script>

<AlertDialogPrimitive.Action
	bind:ref
	data-slot="alert-dialog-action"
	class={cn(buttonVariants({ variant, size }), "max-md:h-11 max-md:rounded-full max-md:text-body", "cn-alert-dialog-action", className)}
	onclick={handleClick}
	{...restProps}
/>
