<script lang="ts">
  import { Badge } from '$lib/components/ui/badge';
  type Props = { status: string };
  const { status }: Props = $props();

  const isRunning = $derived(status !== 'Idle');
</script>

{#if isRunning}
  <Badge variant="outline" class="border-primary/40 text-primary max-w-[160px] gap-1.5 sm:max-w-xs">
    <span class="bg-primary size-1.5 shrink-0 animate-pulse rounded-full"></span>
    <span class="truncate">{status}</span>
  </Badge>
{:else}
  <Badge variant="outline" class="text-muted-foreground gap-1.5">
    <span class="bg-muted-foreground/60 size-1.5 shrink-0 rounded-full"></span>
    Idle
  </Badge>
{/if}
