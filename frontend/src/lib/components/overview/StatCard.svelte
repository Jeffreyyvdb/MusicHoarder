<script lang="ts">
  import type { Component } from 'svelte';
  import { Card, CardContent } from '$lib/components/ui/card';

  type Props = {
    icon: Component<{ class?: string }>;
    label: string;
    value: number;
    color: string;
    bgColor: string;
    href?: string;
  };

  const { icon: Icon, label, value, color, bgColor, href }: Props = $props();
</script>

{#snippet body()}
  <Card class={href ? 'hover:bg-secondary/50 transition-colors' : ''}>
    <CardContent class="p-4">
      <div class="flex items-center gap-3">
        <div class="flex size-10 items-center justify-center rounded-lg {bgColor}">
          <Icon class="size-5 {color}" />
        </div>
        <div>
          <p class="text-2xl font-bold">{value.toLocaleString()}</p>
          <p class="text-muted-foreground text-xs">{label}</p>
        </div>
      </div>
    </CardContent>
  </Card>
{/snippet}

{#if href}
  <a {href}>{@render body()}</a>
{:else}
  {@render body()}
{/if}
