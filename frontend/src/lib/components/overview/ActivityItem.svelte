<script lang="ts">
  import { Badge } from '$lib/components/ui/badge';
  import { CheckCircle2, AlertCircle, XCircle, Disc3, Sparkles } from '@lucide/svelte';

  type Activity = {
    type: 'discovered' | 'copied' | 'enriched' | 'review' | 'failed';
    track: string;
    artist: string;
    time: string;
  };

  type Props = { activity: Activity };
  const { activity }: Props = $props();

  const config = {
    discovered: { icon: Disc3, color: 'text-foreground', bg: 'bg-secondary', label: 'Discovered' },
    copied: { icon: CheckCircle2, color: 'text-primary', bg: 'bg-primary/10', label: 'Copied' },
    enriched: {
      icon: Sparkles,
      color: 'text-blue-400',
      bg: 'bg-blue-400/10',
      label: 'Enriched'
    },
    review: {
      icon: AlertCircle,
      color: 'text-amber-400',
      bg: 'bg-amber-400/10',
      label: 'Needs Review'
    },
    failed: { icon: XCircle, color: 'text-red-400', bg: 'bg-red-400/10', label: 'Failed' }
  } as const;

  const entry = $derived(config[activity.type]);
</script>

<div class="hover:bg-secondary/50 flex items-center gap-3 rounded-lg p-2 transition-colors">
  <div class="flex size-8 shrink-0 items-center justify-center rounded-lg {entry.bg}">
    <entry.icon class="size-4 {entry.color}" />
  </div>
  <div class="min-w-0 flex-1">
    <p class="truncate text-sm font-medium">{activity.track}</p>
    <p class="text-muted-foreground truncate text-xs">{activity.artist}</p>
  </div>
  <div class="shrink-0 text-right">
    <Badge variant="outline" class="text-xs">{entry.label}</Badge>
    <p class="text-muted-foreground mt-1 text-xs">{activity.time}</p>
  </div>
</div>
