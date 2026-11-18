import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import ts from 'typescript-eslint';
import { loadConfig } from '@sveltejs/load-config';
import noLayoutReadBeforeResizeObserver from './eslint/no-layout-read-before-resize-observer.mjs';

const svelteConfig = (await loadConfig('./', { traverse: false }))?.config;

/** Whole-library scans of `songsStore.songs` outside the store (see the block that uses it). */
const songScans = [
  {
    selector:
      "CallExpression[callee.type='MemberExpression'][callee.object.type='MemberExpression'][callee.object.object.name='songsStore'][callee.object.property.name='songs'][callee.property.name=/^(filter|find|findIndex|findLast|findLastIndex|reduce|reduceRight|some|every|forEach|map|flatMap)$/]",
    message:
      'Do not scan songsStore.songs in a component. Read a shared cut (songsStore.builtSongs, .trackListSongs, .leadArtistGroups, .albums, .libraryCounts, .enrichmentStatusCounts), look a row up with songsStore.songsById, or add a cut to lib/stores/songs.svelte.ts.'
  },
  {
    selector:
      "CallExpression > MemberExpression.arguments[object.name='songsStore'][property.name='songs'], VariableDeclarator > MemberExpression.init[object.name='songsStore'][property.name='songs'], ForOfStatement > MemberExpression.right[object.name='songsStore'][property.name='songs'], SpreadElement > MemberExpression.argument[object.name='songsStore'][property.name='songs']",
    message:
      'Do not pass or alias songsStore.songs outside the store: that hides a whole-library scan. Read a shared cut (songsStore.builtSongs, .trackListSongs, .leadArtistGroups, .albums, .libraryCounts, .enrichmentStatusCounts) or songsStore.songsById, or add a cut to lib/stores/songs.svelte.ts. Reading .length is fine.'
  }
];

/**
 * A bare setInterval keeps polling while the tab is hidden or the installed app is in the
 * background. Polls go through #lib/poll; a timer that is not a poll says why on its disable line
 * (`// eslint-disable-next-line no-restricted-syntax -- <reason>`).
 */
const pollMessage =
  'Poll with `poll()` from #lib/poll (it pauses while the page is hidden). A timer that is not a poll: disable this line with the reason.';
const barePolls = [
  { selector: "CallExpression[callee.name='setInterval']", message: pollMessage },
  { selector: "CallExpression[callee.property.name='setInterval']", message: pollMessage }
];

/**
 * `overflow-x: auto` on its own turns `overflow-y` into `auto` as well (CSS has no scroller that
 * scrolls one axis and lets the other show), so a sideways row also scrolls up and down by
 * whatever pokes out below it: a chip's 44pt hit area, a pressed hardware key's 2px. On a phone
 * that is enough for a finger to drag the whole row off its line and let it bounce back. Every
 * sideways scroller says `overflow-y-hidden` too; it clips nothing the row did not clip already.
 */
const sidewaysMessage =
  'A sideways scroller also needs `overflow-y-hidden`: on its own, overflow-x-auto makes the row scroll vertically too, and a finger can drag it up and down.';
const sidewaysScrollers = [
  ['Literal', 'value'],
  ['SvelteLiteral', 'value'],
  ['TemplateElement', 'value.raw']
].map(([node, text]) => ({
  selector: `${node}[${text}=/overflow-x-(auto|scroll)/]:not([${text}=/overflow-y-/])`,
  message: sidewaysMessage
}));

export default ts.config(
  js.configs.recommended,
  ...ts.configs.recommended,
  ...svelte.configs.recommended,
  prettier,
  ...svelte.configs.prettier,
  {
    languageOptions: {
      globals: { ...globals.browser, ...globals.node }
    },
    plugins: {
      local: {
        rules: { 'no-layout-read-before-resize-observer': noLayoutReadBeforeResizeObserver }
      }
    },
    rules: {
      // A ResizeObserver's first callback already reports the size, after layout and before
      // paint; measuring beside it forces an extra layout of the page being mounted.
      'local/no-layout-read-before-resize-observer': 'error',
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }
      ],
      // We use {@html} in JsonLd.svelte to inject a controlled JSON-LD script tag.
      // The content is JSON.stringify of static data, not user input.
      'svelte/no-at-html-tags': 'off',
      // Plain href links to known routes are fine — resolve() is for dynamic params.
      'svelte/no-navigation-without-resolve': 'off',
      // We use plain Set/Map with the immutable-copy reassignment pattern, which
      // triggers $state reactivity correctly. SvelteSet/SvelteMap would only
      // matter if we mutated the collection in place.
      'svelte/prefer-svelte-reactivity': 'off',
      // The $state + $effect-resync pattern is intentional in a few places where
      // the local copy needs to drift before propagating (e.g. debounced
      // searches, expanded-by-default lists).
      'svelte/prefer-writable-derived': 'off'
    }
  },
  {
    files: ['**/*.svelte', '**/*.svelte.ts', '**/*.svelte.js'],
    languageOptions: {
      parserOptions: {
        projectService: true,
        extraFileExtensions: ['.svelte'],
        parser: ts.parser,
        svelteConfig
      }
    }
  },
  // Both guards below use no-restricted-syntax, and a later block's setting of a rule replaces an
  // earlier one for the files both match, so each block lists every selector that applies to it.
  {
    // The library is thousands of rows. A component that filters, searches or folds the whole of
    // `songsStore.songs` repeats that walk on every refresh, like and patchSong, once per mounted
    // copy. The store computes the shared cuts once; read those. Lookups by id go through
    // `songsById`. Handing the array to a helper or keeping an alias of it is the same walk with
    // a step in between, so both are flagged too; `.length` is fine.
    files: ['src/**/*.ts', 'src/**/*.svelte'],
    ignores: ['src/lib/stores/**', 'src/**/*.test.ts'],
    rules: {
      'no-restricted-syntax': ['error', ...songScans, ...barePolls, ...sidewaysScrollers]
    }
  },
  {
    files: ['src/lib/stores/**/*.ts'],
    ignores: ['src/**/*.test.ts'],
    rules: {
      'no-restricted-syntax': ['error', ...barePolls]
    }
  },
  {
    // A skin changes how the app looks, never what it offers. A component that could read the
    // flag could render something else under the skin, which is how Now Playing once lost its
    // Shuffle and its links. So the flag stays inside the skin and the three places that switch
    // it; everything else gets look values (skinLook, mediaAppearance) and housings (skinHousing).
    files: ['src/**/*.ts', 'src/**/*.svelte'],
    ignores: [
      'src/lib/hardware/**',
      'src/routes/+layout.svelte',
      'src/lib/components/v2/AccountPanel.svelte',
      'src/lib/components/CommandPalette.svelte'
    ],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['**/hardware-mode.svelte.ts', '**/hardware-mode.svelte'],
              importNames: ['hardwareMode'],
              message:
                'A skin changes look, never features. Use skinLook() for a look value, or style it in app-skin.css; see lib/hardware/README.md.'
            }
          ]
        }
      ]
    }
  },
  {
    ignores: ['build/', '.svelte-kit/', 'dist/', 'node_modules/']
  }
);
