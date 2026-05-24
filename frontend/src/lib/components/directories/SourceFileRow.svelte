<script lang="ts" module>
  import type { SourceFileState } from '$lib/api-client';
  import { Check, TriangleAlert, X, Clock, type Icon } from '@lucide/svelte';

  // Per-extension accent colors, mirrored from the design's extColor map.
  const EXT_COLORS: Record<string, string> = {
    flac: '#3a7a8a',
    mp3: '#8a6a3a',
    m4a: '#6a5a8a',
    wav: '#7a4a4a',
    ogg: '#4a7a4a',
    aiff: '#7a3a7a',
    aac: '#8a5a3a'
  };

  // `pendingHint` is the placeholder shown in the destination column while a file has no
  // destination yet — it must reflect *why* there's no destination for that state, not assume
  // the file is still awaiting fingerprinting (a matched file has already been fingerprinted).
  const STATE_META: Record<
    SourceFileState,
    { label: string; icon: typeof Icon; pill: string; pendingHint: string }
  > = {
    written: {
      label: 'in library',
      icon: Check,
      pill: 'bg-emerald-500/20 text-emerald-700 dark:text-emerald-300',
      pendingHint: 'in library'
    },
    matched: {
      label: 'matched',
      icon: Check,
      pill: 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400',
      pendingHint: 'awaiting library build'
    },
    review: {
      label: 'review',
      icon: TriangleAlert,
      pill: 'bg-amber-500/20 text-amber-700 dark:text-amber-400',
      pendingHint: 'needs review'
    },
    failed: {
      label: 'no match',
      icon: X,
      pill: 'bg-red-500/15 text-red-600 dark:text-red-400',
      pendingHint: 'no match'
    },
    queued: {
      label: 'queued',
      icon: Clock,
      pill: 'bg-muted text-muted-foreground',
      pendingHint: 'awaiting fingerprint'
    }
  };

  function fileExt(name: string, fallback?: string | null): string {
    const m = name.match(/\.([a-z0-9]+)$/i);
    if (m) return m[1].toLowerCase();
    return (fallback ?? '').replace(/^\./, '').toLowerCase() || 'bin';
  }
</script>

<script lang="ts">
  import type { SourceFile } from '$lib/api-client';
  import { cleanDisplayName, formatFileSize } from '$lib/formatters';
  import { cn } from '$lib/utils';

  let { file, depth = 0 }: { file: SourceFile; depth?: number } = $props();

  const ext = $derived(fileExt(file.fileName, file.extension));
  const meta = $derived(STATE_META[file.state] ?? STATE_META.queued);
  const StateIcon = $derived(meta.icon);
</script>

<div
  class="border-border/60 hover:bg-muted/40 flex items-center gap-3 border-b border-dotted py-1 pr-2 text-[11px] last:border-b-0"
  style="padding-left: {depth * 18 + 30}px"
>
  <!-- filename with ext-colored prefix -->
  <span class="flex min-w-0 flex-1 items-baseline gap-1.5 font-mono">
    <span class="shrink-0 font-semibold" style="color: {EXT_COLORS[ext] ?? '#7a7a7a'}">.{ext}</span>
    <span class="text-foreground truncate" title={file.fileName}>{cleanDisplayName(file.fileName)}</span>
  </span>

  <!-- state pill + optional confidence -->
  <span
    class={cn(
      'inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-medium whitespace-nowrap',
      meta.pill
    )}
  >
    <StateIcon class="size-3 shrink-0" aria-hidden="true" />
    <span>{meta.label}</span>
    {#if file.matchConfidence != null}
      <span class="font-mono opacity-70">{file.matchConfidence.toFixed(2)}</span>
    {/if}
  </span>

  <!-- size -->
  <span class="text-muted-foreground hidden w-16 shrink-0 text-right font-mono text-[10px] tabular-nums sm:block">
    {formatFileSize(file.fileSizeBytes)}
  </span>

  <!-- destination -->
  <span class="hidden min-w-0 flex-1 truncate font-mono text-[10.5px] md:block">
    {#if file.destinationPath}
      <span class="text-muted-foreground/60 mr-1">→</span>
      <span class={file.state === 'review' ? 'text-amber-600 italic dark:text-amber-400' : 'text-muted-foreground'}>
        {file.destinationPath}
      </span>
    {:else}
      <span class="text-muted-foreground/50 italic">— {meta.pendingHint} —</span>
    {/if}
  </span>
</div>
