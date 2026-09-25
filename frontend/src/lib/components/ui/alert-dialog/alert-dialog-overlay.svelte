<script lang="ts">
	import { AlertDialog as AlertDialogPrimitive } from "bits-ui";
	import { cn } from "$lib/utils.js";

	let {
		ref = $bindable(null),
		class: className,
		...restProps
	}: AlertDialogPrimitive.OverlayProps = $props();
</script>

<!-- z-80, with the content: an alert is the topmost modal whatever it was opened over — a bottom
     sheet (z-50, and a sibling alert declared before the sheet portals in first, so at an equal
     z-index it opened UNDER it), Now Playing (z-60) and its nested sheets (z-70). -->
<AlertDialogPrimitive.Overlay
	bind:ref
	data-slot="alert-dialog-overlay"
	class={cn("data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 bg-black/30 dark:bg-black/50 duration-100 fixed inset-0 z-[80]", className)}
	{...restProps}
/>
