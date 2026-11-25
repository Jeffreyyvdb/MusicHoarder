// Mirrors MusicHoarder.Api/BackgroundWork/BackgroundWorkContracts.cs. A value the API adds and these
// unions do not carry fails to compile where the client maps it, which is the point.

export type BackgroundJobId =
  | 'lyrics'
  | 'transcription-allowance'
  | 'cover-art'
  | 'music-videos'
  | 'quality-grading'
  | 'enrichment-retries'
  | 'album-fill-follows';

export type BackgroundJobState = 'off' | 'idle' | 'working' | 'waiting' | 'failing' | 'unknown';

/** What a mark means, not its colour. */
export type BackgroundTone = 'good' | 'ai' | 'neutral' | 'pending' | 'warning' | 'bad' | 'muted';

/** `duration` is seconds; `percent` is a 0–1 fraction. */
export type BackgroundFormat = 'count' | 'duration' | 'bytes' | 'percent';

export interface BackgroundHeadline {
  value: number;
  of?: number | null;
  format: BackgroundFormat;
  caption: string;
  etaUtc?: string | null;
  etaLabel?: string | null;
}

export interface BackgroundJobSummary {
  id: BackgroundJobId;
  title: string;
  description: string;
  state: BackgroundJobState;
  stateLabel: string;
  lastWorkedAtUtc?: string | null;
  nextRunAtUtc?: string | null;
  headline: BackgroundHeadline;
  /** Work per UTC day over the last 14 days, oldest first. */
  trend: number[];
  trendLabel: string;
}

export interface BackgroundWorkSummary {
  generatedAtUtc: string;
  jobs: BackgroundJobSummary[];
}

export interface BreakdownSegment {
  key: string;
  label: string;
  count: number;
  tone: BackgroundTone;
  note?: string | null;
}

export interface BreakdownPanel {
  kind: 'breakdown';
  title: string;
  caption?: string | null;
  total: number;
  unit: string;
  segments: BreakdownSegment[];
}

export interface SeriesPanel {
  kind: 'series';
  title: string;
  caption?: string | null;
  bucket: 'hour' | 'day';
  format: BackgroundFormat;
  stacked: boolean;
  series: { key: string; label: string; tone: BackgroundTone }[];
  /** `values[i]` belongs to `series[i]`. */
  points: { startUtc: string; values: number[] }[];
  limits: { label: string; value: number; tone: BackgroundTone }[];
}

export interface LibraryMapPanel {
  kind: 'library-map';
  title: string;
  caption?: string | null;
  doneLabel: string;
  waitingLabel: string;
  excludedLabel: string;
  /** Oldest first, equal song counts per bin. */
  bins: { fromUtc: string; toUtc: string; done: number; waiting: number; excluded: number }[];
  markers: { label: string; atUtc: string }[];
}

export interface BarsPanel {
  kind: 'bars';
  title: string;
  caption?: string | null;
  format: BackgroundFormat;
  bars: { label: string; value: number; tone: BackgroundTone }[];
}

export interface FiguresPanel {
  kind: 'figures';
  title: string;
  caption?: string | null;
  items: {
    label: string;
    value?: number | null;
    format: BackgroundFormat;
    atUtc?: string | null;
    sublabel?: string | null;
    tone: BackgroundTone;
  }[];
}

export type BackgroundPanel = BreakdownPanel | SeriesPanel | LibraryMapPanel | BarsPanel | FiguresPanel;

export interface BackgroundJobDetail {
  summary: BackgroundJobSummary;
  panels: BackgroundPanel[];
}
