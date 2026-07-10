<script lang="ts" module>
  import type { SourceFileState } from '$lib/api-client';

  // One quiet status signal per row: a small colored dot + sentence-case label.
  // `pendingHint` is the placeholder shown in the destination column while a file has no
  // destination yet — it must reflect *why* there's no destination for that state, not assume
  // the file is still awaiting fingerprinting (a matched file has already been fingerprinted).
  const STATE_META: Record<SourceFileState, { label: string; dot: string; pendingHint: string }> = {
    written: {
      label: 'In library',
      dot: 'bg-primary',
      pendingHint: 'in library'
    },
    matched: {
      label: 'Matched',
      dot: 'bg-primary/50',
      pendingHint: 'awaiting library build'
    },
    review: {
      label: 'Needs review',
      dot: 'bg-amber-500',
      pendingHint: 'needs review'
    },
    failed: {
      label: 'No match',
      dot: 'bg-red-500',
      pendingHint: 'no match'
    },
    queued: {
      label: 'Queued',
      dot: 'bg-muted-foreground/40',
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
</script>

<div
  class="border-border/50 hover:bg-muted/40 flex items-center gap-3 border-b py-1.5 pr-2 text-xs last:border-b-0"
  style="padding-left: {depth * 18 + 30}px"
>
  <!-- filename with a muted extension prefix -->
  <span class="flex min-w-0 flex-1 items-baseline gap-1.5">
    <span class="text-muted-foreground shrink-0">.{ext}</span>
    <span class="text-foreground truncate" title={file.fileName}>{cleanDisplayName(file.fileName)}</span>
  </span>

  <!-- state: dot + sentence-case label + optional confidence -->
  <span class="text-muted-foreground inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap">
    <span class={cn('size-1.5 shrink-0 rounded-full', meta.dot)} aria-hidden="true"></span>
    <span>{meta.label}</span>
    {#if file.matchConfidence != null}
      <span class="text-muted-foreground/70 tabular-nums">{file.matchConfidence.toFixed(2)}</span>
    {/if}
  </span>

  <!-- size -->
  <span class="text-muted-foreground hidden w-16 shrink-0 text-right tabular-nums sm:block">
    {formatFileSize(file.fileSizeBytes)}
  </span>

  <!-- destination -->
  <span class="hidden min-w-0 flex-1 truncate md:block">
    {#if file.destinationPath}
      <span class="text-muted-foreground/60 mr-1">→</span>
      <span class="text-muted-foreground">{file.destinationPath}</span>
    {:else}
      <span class="text-muted-foreground/50 italic">— {meta.pendingHint} —</span>
    {/if}
  </span>
</div>
