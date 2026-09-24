/**
 * How a Performance chart's latest value moved against the previous snapshot — and whether that
 * is good news. The sign alone says nothing: a rise in "Match rate" is an improvement, a rise in
 * "Failed" is a regression, and colouring both green (as the page once did) tells the reader the
 * opposite of the truth for half the charts. So each chart states its polarity and the verdict is
 * spelled out in words as well as colour.
 */

export type DeltaTone = 'better' | 'worse' | 'same';

export type DeltaDescription = {
  tone: DeltaTone;
  direction: 'up' | 'down' | 'flat';
  /** Visible copy, e.g. "Up 3% · better". */
  text: string;
};

type DeltaOptions = { format: (v: number) => string; higherIsBetter: boolean };

/** Changes smaller than this are rounding noise between snapshots, not movement. */
const EPSILON = 0.01;

export function describeDelta(
  latest: number | null | undefined,
  previous: number | null | undefined,
  opts: DeltaOptions
): DeltaDescription | null {
  if (latest == null || previous == null) return null;
  const delta = latest - previous;
  // Judged at the precision the card shows: 84.2 → 84.4 reads "84%" twice, and "Up 0% · better"
  // under two identical figures would be noise dressed up as news.
  if (Math.abs(delta) <= EPSILON || opts.format(latest) === opts.format(previous)) {
    return { tone: 'same', direction: 'flat', text: 'No change' };
  }
  const up = delta > 0;
  const better = up === opts.higherIsBetter;
  // The displayed figures differ but the change itself rounds to nothing (84.4 → 84.6 shows
  // 84% → 85% with a 0.2 delta): say it moved a little rather than "Up 0%".
  const amount = opts.format(Math.abs(delta));
  const size = amount === opts.format(0) ? 'slightly' : amount;
  return {
    tone: better ? 'better' : 'worse',
    direction: up ? 'up' : 'down',
    text: `${up ? 'Up' : 'Down'} ${size} · ${better ? 'better' : 'worse'}`
  };
}

/**
 * The delta line for a whole series (oldest → newest). Only a chart with fewer than two captures
 * is a "First capture"; a longer series whose newest or previous point is missing (AI grading
 * switched off, a snapshot taken before a metric existed) says so instead of claiming to be new
 * on a page that lists several versions.
 */
export function describeSeriesDelta(
  series: readonly (number | null | undefined)[],
  opts: DeltaOptions
): DeltaDescription {
  if (series.length < 2) return { tone: 'same', direction: 'flat', text: 'First capture' };
  const latest = series[series.length - 1];
  const previous = series[series.length - 2];
  if (latest == null) return { tone: 'same', direction: 'flat', text: 'No data' };
  if (previous == null) return { tone: 'same', direction: 'flat', text: 'Nothing to compare' };
  return describeDelta(latest, previous, opts)!;
}
