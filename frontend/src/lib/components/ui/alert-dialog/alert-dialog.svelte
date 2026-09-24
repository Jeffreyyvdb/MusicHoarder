<script lang="ts" module>
	import { getContext } from "svelte";

	const CLOSE = Symbol("alert-dialog-close");

	/** The enclosing AlertDialog's close, for its Action (bits' own Action never closes). */
	export function getAlertDialogClose(): (() => void) | undefined {
		return getContext<(() => void) | undefined>(CLOSE);
	}
</script>

<script lang="ts">
	import { setContext } from "svelte";
	import { AlertDialog as AlertDialogPrimitive } from "bits-ui";

	let { open = $bindable(false), onOpenChange, ...restProps }: AlertDialogPrimitive.RootProps = $props();

	// bits-ui's AlertDialog.Action is a plain button: it never closed its dialog, so every confirm
	// had to remember to, and the ones that forgot stayed up over their own result. Our Action
	// closes through this after its onclick. Setting `open` programmatically does not make bits
	// call onOpenChange, so a controlled dialog (`open={…}` + onOpenChange) is told here; the guard
	// keeps an Action that already closed its dialog from reporting it twice.
	setContext(CLOSE, () => {
		if (!open) return;
		open = false;
		onOpenChange?.(false);
	});
</script>

<AlertDialogPrimitive.Root bind:open {onOpenChange} {...restProps} />
