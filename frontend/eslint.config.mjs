import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import ts from 'typescript-eslint';
import { loadConfig } from '@sveltejs/load-config';

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
    rules: {
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
      'no-restricted-syntax': ['error', ...songScans, ...barePolls]
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
    ignores: ['build/', '.svelte-kit/', 'dist/', 'node_modules/']
  }
);
