// local/sideways-scroller for the markup of .svelte files, which oxlint's JS plugins never see
// (they get the <script> blocks only, and cannot report outside them). Checks the same strings
// the rule does there: attribute text, and string and template literals in markup expressions.
// Usage: bun oxlint/svelte-markup.mjs [dir | file.svelte...]   (default: src)
import { readFileSync, readdirSync } from 'node:fs';
import { join, relative } from 'node:path';
import { parse } from 'svelte/compiler';
import { isSidewaysWithoutY, sidewaysMessage } from './local.mjs';

const IGNORED = /^src\/lib\/stores\//;

/** Adds every string under `node` the rule applies to, as [start offset, text], to `found`. */
function collectStrings(node, inAttribute, found) {
  if (Array.isArray(node)) {
    for (const child of node) collectStrings(child, inAttribute, found);
    return found;
  }
  if (!node || typeof node !== 'object') return found;
  if (node.type === 'Text') {
    if (inAttribute) found.push([node.start, node.data]);
    return found;
  }
  if (node.type === 'Literal' && typeof node.value === 'string')
    found.push([node.start, node.value]);
  if (node.type === 'TemplateElement') found.push([node.start, node.value.raw]);
  for (const [key, child] of Object.entries(node)) {
    if (key === 'metadata' || key === 'parent') continue;
    collectStrings(child, inAttribute || (node.type === 'Attribute' && key === 'value'), found);
  }
  return found;
}

function lineColumn(source, offset) {
  const before = source.slice(0, offset);
  const line = before.split('\n').length;
  return [line, offset - before.lastIndexOf('\n')];
}

/** Problems in one .svelte file, as `path:line:column: message` lines. */
export function checkSvelteMarkup(path, source) {
  if (!/overflow-x-/.test(source)) return [];
  const { fragment } = parse(source, { modern: true });
  const problems = [];
  for (const [start, text] of collectStrings(fragment, false, [])) {
    if (!isSidewaysWithoutY(text)) continue;
    const [line, column] = lineColumn(source, start);
    problems.push(`${path}:${line}:${column}: local(sideways-scroller): ${sidewaysMessage}`);
  }
  return problems;
}

if (import.meta.main) {
  const args = process.argv.slice(2);
  const files = args.some((arg) => arg.endsWith('.svelte'))
    ? args
    : readdirSync(args[0] ?? 'src', { recursive: true }).map((file) =>
        join(args[0] ?? 'src', file)
      );
  const problems = files
    .filter((path) => path.endsWith('.svelte') && !IGNORED.test(relative('.', path)))
    .sort()
    .flatMap((path) => checkSvelteMarkup(path, readFileSync(path, 'utf8')));
  for (const problem of problems) console.error(problem);
  if (problems.length > 0) process.exitCode = 1;
}
