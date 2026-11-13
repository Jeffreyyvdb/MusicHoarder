import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

/**
 * The now-playing equalizer animates for as long as a song plays with its row on screen, so the
 * property it animates decides what every one of those frames costs. A transform runs on the
 * compositor. `height`, which it used to animate, had the browser lay the document out and paint
 * it sixty times a second, for three bars.
 *
 * No test in this suite renders a component, so this reads the rule as it is written, in each of
 * the components that carry a copy of it.
 */
const COPIES = [
  'file-browser/TrackList.svelte',
  'file-browser/AlbumPage.svelte',
  'v2/LibraryV2.svelte'
];

/** Properties a browser animates without layout or paint. */
const COMPOSITED = ['transform', 'opacity'];

function styleOf(file: string): string {
  const source = readFileSync(new URL(`./${file}`, import.meta.url), 'utf8');
  return source.slice(source.lastIndexOf('<style>'));
}

/** The body of the first block that `opener` starts, braces matched. */
function blockAfter(css: string, opener: string): string {
  const start = css.indexOf('{', css.indexOf(opener));
  expect(css.indexOf(opener), `${opener} is missing`).toBeGreaterThanOrEqual(0);
  let depth = 0;
  for (let i = start; i < css.length; i++) {
    if (css[i] === '{') depth += 1;
    else if (css[i] === '}' && --depth === 0) return css.slice(start + 1, i);
  }
  throw new Error(`${opener} never closes`);
}

function propertiesIn(block: string): string[] {
  return [...block.matchAll(/([a-z-]+)\s*:/g)].map((match) => match[1]);
}

describe.each(COPIES)('the now-playing equalizer in %s', (file) => {
  const css = styleOf(file);

  it('animates only what the compositor can animate by itself', () => {
    const animated = propertiesIn(blockAfter(css, '@keyframes mh-eq'));
    expect(animated.length).toBeGreaterThan(0);
    expect(animated.filter((property) => !COMPOSITED.includes(property))).toEqual([]);
  });

  it('scales the bars from their foot, and only while it plays', () => {
    const playing = blockAfter(css, '.mh-eq.is-playing >');
    expect(playing).toMatch(/animation:\s*mh-eq\b/);
    expect(playing).toMatch(/transform-origin:\s*bottom/);
  });

  it('holds still under Reduce Motion, in the pose it rests in', () => {
    const reduced = blockAfter(css, '@media (prefers-reduced-motion: reduce)');
    const bars = blockAfter(reduced, '.mh-eq.is-playing >');
    expect(bars).toMatch(/animation:\s*none/);
    // The resting height: a bar that stopped animating must not stay at the full height it has
    // while a transform draws it shorter.
    const resting = blockAfter(css, '.mh-eq >');
    expect(bars).toContain(resting.match(/height:\s*[^;]+;/)?.[0]);
  });
});
