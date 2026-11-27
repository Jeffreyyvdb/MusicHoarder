import type { CleanEditItem, CleanEditProgress, CleanEditVerdict } from '#lib/api-client.ts';

/** The verdicts that mean a file is not the explicit original: the ones the page lists. */
export const FOUND_VERDICTS: readonly CleanEditVerdict[] = ['cleanMaster', 'mutedWords'];

interface VerdictInfo {
  label: string;
  /** What the verdict says about the file, for the summary row's second line. */
  meaning: string;
}

/** Summary rows, in the order a person reads them: what was found, then what settled nothing. */
export const VERDICT_ROWS: readonly (VerdictInfo & { verdict: CleanEditVerdict })[] = [
  {
    verdict: 'cleanMaster',
    label: 'Clean edit',
    meaning: 'Follows the label’s clean edition where the two editions differ.'
  },
  {
    verdict: 'mutedWords',
    label: 'Muted words',
    meaning: 'Words the explicit edition sings are silent in the file.'
  },
  {
    verdict: 'explicit',
    label: 'Explicit original',
    meaning: 'Follows the explicit edition.'
  },
  {
    verdict: 'inconclusive',
    label: 'Not settled',
    meaning: 'The previews do not show a difference to decide by.'
  },
  {
    verdict: 'noCleanEdition',
    label: 'No clean edition sold',
    meaning: 'Only an explicit edition exists, so there is nothing clean to mistake it for.'
  },
  {
    verdict: 'noReference',
    label: 'No preview found',
    meaning: 'The catalogs have no matching explicit preview for this song.'
  },
  {
    verdict: 'failed',
    label: 'Failed',
    meaning: 'A lookup or download failed. The next check tries these again.'
  }
];

export function verdictLabel(verdict: CleanEditVerdict): string {
  return VERDICT_ROWS.find((row) => row.verdict === verdict)?.label ?? verdict;
}

/** Songs that could be checked and have no check yet. */
export function notCheckedCount(
  counts: Record<CleanEditVerdict, number>,
  eligible: number
): number {
  const checked = Object.values(counts).reduce((sum, n) => sum + n, 0);
  return Math.max(0, eligible - checked);
}

/** `73.5` as `1:13`. */
export function clock(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

const SOURCE_NAMES: Record<string, string> = { itunes: 'iTunes', deezer: 'Deezer' };

/** The catalogs a verdict rests on, from reference ids like `itunes:123`. */
export function sourcesOf(item: Pick<CleanEditItem, 'references'>): string {
  const names = new Set(
    item.references.map((ref) => {
      const source = ref.id.split(':')[0] ?? ref.id;
      return SOURCE_NAMES[source] ?? source;
    })
  );
  return [...names].sort().join(' and ');
}

const MAX_TIMES_SHOWN = 4;

/** Why a found song was flagged: which test, and what it saw. */
export function evidenceLine(item: CleanEditItem): string {
  const parts: string[] = [];
  if (item.verdict === 'mutedWords') {
    // Two muted words in one second read the same on a clock.
    const times = [...new Set(item.mutedAtSeconds.map(clock))];
    const shown = times.slice(0, MAX_TIMES_SHOWN).join(', ');
    const more = times.length - MAX_TIMES_SHOWN;
    parts.push(`Muted at ${shown}${more > 0 ? ` and ${more} more` : ''}`);
  } else {
    parts.push('Follows the clean edition');
    if (item.margin != null) parts.push(`${Math.round(item.margin * 100)}% closer to it`);
    if (item.editedSeconds != null) parts.push(`${item.editedSeconds.toFixed(1)} s differ`);
  }
  const sources = sourcesOf(item);
  if (sources) parts.push(`${sources} previews`);
  return parts.join(' · ');
}

/** The subtitle while a run is going, and after one that stopped short. */
export function progressLine(progress: CleanEditProgress): string | null {
  if (progress.running) {
    const where = `${progress.processed.toLocaleString()} of ${progress.total.toLocaleString()}`;
    if (progress.stopping) return `Stopping · ${where}`;
    return progress.current ? `Checking ${where} · ${progress.current}` : `Checking ${where}`;
  }
  return progress.stoppedReason ?? null;
}
