import type {
  BackgroundJobDetail,
  BackgroundJobId,
  BackgroundJobSummary,
  BackgroundPanel,
  BackgroundWorkSummary,
  LibraryMapPanel,
  SeriesPanel
} from './types.ts';

// A summary and one detail per job, shaped like a real owner library (21,006 songs, 13,195 with
// synced lyrics, an hourly audio allowance of 6,000 s against the provider's 7,200). The Pipeline
// page's tests render it, and it is what a screenshot of the section is taken from.

export const FIXTURE_NOW = '2026-11-25T10:30:00Z';
const NOW = Date.parse(FIXTURE_NOW);
const HOUR = 3_600_000;
const DAY = 86_400_000;

const iso = (ms: number) => new Date(ms).toISOString();
const startOfUtcDay = (ms: number) => Math.floor(ms / DAY) * DAY;
const startOfUtcHour = (ms: number) => Math.floor(ms / HOUR) * HOUR;

/** A repeatable wobble in 0 to 1, so a chart has a shape and a test sees the same one every run. */
function wobble(seed: number): number {
  const x = Math.sin(seed * 12.9898 + 78.233) * 43_758.5453;
  return x - Math.floor(x);
}

const dayStarts = (count: number) =>
  Array.from({ length: count }, (_, i) => startOfUtcDay(NOW) - (count - 1 - i) * DAY);
const hourStarts = (count: number) =>
  Array.from({ length: count }, (_, i) => startOfHour(count - 1 - i));
function startOfHour(back: number) {
  return startOfUtcHour(NOW) - back * HOUR;
}

function trend(base: number, spread: number, seed: number, today = 0.45): number[] {
  return Array.from({ length: 14 }, (_, i) => {
    const value = Math.round(base + (wobble(seed + i) - 0.5) * 2 * spread);
    return i === 13 ? Math.round(value * today) : Math.max(0, value);
  });
}

function dailySeries(
  title: string,
  caption: string,
  series: SeriesPanel['series'],
  weights: number[],
  base: number,
  limits: SeriesPanel['limits'] = [],
  format: SeriesPanel['format'] = 'count',
  seed = 1
): SeriesPanel {
  const total = weights.reduce((a, b) => a + b, 0);
  return {
    kind: 'series',
    title,
    caption,
    bucket: 'day',
    format,
    stacked: true,
    series,
    points: dayStarts(14).map((start, i) => {
      const day = base * (0.6 + wobble(seed + i) * 0.8) * (i === 13 ? 0.45 : 1);
      return { startUtc: iso(start), values: weights.map((w) => Math.round((day * w) / total)) };
    }),
    limits
  };
}

// ── lyrics ──────────────────────────────────────────────────────────────────────────────────────

const LIBRARY_SONGS = 21_006;

function lyricsMap(): LibraryMapPanel {
  const binCount = 60;
  const perBin = LIBRARY_SONGS / binCount;
  // The library grows faster the later it goes, so the oldest bins span years and the newest weeks.
  const from = Date.parse('2009-02-01T00:00:00Z');
  const at = (i: number) => from + (NOW - from) * (1 - (1 - i / binCount) ** 1.7);
  // The sweep works in from both ends: the oldest 19 slices and the newest 12 are done.
  const doneShare = (i: number) =>
    i < 19
      ? 0.96
      : i < 23
        ? 0.6 - (i - 19) * 0.15
        : i < 45
          ? 0.02
          : i < 48
            ? 0.3 + (i - 45) * 0.2
            : 0.92;
  const bins = Array.from({ length: binCount }, (_, i) => {
    const excluded = Math.round(perBin * (0.03 + wobble(i) * 0.07));
    const share = Math.max(0, Math.min(1, doneShare(i) + (wobble(i + 90) - 0.5) * 0.04));
    const done = Math.round((perBin - excluded) * share);
    return {
      fromUtc: iso(at(i)),
      toUtc: iso(at(i + 1)),
      done,
      waiting: Math.round(perBin) - excluded - done,
      excluded
    };
  });
  const month = (isoDate: string) =>
    new Date(isoDate).toLocaleDateString('en-GB', {
      month: 'short',
      year: 'numeric',
      timeZone: 'UTC'
    });
  return {
    kind: 'library-map',
    title: 'Where the sweep is working',
    caption: 'The library from the first song added to the newest, in equal slices of songs.',
    doneLabel: 'Lyrics found',
    waitingLabel: 'Still to look up',
    excludedLabel: 'Not looked up (instrumental, no title)',
    bins,
    markers: [
      { label: `Oldest end at ${month(bins[19].fromUtc)}`, atUtc: bins[19].fromUtc },
      { label: `Newest end at ${month(bins[47].toUtc)}`, atUtc: bins[47].toUtc }
    ]
  };
}

const lyricsSummary: BackgroundJobSummary = {
  id: 'lyrics',
  title: 'Lyrics',
  description:
    'Looks every song up on LRCLIB, and when there is no synced version has the transcription service time the official words or write them out.',
  state: 'working',
  stateLabel: 'Working through the middle of the library',
  lastWorkedAtUtc: '2026-11-25T10:21:00Z',
  nextRunAtUtc: null,
  headline: {
    value: 13_195,
    of: LIBRARY_SONGS,
    format: 'count',
    caption: 'of 21,006 songs have synced lyrics',
    etaUtc: '2026-12-28T10:30:00Z',
    etaLabel: "at the last 7 days' rate of 64 songs a day"
  },
  trend: trend(64, 26, 3),
  trendLabel: 'songs synced per day'
};

const lyricsDetail: BackgroundJobDetail = {
  summary: lyricsSummary,
  panels: [
    {
      kind: 'breakdown',
      title: 'Where the lyrics came from',
      caption: 'Every song in the library, by what it has now.',
      total: LIBRARY_SONGS,
      unit: 'songs',
      segments: [
        {
          key: 'lrclib',
          label: 'Synced by LRCLIB',
          count: 11_402,
          tone: 'good',
          note: 'Line-timed words from the community database.'
        },
        {
          key: 'ai-timed',
          label: 'Official words, timed by AI',
          count: 1_793,
          tone: 'ai',
          note: 'LRCLIB had the words but no timing; the transcription service aligned them.'
        },
        {
          key: 'plain',
          label: 'Words without timing',
          count: 1_769,
          tone: 'neutral',
          note: 'Plain text only: LRCLIB 1,321, written by AI 448.'
        },
        {
          key: 'not-found',
          label: 'Not found anywhere',
          count: 5_077,
          tone: 'pending',
          note: 'Tried again every 30 days.'
        },
        { key: 'instrumental', label: 'Instrumental', count: 122, tone: 'muted' },
        {
          key: 'cannot',
          label: "Can't be looked up",
          count: 843,
          tone: 'warning',
          note: 'No title or artist to search by.'
        }
      ]
    },
    lyricsMap(),
    dailySeries(
      'Songs synced per day',
      'The last 14 UTC days, by how the words were synced.',
      [
        { key: 'lrclib', label: 'LRCLIB', tone: 'good' },
        { key: 'ai-timed', label: 'Timed by AI', tone: 'ai' }
      ],
      [78, 22],
      70,
      [],
      'count',
      11
    ),
    {
      kind: 'bars',
      title: 'Attempts by outcome, last 7 days',
      caption: 'One bar per look-up the sweep made.',
      format: 'count',
      bars: [
        { label: 'Synced', value: 402, tone: 'good' },
        { label: 'Timed by AI', value: 96, tone: 'ai' },
        { label: 'Plain only', value: 71, tone: 'neutral' },
        { label: 'Not found', value: 640, tone: 'pending' },
        { label: 'Failed', value: 14, tone: 'bad' }
      ]
    },
    {
      kind: 'figures',
      title: 'Backlog',
      items: [
        {
          label: 'Waiting to look up',
          value: 1_904,
          format: 'count',
          sublabel: 'songs the sweep has not reached',
          tone: 'neutral'
        },
        {
          label: 'Retry of not-found',
          value: 312,
          format: 'count',
          sublabel: 'due again this week',
          tone: 'pending'
        },
        {
          label: 'Last pass',
          format: 'count',
          atUtc: '2026-11-25T10:21:00Z',
          sublabel: 'looked up 30 songs',
          tone: 'good'
        },
        {
          label: 'Failed this week',
          value: 14,
          format: 'count',
          sublabel: 'LRCLIB timed out',
          tone: 'bad'
        }
      ]
    }
  ]
};

// ── transcription allowance ─────────────────────────────────────────────────────────────────────

const ALLOWANCE_SERIES: SeriesPanel['series'] = [
  { key: 'sync', label: 'Timing official words', tone: 'ai' },
  { key: 'write', label: 'Writing words', tone: 'ai' },
  { key: 'video', label: 'Checking music videos', tone: 'neutral' }
];

function allowanceHours(): SeriesPanel {
  return {
    kind: 'series',
    title: 'Audio sent for transcription, per hour',
    caption: 'The last 72 hours, by what the audio was for, against the hourly ceilings.',
    bucket: 'hour',
    format: 'duration',
    stacked: true,
    series: ALLOWANCE_SERIES,
    points: hourStarts(72).map((start, i) => {
      const busy = wobble(i + 5) > 0.18 ? 1 : 0.1;
      const level = 5_200 * busy * (0.55 + wobble(i) * 0.45);
      return {
        startUtc: iso(start),
        values: [Math.round(level * 0.6), Math.round(level * 0.3), Math.round(level * 0.1)].map(
          (v) => (i === 71 ? Math.round(v * 0.3) : v)
        )
      };
    }),
    limits: [
      { label: 'Our hourly ceiling 6,000 s', value: 6_000, tone: 'warning' },
      { label: "Provider's limit 7,200 s", value: 7_200, tone: 'bad' }
    ]
  };
}

const allowanceSummary: BackgroundJobSummary = {
  id: 'transcription-allowance',
  title: 'Transcription allowance',
  description:
    'Audio the transcription service may hear. Lyrics and music-video checks share it, so it is what limits how fast they go.',
  state: 'waiting',
  stateLabel: 'Hourly ceiling reached, resumes at 11:00',
  lastWorkedAtUtc: '2026-11-25T10:14:00Z',
  nextRunAtUtc: '2026-11-25T10:00:00Z',
  headline: {
    value: 15_840,
    of: 20_000,
    format: 'duration',
    caption: "of today's 5 h 33 min allowance used",
    etaUtc: null,
    etaLabel: null
  },
  trend: trend(16_800, 3_400, 7),
  trendLabel: 'audio seconds per day'
};

const allowanceDetail: BackgroundJobDetail = {
  summary: allowanceSummary,
  panels: [
    allowanceHours(),
    dailySeries(
      'Audio sent for transcription, per day',
      'The last 14 UTC days against the daily ceilings.',
      ALLOWANCE_SERIES,
      [60, 30, 10],
      17_000,
      [
        { label: 'Our daily ceiling 20,000 s', value: 20_000, tone: 'warning' },
        { label: "Provider's limit 28,800 s", value: 28_800, tone: 'bad' }
      ],
      'duration',
      21
    ),
    {
      kind: 'breakdown',
      title: "Today's audio, by purpose",
      total: 15_840,
      unit: 'seconds',
      segments: [
        { key: 'sync', label: 'Timing official words', count: 9_360, tone: 'ai' },
        { key: 'write', label: 'Writing words', count: 4_880, tone: 'ai' },
        { key: 'video', label: 'Checking music videos', count: 1_600, tone: 'neutral' }
      ]
    },
    {
      kind: 'figures',
      title: 'What is left',
      items: [
        {
          label: 'This hour',
          value: 0,
          format: 'duration',
          sublabel: 'of 6,000 s spent, resets at 11:00',
          tone: 'warning'
        },
        {
          label: 'Today',
          value: 4_160,
          format: 'duration',
          sublabel: 'left of 20,000 s',
          tone: 'good'
        },
        {
          label: 'Resumes',
          format: 'count',
          atUtc: '2026-11-25T11:00:00Z',
          sublabel: 'next hour starts',
          tone: 'neutral'
        }
      ]
    }
  ]
};

// ── cover art ───────────────────────────────────────────────────────────────────────────────────

const coverSummary: BackgroundJobSummary = {
  id: 'cover-art',
  title: 'Cover art',
  description: 'Finds a cover for albums without one, from the files, the folder and the catalogs.',
  state: 'idle',
  stateLabel: 'Nothing left to look for; checks again after the next scan',
  lastWorkedAtUtc: '2026-11-25T06:02:00Z',
  nextRunAtUtc: '2026-11-25T12:00:00Z',
  headline: {
    value: 20_431,
    of: LIBRARY_SONGS,
    format: 'count',
    caption: 'of 21,006 songs have a cover',
    etaUtc: null,
    etaLabel: null
  },
  trend: trend(9, 8, 31, 0),
  trendLabel: 'covers added per day'
};

const coverDetail: BackgroundJobDetail = {
  summary: coverSummary,
  panels: [
    {
      kind: 'breakdown',
      title: 'Where the covers came from',
      total: LIBRARY_SONGS,
      unit: 'songs',
      segments: [
        { key: 'embedded', label: 'Inside the files', count: 17_880, tone: 'good' },
        { key: 'folder', label: 'A picture in the folder', count: 1_912, tone: 'neutral' },
        { key: 'fetched', label: 'Fetched from a catalog', count: 639, tone: 'ai' },
        {
          key: 'missing',
          label: 'No cover found',
          count: 575,
          tone: 'pending',
          note: 'Looked everywhere it knows; retried monthly.'
        }
      ]
    },
    dailySeries(
      'Covers added per day',
      'The last 14 UTC days.',
      [{ key: 'covers', label: 'Covers', tone: 'good' }],
      [1],
      10,
      [],
      'count',
      41
    ),
    {
      kind: 'figures',
      title: 'Backlog',
      items: [
        {
          label: 'Albums without a cover',
          value: 96,
          format: 'count',
          sublabel: '575 songs',
          tone: 'pending'
        },
        {
          label: 'Last found',
          format: 'count',
          atUtc: '2026-11-25T06:02:00Z',
          sublabel: 'for Heaven Knows',
          tone: 'good'
        }
      ]
    }
  ]
};

// ── music videos ────────────────────────────────────────────────────────────────────────────────

const videosSummary: BackgroundJobSummary = {
  id: 'music-videos',
  title: 'Music videos',
  description:
    'Searches for an official video of songs you play and downloads the ones that pass a check.',
  state: 'working',
  stateLabel: 'Checking 3 candidates',
  lastWorkedAtUtc: '2026-11-25T10:27:00Z',
  nextRunAtUtc: null,
  headline: {
    value: 112,
    of: null,
    format: 'count',
    caption: 'music videos ready to play',
    etaUtc: '2027-02-14T00:00:00Z',
    etaLabel: "at the last 7 days' rate of 9 videos a day, for the 1,022 songs still queued"
  },
  trend: trend(9, 5, 17),
  trendLabel: 'videos added per day'
};

const videosDetail: BackgroundJobDetail = {
  summary: videosSummary,
  panels: [
    {
      kind: 'breakdown',
      title: 'What happened to each search',
      total: 1_290,
      unit: 'songs',
      segments: [
        { key: 'ready', label: 'Ready to play', count: 112, tone: 'good' },
        { key: 'queued', label: 'Queued', count: 41, tone: 'pending' },
        {
          key: 'static',
          label: 'Rejected, a still picture',
          count: 87,
          tone: 'warning',
          note: 'The candidate was an audio upload with a cover image.'
        },
        { key: 'none', label: 'No candidate found', count: 1_022, tone: 'muted' },
        { key: 'failed', label: 'Download failed', count: 28, tone: 'bad' }
      ]
    },
    dailySeries(
      'Videos added per day',
      'The last 14 UTC days.',
      [{ key: 'v', label: 'Ready', tone: 'good' }],
      [1],
      9,
      [],
      'count',
      53
    ),
    {
      kind: 'bars',
      title: 'Download size',
      format: 'bytes',
      bars: [
        { label: 'Under 50 MB', value: 52 * 1024 ** 2, tone: 'neutral' },
        { label: '50 to 100 MB', value: 190 * 1024 ** 2, tone: 'neutral' },
        { label: 'Over 100 MB', value: 330 * 1024 ** 2, tone: 'neutral' }
      ]
    }
  ]
};

// ── quality grading ─────────────────────────────────────────────────────────────────────────────

const qualitySummary: BackgroundJobSummary = {
  id: 'quality-grading',
  title: 'AI quality grading',
  description:
    'Has a language model read each song and album dossier and grade how well it matches.',
  state: 'working',
  stateLabel: 'Grading albums, 24 a minute',
  lastWorkedAtUtc: '2026-11-25T10:29:00Z',
  nextRunAtUtc: null,
  headline: {
    value: 8_640,
    of: LIBRARY_SONGS,
    format: 'count',
    caption: 'of 21,006 songs graded',
    etaUtc: '2027-01-09T00:00:00Z',
    etaLabel: "at the last 7 days' rate of 273 songs a day"
  },
  trend: trend(273, 60, 61),
  trendLabel: 'songs graded per day'
};

const qualityDetail: BackgroundJobDetail = {
  summary: qualitySummary,
  panels: [
    {
      kind: 'bars',
      title: 'Grades so far',
      caption: '1 is a wrong match, 5 is certain.',
      format: 'count',
      bars: [
        { label: '1', value: 211, tone: 'bad' },
        { label: '2', value: 603, tone: 'warning' },
        { label: '3', value: 1_405, tone: 'pending' },
        { label: '4', value: 2_880, tone: 'neutral' },
        { label: '5', value: 3_541, tone: 'good' }
      ]
    },
    dailySeries(
      'Songs graded per day',
      'The last 14 UTC days.',
      [{ key: 'g', label: 'Graded', tone: 'neutral' }],
      [1],
      273,
      [],
      'count',
      71
    ),
    {
      kind: 'figures',
      title: 'Queue',
      items: [
        { label: 'Waiting to grade', value: 12_366, format: 'count', tone: 'neutral' },
        {
          label: 'Needs a second look',
          value: 211,
          format: 'count',
          sublabel: 'sent to the Inbox as AI flagged',
          tone: 'warning'
        }
      ]
    }
  ]
};

// ── enrichment retries ──────────────────────────────────────────────────────────────────────────

const retriesSummary: BackgroundJobSummary = {
  id: 'enrichment-retries',
  title: 'Enrichment retries',
  description: 'Runs songs that did not match again, once a provider or a tag has changed.',
  state: 'idle',
  stateLabel: 'Asleep until the next pass',
  lastWorkedAtUtc: '2026-11-25T04:00:00Z',
  nextRunAtUtc: '2026-11-26T04:00:00Z',
  headline: {
    value: 312,
    of: null,
    format: 'count',
    caption: 'songs waiting for another try',
    etaUtc: null,
    etaLabel: null
  },
  trend: trend(40, 22, 83, 0),
  trendLabel: 'songs retried per day'
};

const retriesDetail: BackgroundJobDetail = {
  summary: retriesSummary,
  panels: [
    {
      kind: 'bars',
      title: 'Retried by provider, last 7 days',
      format: 'count',
      bars: [
        { label: 'AcoustID', value: 144, tone: 'neutral' },
        { label: 'MusicBrainz', value: 96, tone: 'neutral' },
        { label: 'Spotify', value: 61, tone: 'neutral' },
        { label: 'Trackers', value: 19, tone: 'neutral' }
      ]
    },
    dailySeries(
      'Songs retried per day',
      'The last 14 UTC days: matched after another try, and still unmatched.',
      [
        { key: 'm', label: 'Now matched', tone: 'good' },
        { key: 'u', label: 'Still unmatched', tone: 'pending' }
      ],
      [35, 65],
      40,
      [],
      'count',
      91
    ),
    {
      kind: 'figures',
      title: 'Backlog',
      items: [
        { label: 'Waiting', value: 312, format: 'count', tone: 'pending' },
        { label: 'Next pass', format: 'count', atUtc: '2026-11-26T04:00:00Z', tone: 'neutral' }
      ]
    }
  ]
};

// ── album fill and follows ──────────────────────────────────────────────────────────────────────

const followsSummary: BackgroundJobSummary = {
  id: 'album-fill-follows',
  title: 'Album fill and follows',
  description:
    'Downloads the missing tracks of albums you own and new releases from artists you follow.',
  state: 'failing',
  stateLabel: 'Last pass failed: the catalog answered 429',
  lastWorkedAtUtc: '2026-11-24T22:10:00Z',
  nextRunAtUtc: '2026-11-25T11:10:00Z',
  headline: {
    value: 41,
    of: null,
    format: 'count',
    caption: 'tracks queued from albums and follows',
    etaUtc: null,
    etaLabel: null
  },
  trend: trend(18, 12, 101),
  trendLabel: 'tracks queued per day'
};

const followsDetail: BackgroundJobDetail = {
  summary: followsSummary,
  panels: [
    dailySeries(
      'Tracks queued per day',
      'The last 14 UTC days, by why they were queued.',
      [
        { key: 'fill', label: 'Album fill', tone: 'neutral' },
        { key: 'follow', label: 'New releases', tone: 'good' }
      ],
      [70, 30],
      18,
      [],
      'count',
      111
    ),
    {
      kind: 'figures',
      title: 'Follows',
      items: [
        { label: 'Artists followed', value: 18, format: 'count', tone: 'neutral' },
        { label: 'New releases this month', value: 3, format: 'count', tone: 'good' },
        { label: 'Albums still incomplete', value: 212, format: 'count', tone: 'pending' },
        {
          label: 'Failed passes',
          value: 2,
          format: 'count',
          sublabel: 'since midnight UTC',
          tone: 'bad'
        }
      ]
    }
  ]
};

// ── the whole fixture ───────────────────────────────────────────────────────────────────────────

export const FIXTURE_DETAILS: Record<BackgroundJobId, BackgroundJobDetail> = {
  lyrics: lyricsDetail,
  'transcription-allowance': allowanceDetail,
  'cover-art': coverDetail,
  'music-videos': videosDetail,
  'quality-grading': qualityDetail,
  'enrichment-retries': retriesDetail,
  'album-fill-follows': followsDetail
};

export const FIXTURE_SUMMARY: BackgroundWorkSummary = {
  generatedAtUtc: FIXTURE_NOW,
  jobs: Object.values(FIXTURE_DETAILS).map((detail) => detail.summary)
};

export const FIXTURE_PANELS: BackgroundPanel[] = Object.values(FIXTURE_DETAILS).flatMap(
  (d) => d.panels
);
