// Writes .oxlintrc.json: every oxlint rule of CATEGORIES in PLUGINS is on, except the rules in
// OFF (with their reason) and the rules the current tree breaks. Those go off for the narrowest
// file kind (tests, components, other code) that holds all their findings, with their count
// beside them, so a rule that reaches zero findings turns itself back on. Rerun after an oxlint
// upgrade or a cleanup:
//   bun oxlint/generate-config.mjs
import { execFileSync } from 'node:child_process';
import { rmSync, writeFileSync } from 'node:fs';
import { format, resolveConfig } from 'prettier';

const OUTPUT = '.oxlintrc.json';
const OXLINT = 'node_modules/.bin/oxlint';
const CATEGORIES = ['correctness', 'suspicious', 'perf', 'pedantic', 'style', 'restriction'];
const PLUGINS = ['eslint', 'typescript', 'unicorn', 'oxc', 'import', 'promise', 'node', 'jsdoc'];

/** The rules of CATEGORIES, as `plugin/rule`, type-aware ones (oxlint-tsgolint) included. */
const RULES = JSON.parse(
  execFileSync(OXLINT, ['--rules', '--format', 'json'], { encoding: 'utf8' })
)
  .filter((rule) => CATEGORIES.includes(rule.category))
  .map((rule) => ({ ...rule, id: `${rule.scope}/${rule.value}` }));

/**
 * File kinds, most specific first; a file belongs to the first kind it matches. oxlint's
 * categories do not reach a plugin that only an override turns on, so a kind's own plugins get
 * their rules listed one by one.
 */
const KINDS = [
  {
    name: 'tests',
    files: ['**/*.test.ts'],
    plugins: ['vitest'],
    match: (f) => f.endsWith('.test.ts')
  },
  { name: 'components', files: ['**/*.svelte'], match: (f) => f.endsWith('.svelte') },
  {
    name: 'other',
    files: ['**/*'],
    excludeFiles: ['**/*.test.ts', '**/*.svelte'],
    match: () => true
  }
];

/** Rules that stay off whatever the tree holds. */
const OFF = {
  'eslint/curly': 'prettier owns formatting (eslint-config-prettier turns it off)',
  'unicorn/empty-brace-spaces': 'prettier owns formatting (eslint-config-prettier turns it off)',
  'unicorn/no-nested-ternary': 'prettier owns formatting (eslint-config-prettier turns it off)',
  'unicorn/number-literal-case': 'prettier owns formatting (eslint-config-prettier turns it off)'
};

/** Options for rules that are on. */
const OPTIONS = {
  'eslint/no-unused-vars': [{ argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
  // Vitest's expect takes a message as its second argument.
  'vitest/valid-expect': [{ maxArgs: 2 }]
};
const on = (rule) => (OPTIONS[rule] ? ['error', ...OPTIONS[rule]] : 'error');
const pluginOf = (rule) => rule.split('/')[0];

const SOURCES = ['src/**/*.ts', 'src/**/*.svelte'];
const TESTS = ['src/**/*.test.ts'];

/** `off` maps a scope ('root' or a kind's name) to { rule: note }. */
function buildConfig(off) {
  const offRules = (scope) =>
    Object.fromEntries(Object.keys(off[scope] ?? {}).map((r) => [r, 'off']));
  return {
    $schema: './node_modules/oxlint/configuration_schema.json',
    plugins: PLUGINS,
    jsPlugins: ['./oxlint/local.mjs'],
    categories: Object.fromEntries(CATEGORIES.map((category) => [category, 'error'])),
    env: { builtin: true, browser: true, node: true },
    ignorePatterns: ['build/', '.svelte-kit/', 'dist/', 'node_modules/'],
    options: { typeAware: true, reportUnusedDisableDirectives: 'error' },
    rules: {
      ...Object.fromEntries(
        Object.keys(OPTIONS)
          .filter((rule) => PLUGINS.includes(pluginOf(rule)))
          .map((rule) => [rule, on(rule)])
      ),
      'local/no-layout-read-before-resize-observer': 'error',
      ...offRules('root')
    },
    overrides: [
      ...KINDS.map((kind) => ({
        files: kind.files,
        ...(kind.excludeFiles && { excludeFiles: kind.excludeFiles }),
        ...(kind.plugins && { plugins: [...PLUGINS, ...kind.plugins] }),
        rules: {
          ...Object.fromEntries(
            RULES.filter((rule) => kind.plugins?.includes(rule.scope)).map((rule) => [
              rule.id,
              on(rule.id)
            ])
          ),
          ...offRules(kind.name)
        }
      })),
      { files: SOURCES, excludeFiles: TESTS, rules: { 'local/no-bare-poll': 'error' } },
      {
        files: SOURCES,
        excludeFiles: ['src/lib/stores/**', ...TESTS],
        rules: { 'local/no-song-scan': 'error', 'local/sideways-scroller': 'error' }
      }
    ]
  };
}

function oxlint(args) {
  try {
    return execFileSync(OXLINT, args, { encoding: 'utf8', maxBuffer: 1 << 30 });
  } catch (error) {
    if (typeof error.stdout !== 'string') throw error;
    return error.stdout;
  }
}

/** Findings per rule per file kind under `config`. */
function findings(config) {
  const probe = '.oxlintrc.probe.json';
  writeFileSync(probe, JSON.stringify(config));
  let diagnostics;
  try {
    ({ diagnostics } = JSON.parse(oxlint(['-c', probe, '-f', 'json', '.'])));
  } finally {
    rmSync(probe);
  }
  const counts = new Map();
  for (const { code, filename, message } of diagnostics) {
    const [, plugin, name] = /^([\w-]+)\((.+)\)$/.exec(code ?? '') ?? [];
    if (!plugin) throw new Error(`${filename}: ${message}`);
    const rule = `${plugin}/${name}`;
    const kind = KINDS.find((k) => k.match(filename)).name;
    const byKind = counts.get(rule) ?? {};
    byKind[kind] = (byKind[kind] ?? 0) + 1;
    counts.set(rule, byKind);
  }
  return counts;
}

/** JSON with a `// note` after each rule that `notes` (rules object → { rule: note }) names. */
function jsonc(value, notes, indent = '') {
  if (Array.isArray(value) && value.some((v) => typeof v === 'object')) {
    const inner = indent + '  ';
    return `[\n${value.map((v) => inner + jsonc(v, notes, inner)).join(',\n')}\n${indent}]`;
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) return JSON.stringify(value);
  const inner = indent + '  ';
  const noted = notes.get(value) ?? {};
  const entries = Object.entries(value).map(([key, v], i, all) => {
    const comma = i < all.length - 1 ? ',' : '';
    const note = noted[key] ? ` // ${noted[key]}` : '';
    return `${inner}${JSON.stringify(key)}: ${jsonc(v, notes, inner)}${comma}${note}`;
  });
  return entries.length === 0 ? '{}' : `{\n${entries.join('\n')}\n${indent}}`;
}

// Type-aware rules resolve imports through the tsconfig that `svelte-kit sync` writes.
execFileSync('node_modules/.bin/svelte-kit', ['sync']);
const counts = findings(buildConfig({ root: OFF }));
const local = [...counts.keys()].filter((rule) => rule.startsWith('local/'));
if (local.length > 0)
  throw new Error(`fix these first, the local rules stay on: ${local.join(', ')}`);

const off = { root: { ...OFF } };
for (const [rule, byKind] of [...counts].sort(([a], [b]) => a.localeCompare(b))) {
  if (off.root[rule]) continue;
  const kinds = Object.keys(byKind);
  const total = Object.values(byKind).reduce((a, b) => a + b, 0);
  const scope = kinds.length === 1 ? kinds[0] : 'root';
  (off[scope] ??= {})[rule] = `${total} finding${total === 1 ? '' : 's'}`;
}

const config = buildConfig(off);
const notes = new Map([[config.rules, off.root]]);
KINDS.forEach((kind, i) => notes.set(config.overrides[i].rules, off[kind.name] ?? {}));
const prettier = { ...(await resolveConfig(OUTPUT)), filepath: OUTPUT };
writeFileSync(OUTPUT, await format(jsonc(config, notes), prettier));

const remaining = oxlint(['--format', 'unix', '.']);
if (remaining.trim()) throw new Error(`${OUTPUT} still reports:\n${remaining}`);

const table = CATEGORIES.map((category) => {
  const inCategory = RULES.filter(
    (rule) => rule.category === category && [...PLUGINS, 'vitest'].includes(rule.scope)
  );
  const offIn = (rule) => Object.keys(off).filter((scope) => rule.id in off[scope]);
  const everywhere = inCategory.filter((rule) => offIn(rule).length === 0).length;
  const offRoot = inCategory.filter((rule) => offIn(rule).includes('root')).length;
  const offKind = inCategory.length - everywhere - offRoot;
  return `${category}: ${inCategory.length} rules, ${everywhere} on, ${offKind} off in one file kind, ${offRoot} off`;
});
console.log(`${OUTPUT} written\n${table.join('\n')}`);
