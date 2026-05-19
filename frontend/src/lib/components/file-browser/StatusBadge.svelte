<script lang="ts">
  import { Badge } from '$lib/components/ui/badge';
  import { CheckCircle2, Loader2, Eye, Clock, AlertCircle } from '@lucide/svelte';
  type Props = { status: string };
  const { status }: Props = $props();
</script>

{#if status === 'complete'}
  <Badge class="bg-primary/10 text-primary hover:bg-primary/20">
    <CheckCircle2 class="mr-1.5 size-3" />
    Enriched
  </Badge>
{:else if status === 'processing'}
  <Badge class="bg-chart-2/10 text-chart-2 hover:bg-chart-2/20">
    <Loader2 class="mr-1.5 size-3 animate-spin" />
    Processing
  </Badge>
{:else if status === 'needsreview'}
  <Badge class="bg-amber-500/10 text-amber-600 hover:bg-amber-500/20 dark:text-amber-500">
    <Eye class="mr-1.5 size-3" />
    Needs Review
  </Badge>
{:else if status === 'pending'}
  <Badge variant="secondary">
    <Clock class="mr-1.5 size-3" />
    Pending
  </Badge>
{:else if status === 'failed'}
  <Badge variant="destructive">
    <AlertCircle class="mr-1.5 size-3" />
    Failed
  </Badge>
{/if}
