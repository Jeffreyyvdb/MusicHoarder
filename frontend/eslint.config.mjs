import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import ts from 'typescript-eslint';
import { loadConfig } from '@sveltejs/load-config';
import noLayoutReadBeforeResizeObserver from './eslint/no-layout-read-before-resize-observer.mjs';

const svelteConfig = (await loadConfig('./', { traverse: false }))?.config;

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
  {
    // The library is thousands of rows. A component that filters, searches or folds the whole of
    // `songsStore.songs` repeats that walk on every refresh, like and patchSong, once per mounted
    // copy. The store computes the shared cuts once; read those. Lookups by id go through
    // `songsById`. Handing the array to a helper or keeping an alias of it is the same walk with
    // a step in between, so both are flagged too; `.length` is fine.
    files: ['src/**/*.ts', 'src/**/*.svelte'],
    ignores: ['src/lib/stores/**', 'src/**/*.test.ts'],
    rules: {
      'no-restricted-syntax': [
        'error',
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
      ]
    }
  },
  {
    ignores: ['build/', '.svelte-kit/', 'dist/', 'node_modules/']
  }
);
