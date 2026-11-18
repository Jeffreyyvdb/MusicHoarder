// This repo's own lint rules, loaded by .oxlintrc.json as `jsPlugins`. .oxlintrc.json's overrides
// decide which files each rule applies to. oxlint runs JS plugins on the <script> blocks of a
// .svelte file only; the markup half of `sideways-scroller` is checked by svelte-markup.mjs.
import { noLayoutReadBeforeResizeObserver } from './no-layout-read-before-resize-observer.mjs';

/**
 * Builds a rule that reports every node matching one of `selectors` (esquery, as ESLint's
 * `no-restricted-syntax` takes them) with that selector's message.
 */
function restrictedSyntax(description, selectors) {
  return {
    meta: { type: 'problem', docs: { description }, schema: [] },
    create(context) {
      return Object.fromEntries(
        selectors.map(({ selector, message }) => [
          selector,
          (node) => context.report({ node, message })
        ])
      );
    }
  };
}

// The library is thousands of rows. A component that filters, searches or folds the whole of
// `songsStore.songs` repeats that walk on every refresh, like and patchSong, once per mounted
// copy. The store computes the shared cuts once; read those. Lookups by id go through
// `songsById`. Handing the array to a helper or keeping an alias of it is the same walk with a
// step in between, so both are flagged too; `.length` is fine.
const noSongScan = restrictedSyntax(
  'Disallow whole-library scans of songsStore.songs outside the store.',
  [
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
);

// A bare setInterval keeps polling while the tab is hidden or the installed app is in the
// background. Polls go through #lib/poll; a timer that is not a poll says why on its disable line
// (`// eslint-disable-next-line local/no-bare-poll -- <reason>`).
const pollMessage =
  'Poll with `poll()` from #lib/poll (it pauses while the page is hidden). A timer that is not a poll: disable this line with the reason.';
const noBarePoll = restrictedSyntax('Disallow setInterval outside #lib/poll.', [
  { selector: "CallExpression[callee.name='setInterval']", message: pollMessage },
  { selector: "CallExpression[callee.property.name='setInterval']", message: pollMessage }
]);

// `overflow-x: auto` on its own turns `overflow-y` into `auto` as well (CSS has no scroller that
// scrolls one axis and lets the other show), so a sideways row also scrolls up and down by
// whatever pokes out below it: a chip's 44pt hit area, a pressed hardware key's 2px. On a phone
// that is enough for a finger to drag the whole row off its line and let it bounce back. Every
// sideways scroller says `overflow-y-hidden` too; it clips nothing the row did not clip already.
export const sidewaysMessage =
  'A sideways scroller also needs `overflow-y-hidden`: on its own, overflow-x-auto makes the row scroll vertically too, and a finger can drag it up and down.';

/** Whether a class string makes a sideways scroller without saying what happens vertically. */
export function isSidewaysWithoutY(text) {
  return /overflow-x-(auto|scroll)/.test(text) && !/overflow-y-/.test(text);
}

const sidewaysScroller = {
  meta: {
    type: 'problem',
    docs: { description: 'Require overflow-y-* beside overflow-x-auto / overflow-x-scroll.' },
    schema: []
  },
  create(context) {
    const check = (node, text) => {
      if (typeof text === 'string' && isSidewaysWithoutY(text))
        context.report({ node, message: sidewaysMessage });
    };
    return {
      Literal: (node) => check(node, node.value),
      TemplateElement: (node) => check(node, node.value.raw)
    };
  }
};

const local = {
  meta: { name: 'local' },
  rules: {
    'no-layout-read-before-resize-observer': noLayoutReadBeforeResizeObserver,
    'no-song-scan': noSongScan,
    'no-bare-poll': noBarePoll,
    'sideways-scroller': sidewaysScroller
  }
};

export default local;
