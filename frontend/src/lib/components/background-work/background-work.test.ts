import { describe, expect, it } from 'vitest';
import { createRawSnippet, type Component } from 'svelte';
import { render } from 'svelte/server';
import { FIXTURE_DETAILS, FIXTURE_NOW, FIXTURE_SUMMARY } from '#lib/background-work/fixtures.ts';
import BackgroundJobDetailBody from './BackgroundJobDetailBody.svelte';
import BackgroundWorkSection from './BackgroundWorkSection.svelte';
import PanelView from './panels/PanelView.svelte';
import { PANEL_VIEWS } from './panels/panel-registry.ts';

const NOW = Date.parse(FIXTURE_NOW);
const header = createRawSnippet(() => ({ render: () => '<span>Background work</span>' }));

const html = <P extends Record<string, unknown>>(component: Component<P>, props: P) =>
  render(component, { props }).body;

describe('PanelView', () => {
  it('draws every kind of panel through its own view', () => {
    const kinds = Object.keys(PANEL_VIEWS).sort();
    expect(kinds).toEqual(['bars', 'breakdown', 'figures', 'library-map', 'series']);
    for (const kind of kinds) {
      const panel = Object.values(FIXTURE_DETAILS)
        .flatMap((d) => d.panels)
        .find((p) => p.kind === kind)!;
      const body = html(PanelView, { panel, now: NOW });
      expect(body, kind).toContain(`data-kind="${kind}"`);
      expect(body, kind).toContain(panel.title);
    }
  });

  it('gives every panel a table of the same numbers for assistive technology', () => {
    for (const detail of Object.values(FIXTURE_DETAILS)) {
      for (const panel of detail.panels) {
        const body = html(PanelView, { panel, now: NOW });
        if (panel.kind === 'figures') continue;
        expect(body, panel.title).toContain('<table');
      }
    }
  });

  it('stacks the lyrics breakdown legend in the order the API gave', () => {
    const panel = FIXTURE_DETAILS.lyrics.panels[0];
    const body = html(PanelView, { panel });
    const order = [
      'Synced by LRCLIB',
      'Official words, timed by AI',
      'Words without timing',
      'Not found anywhere'
    ];
    const positions = order.map((label) => body.indexOf(label));
    expect([...positions].sort((a, b) => a - b)).toEqual(positions);
    expect(positions.every((p) => p >= 0)).toBe(true);
    expect(body).toContain('11,402');
  });

  it('marks the allowance ceilings on its hourly chart and keys them', () => {
    const panel = FIXTURE_DETAILS['transcription-allowance'].panels[0];
    const body = html(PanelView, { panel });
    expect(body).toContain('Our hourly ceiling 6,000 s');
    expect(body).toContain('role="slider"');
    expect(body).toContain('border-dashed');
  });

  it('turns the library map into columns of done, waiting and excluded', () => {
    const panel = FIXTURE_DETAILS.lyrics.panels.find((p) => p.kind === 'library-map');
    if (panel?.kind !== 'library-map') throw new Error('fixture lost its library map');
    const body = html(PanelView, { panel });
    expect(body.match(/rounded-\[2px\]/g)).toHaveLength(panel.bins.length);
    expect(body).toContain('Oldest end at');
    expect(body).toContain('Lyrics found:');
  });
});

describe('BackgroundJobDetailBody', () => {
  it('shows the summary and then every panel in order', () => {
    const detail = FIXTURE_DETAILS['transcription-allowance'];
    const body = html(BackgroundJobDetailBody, { summary: null, detail, now: NOW });
    expect(body).toContain('Transcription allowance');
    expect(body).toContain('Audio the transcription service may hear');
    const at = detail.panels.map((p) => body.indexOf(p.title));
    expect(at.every((i) => i >= 0)).toBe(true);
    expect([...at].sort((a, b) => a - b)).toEqual(at);
  });

  it('says how the estimate was made', () => {
    const body = html(BackgroundJobDetailBody, {
      summary: null,
      detail: FIXTURE_DETAILS.lyrics,
      now: NOW
    });
    expect(body).toContain('rate of 64 songs a day');
  });

  it('holds the row in place while the detail loads, and says so when it cannot', () => {
    const summary = FIXTURE_SUMMARY.jobs[0];
    const loading = html(BackgroundJobDetailBody, { summary, detail: null, now: NOW });
    expect(loading).toContain(summary.title);
    expect(loading).toContain('aria-busy="true"');
    const failed = html(BackgroundJobDetailBody, {
      summary,
      detail: null,
      error: 'Nope',
      now: NOW
    });
    expect(failed).toContain('role="alert"');
    expect(failed).toContain('Nope');
  });
});

describe('BackgroundWorkSection', () => {
  const props = (over: Record<string, unknown> = {}) => ({
    summary: FIXTURE_SUMMARY,
    onopen: () => {},
    header,
    now: NOW,
    ...over
  });

  it('lists every job with its headline, state, coverage and estimate', () => {
    const body = html(BackgroundWorkSection, props());
    for (const job of FIXTURE_SUMMARY.jobs) expect(body).toContain(`data-job="${job.id}"`);
    expect(body).toContain('13,195');
    expect(body).toContain('of 21,006 songs have synced lyrics');
    expect(body).toContain('role="meter"');
    expect(body).toMatch(/At this pace: about 33 days, around (28 Dec|Dec 28)/);
    // How the estimate was made is the sheet's, not the row's.
    expect(body).not.toContain('rate of 64 songs a day');
    expect(body).toContain('Working');
    expect(body).toContain('Failing');
    expect(body).toContain('songs synced per day');
  });

  it('reads the trend aloud with its numbers', () => {
    const body = html(BackgroundWorkSection, props());
    expect(body).toMatch(/aria-label="songs synced per day, last 14 days, oldest first: [\d, ]+"/);
  });

  it('shows a placeholder before the first answer and a message after a failed one', () => {
    expect(html(BackgroundWorkSection, props({ summary: null }))).toContain('aria-busy="true"');
    const failed = html(BackgroundWorkSection, props({ summary: null, error: "Couldn't load." }));
    expect(failed).toMatch(/Couldn(&#39;|')t load\./);
    expect(failed).not.toContain('data-job=');
  });
});
