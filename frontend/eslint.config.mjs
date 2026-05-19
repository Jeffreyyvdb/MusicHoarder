import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import ts from 'typescript-eslint';
import svelteConfig from './svelte.config.js';

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
  {
    ignores: ['build/', '.svelte-kit/', 'dist/', 'node_modules/']
  }
);
