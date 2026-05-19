<script lang="ts">
  import type { Component } from 'svelte';
  type Props = {
    icon: Component<{ class?: string }>;
    label: string;
    value: string;
  };
  const { icon: Icon, label, value }: Props = $props();
</script>

<div class="flex items-center justify-between">
  <div class="text-muted-foreground flex items-center gap-2 text-sm">
    <Icon class="size-4" />
    <span>{label}</span>
  </div>
  <span class="text-sm font-medium">{value}</span>
</div>
