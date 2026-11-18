import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, type Container, type Rule } from 'postcss';
import { describe, expect, it } from 'vitest';

const HIDES: Record<string, RegExp> = {
  display: /^none$/,
  visibility: /^(hidden|collapse)$/,
  opacity: /^0(\.0+)?%?$/,
  'content-visibility': /^hidden$/,
  'font-size': /^0([a-z%]+)?$/,
  width: /^0([a-z%]+)?$/,
  height: /^0([a-z%]+)?$/,
  'max-width': /^0([a-z%]+)?$/,
  'max-height': /^0([a-z%]+)?$/,
  'clip-path': /inset\(\s*(50|100)%/,
  clip: /^rect\(\s*0/,
  scale: /^0(\s+0)?$/,
  transform: /scale[xy]?\(\s*0\s*[,)]/,
  'pointer-events': /^none$/
};

const GRID = new Set(['grid', 'grid-template', 'grid-template-columns']);

const RULE =
  'A skin may hide only pseudo-elements and elements marked aria-hidden data-mh-decor. Restyle it; if it is decoration, mark it aria-hidden="true" data-mh-decor in its component.';

function splitTopLevel(text: string, separator: RegExp): string[] {
  const parts: string[] = [];
  let depth = 0;
  let quote = '';
  let from = 0;
  for (let i = 0; i < text.length; i += 1) {
    const ch = text[i];
    if (quote) {
      if (ch === '\\') i += 1;
      else if (ch === quote) quote = '';
    } else if (ch === '"' || ch === "'") quote = ch;
    else if (ch === '(' || ch === '[') depth += 1;
    else if (ch === ')' || ch === ']') depth -= 1;
    else if (depth === 0 && separator.test(ch)) {
      parts.push(text.slice(from, i));
      from = i + 1;
    }
  }
  parts.push(text.slice(from));
  return parts.map((part) => part.trim()).filter(Boolean);
}

function subjectOf(selector: string): string {
  return splitTopLevel(selector, /[\s>+~]/).at(-1) ?? '';
}

function withoutArguments(compound: string): string {
  let out = '';
  let depth = 0;
  for (const ch of compound) {
    if (ch === ')') depth -= 1;
    if (depth === 0) out += ch;
    if (ch === '(') depth += 1;
  }
  return out;
}

function mayHide(selector: string): boolean {
  const subject = subjectOf(selector);
  const bare = withoutArguments(subject);
  if (/::[\w-]+|:(before|after)\b/.test(bare)) return true;
  if (/\[data-mh-decor\b/.test(bare)) return true;
  const list = /^:(?:is|where)\((.*)\)$/s.exec(subject);
  return list !== null && splitTopLevel(list[1], /,/).every(mayHide);
}

function selectorsOf(rule: Rule): string[] {
  let parent = rule.parent as Container | undefined;
  while (parent && parent.type === 'atrule') parent = parent.parent as Container | undefined;
  const own = splitTopLevel(rule.selector, /,/);
  if (!parent || parent.type !== 'rule') return own;
  const outer = `:is(${selectorsOf(parent as Rule).join(', ')})`;
  return own.map((s) => (s.includes('&') ? s.replaceAll('&', outer) : `${outer} ${s}`));
}

function inKeyframes(rule: Rule): boolean {
  const parent = rule.parent;
  return (
    parent?.type === 'atrule' &&
    'name' in parent &&
    typeof parent.name === 'string' &&
    parent.name.endsWith('keyframes')
  );
}

function oneLine(text: string): string {
  return text.replace(/\s+/g, ' ');
}

function violations(css: string, file: string, lineOffset = 0): string[] {
  const found: string[] = [];
  parse(css).walkRules((rule) => {
    if (inKeyframes(rule)) return;
    const selectors = selectorsOf(rule);
    rule.each((node) => {
      if (node.type !== 'decl') return;
      const prop = node.prop.toLowerCase();
      const value = node.value
        .replace(/!important$/, '')
        .trim()
        .toLowerCase();
      const at = `${file}:${(node.source?.start?.line ?? 0) + lineOffset}`;
      if (HIDES[prop]?.test(value)) {
        for (const selector of selectors.filter((s) => !mayHide(s))) {
          found.push(`${at} hides "${oneLine(selector)}" (${prop}: ${value}). ${RULE}`);
        }
      }
      if (GRID.has(prop)) {
        for (const selector of selectors.filter((s) => s.includes('data-mh-tracks'))) {
          found.push(
            `${at} sets ${prop} on "${oneLine(selector)}". TrackList's GRID is the only definition of the tracks table; a skin that needs room changes type or spacing, never the columns.`
          );
        }
      }
    });
  });
  return found;
}

const here = fileURLToPath(new URL('.', import.meta.url));
const src = join(here, '..', '..');

function filesIn(dir: string, name: RegExp): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return filesIn(path, name);
    return name.test(entry.name) ? [path] : [];
  });
}

const svelteFiles = (dir: string) => filesIn(dir, /\.svelte$/);

const HIDING_LOOK = /^(['"`])?(.*\b(hidden|sr-only|invisible|opacity-0|collapse)\b.*|0)\1$/;

function lookViolations(source: string, file: string): string[] {
  const found: string[] = [];
  for (const match of source.matchAll(/\bskinLook\(/g)) {
    const open = match.index + match[0].length;
    let depth = 1;
    let end = open;
    for (; end < source.length && depth > 0; end += 1) {
      if (source[end] === '(') depth += 1;
      else if (source[end] === ')') depth -= 1;
    }
    const args = splitTopLevel(source.slice(open, end - 1), /,/);
    if (args.some((arg) => HIDING_LOOK.test(arg))) {
      const line = source.slice(0, match.index).split('\n').length;
      found.push(
        `${file}:${line} skinLook(${args.join(', ')}) can hide something under the skin. A look value changes size, type or colour; a hide belongs in app-skin.css on an element marked data-mh-decor.`
      );
    }
  }
  return found;
}

function skinViolations(): string[] {
  const sheets = ['app-skin.css', 'musichoarder.css'].flatMap((name) =>
    violations(readFileSync(join(here, name), 'utf8'), name)
  );
  const styleBlocks = svelteFiles(here).flatMap((path) => {
    const source = readFileSync(path, 'utf8');
    return [...source.matchAll(/<style[^>]*>([\s\S]*?)<\/style>/g)].flatMap((block) =>
      violations(
        block[1],
        relative(here, path),
        source.slice(0, block.index).split('\n').length - 1
      )
    );
  });
  return [...sheets, ...styleBlocks];
}

function decorTags(source: string): { tag: string; line: number }[] {
  const tags: { tag: string; line: number }[] = [];
  for (const match of source.matchAll(/<[a-zA-Z][\w.:-]*/g)) {
    let depth = 0;
    let quote = '';
    let end = match.index + match[0].length;
    for (; end < source.length; end += 1) {
      const ch = source[end];
      if (quote) {
        if (ch === quote) quote = '';
      } else if (ch === '"' || ch === "'" || ch === '`') quote = ch;
      else if (ch === '{') depth += 1;
      else if (ch === '}') depth -= 1;
      else if (ch === '>' && depth === 0) break;
    }
    const tag = source.slice(match.index, end + 1);
    if (/\sdata-mh-decor\b/.test(tag)) {
      tags.push({ tag, line: source.slice(0, match.index).split('\n').length });
    }
  }
  return tags;
}

function decorViolations(source: string, file: string): string[] {
  return decorTags(source).flatMap(({ tag, line }) => {
    const problems = [
      !/\saria-hidden="true"/.test(tag) && 'has no aria-hidden="true"',
      /^<(a|button|input|select|textarea|label|summary|details)\b/.test(tag) &&
        'is an interactive element',
      /\s(on[a-z]+|href|use:[\w-]+|bind:[\w-]+|tabindex)[\s=>{]/.test(tag) &&
        'carries a handler, a link or a binding'
    ].filter((problem) => problem !== false);
    return problems.map(
      (problem) =>
        `${file}:${line} is marked data-mh-decor but ${problem}. Only decoration a skin may drop carries the marker: nothing a person reads or uses.`
    );
  });
}

describe('the skin contract', () => {
  it('finds a hide of content and lets decoration and pseudo-elements through', () => {
    const fixture = `
      :root[data-mh-skin='hardware'] [data-slot='filter-chip'] > svg { display: none; }
      :root[data-mh-skin='hardware'] :is([data-a], [data-b]) > :is(:nth-child(2), :nth-child(8)) {
        visibility: hidden;
      }
      :root[data-mh-skin='hardware'] [data-x]:not([data-mh-decor]) { opacity: 0; }
      [data-skin='hardware'] [data-mh-video-key] { & > small { font-size: 0; } }
      :root[data-mh-skin='hardware'] [data-mh-tracks='wide'] > [role='button'] {
        grid-template-columns: 40px 1fr;
      }
      :root[data-mh-skin='hardware'] [data-slot='button'] { pointer-events: none; }
      :root[data-mh-skin='hardware'] [data-slot='badge'] { transform: scale(0); }

      :root[data-mh-skin='hardware'] [data-mh-decor='wash'] { display: none; }
      :root[data-mh-skin='hardware'] .nav-search [data-slot='search-field']::before { display: none; }
      [data-skin='hardware'] [data-kit='lcd-body'] { opacity: 0; }
      :root[data-mh-skin='hardware'] [data-slot='button']:active { transform: scale(0.97); }
      :root[data-mh-skin='hardware'] [data-mh-tracks='wide'] { clip-path: inset(0 -8px -8px -8px); }
      :root[data-mh-skin='hardware'] :is([data-mh-decor='a'], [data-mh-decor='b']) { display: none; }
      @keyframes blink { 50% { opacity: 0; } }
    `;
    const found = violations(fixture, 'fixture.css');
    expect(found.map((message) => message.replace(/ \(.*$/, '')).sort()).toEqual(
      [
        `fixture.css:2 hides ":root[data-mh-skin='hardware'] [data-slot='filter-chip'] > svg"`,
        `fixture.css:4 hides ":root[data-mh-skin='hardware'] :is([data-a], [data-b]) > :is(:nth-child(2), :nth-child(8))"`,
        `fixture.css:6 hides ":root[data-mh-skin='hardware'] [data-x]:not([data-mh-decor])"`,
        `fixture.css:7 hides ":is([data-skin='hardware'] [data-mh-video-key]) > small"`,
        `fixture.css:11 hides ":root[data-mh-skin='hardware'] [data-slot='button']"`,
        `fixture.css:12 hides ":root[data-mh-skin='hardware'] [data-slot='badge']"`,
        `fixture.css:16 hides "[data-skin='hardware'] [data-kit='lcd-body']"`,
        `fixture.css:9 sets grid-template-columns on ":root[data-mh-skin='hardware'] [data-mh-tracks='wide'] > [role='button']". TrackList's GRID is the only definition of the tracks table; a skin that needs room changes type or spacing, never the columns.`
      ].sort()
    );
  });

  it('hides only decoration, and leaves the tracks table its columns', () => {
    expect(skinViolations()).toEqual([]);
  });

  it('knows a marker on something that is not decoration', () => {
    const fixture = `
      <img src={url} alt="" aria-hidden="true" data-mh-decor="backdrop" class="blur" />
      <div data-mh-decor="wash" style:background={(() => a > b)()}></div>
      <button aria-hidden="true" data-mh-decor onclick={() => close()}>x</button>
    `;
    expect(decorViolations(fixture, 'Fixture.svelte')).toEqual([
      expect.stringContaining('Fixture.svelte:3 is marked data-mh-decor but has no aria-hidden'),
      expect.stringContaining('Fixture.svelte:4 is marked data-mh-decor but is an interactive'),
      expect.stringContaining('Fixture.svelte:4 is marked data-mh-decor but carries a handler')
    ]);
  });

  it('knows a look value that hides', () => {
    const fixture = `
      const rowH = skinLook(56, 40);
      <div class={skinLook('', 'hidden md:block')}></div>
      <Cover size={skinLook(36, 0)} />
      <span class={skinLook('dark', undefined)}></span>
    `;
    expect(lookViolations(fixture, 'Fixture.svelte')).toEqual([
      expect.stringContaining('Fixture.svelte:3 skinLook'),
      expect.stringContaining('Fixture.svelte:4 skinLook')
    ]);
  });

  it('asks the skin only for look values that keep everything shown', () => {
    const found = filesIn(src, /\.(svelte|ts)$/)
      .filter((path) => !path.endsWith('.test.ts'))
      .flatMap((path) => lookViolations(readFileSync(path, 'utf8'), relative(src, path)));
    expect(found).toEqual([]);
  });

  it('marks only decoration as decoration', () => {
    const found = svelteFiles(src).flatMap((path) =>
      decorViolations(readFileSync(path, 'utf8'), relative(src, path))
    );
    expect(found).toEqual([]);
  });
});
