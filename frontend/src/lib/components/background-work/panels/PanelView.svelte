<script lang="ts">
  import type { Component } from 'svelte';
  import type { BackgroundPanel } from '#lib/background-work/types.ts';
  import { PANEL_VIEWS } from './panel-registry.ts';

  type Props = { panel: BackgroundPanel; now?: number };
  let { panel, now }: Props = $props();

  // The registry types each view for its own kind; the lookup by `panel.kind` is what pairs them.
  const View = $derived(
    PANEL_VIEWS[panel.kind] as Component<{ panel: BackgroundPanel; now?: number }>
  );
</script>

<View {panel} {now} />
