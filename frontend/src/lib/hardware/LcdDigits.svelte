<script lang="ts">
  // Digits on the LCD, with their unlit segments showing faintly behind them.
  let { value, ghost }: { value: string; ghost?: string } = $props();
</script>

<span data-kit="lcd-digits">
  <span data-kit="lcd-ghost" aria-hidden="true">{ghost ?? value.replace(/[0-9]/g, '8')}</span>
  <span data-kit="lcd-lit">{value}</span>
</span>
