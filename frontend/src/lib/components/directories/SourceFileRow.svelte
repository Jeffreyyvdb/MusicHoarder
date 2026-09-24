<script lang="ts" module>
  import type { SourceFileState } from '$lib/api-client';

  // One quiet status signal per row: a small coloured dot + sentence-case label, the dot in the
  // same status tokens as the folder bars and the page legend.
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
      dot: 'bg-warning',
      pendingHint: 'needs review'
    },
    failed: {
      label: 'No match',
      dot: 'bg-destructive',
      pendingHint: 'no match'
    },
    queued: {
      label: 'Queued',
      dot: 'bg-muted-foreground-dim',
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

<!-- A phone stacks the name over its state (44pt); a desktop keeps one dense line with the size
     and destination columns. -->
<div
  class="border-separator hover:bg-accent flex min-h-11 flex-col justify-center gap-0.5 border-b py-1.5 pr-3 pl-[calc(min(var(--depth),3)*14px_+_32px)] last:border-b-0 md:min-h-8 md:flex-row md:items-center md:justify-start md:gap-3 md:pr-2 md:pl-[calc(var(--depth)*18px_+_30px)]"
  style:--depth={depth}
>
  <!-- filename with a muted extension prefix -->
  <span class="text-subheadline flex min-w-0 items-baseline gap-1.5 md:flex-1 md:text-xs">
    <span class="text-muted-foreground shrink-0">.{ext}</span>
    <span class="text-foreground truncate" title={file.fileName}>{cleanDisplayName(file.fileName)}</span>
  </span>

  <!-- state: dot + sentence-case label + optional confidence (+ size on a phone) -->
  <span class="text-muted-foreground text-footnote inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap md:text-xs">
    <span class={cn('size-1.5 shrink-0 rounded-full', meta.dot)} aria-hidden="true"></span>
    <span>{meta.label}</span>
    {#if file.matchConfidence != null}
      <span class="text-muted-foreground-dim tabular-nums">{file.matchConfidence.toFixed(2)}</span>
    {/if}
    <span class="text-muted-foreground-dim tabular-nums md:hidden">· {formatFileSize(file.fileSizeBytes)}</span>
  </span>

  <!-- size -->
  <span class="text-muted-foreground hidden w-16 shrink-0 text-right text-xs tabular-nums md:block">
    {formatFileSize(file.fileSizeBytes)}
  </span>

  <!-- destination -->
  <span class="hidden min-w-0 flex-1 truncate text-xs md:block">
    {#if file.destinationPath}
      <span class="text-muted-foreground-dim mr-1" aria-hidden="true">→</span>
      <span class="text-muted-foreground font-mono">{file.destinationPath}</span>
    {:else}
      <span class="text-muted-foreground-dim italic">— {meta.pendingHint} —</span>
    {/if}
  </span>
</div>
