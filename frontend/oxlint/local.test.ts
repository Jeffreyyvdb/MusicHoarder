import { RuleTester } from 'oxlint/plugins-dev';
import { describe, expect, it } from 'vitest';
import local from './local.mjs';
import { checkSvelteMarkup } from './svelte-markup.mjs';

RuleTester.describe = describe;
RuleTester.it = it;
RuleTester.itOnly = it.only;

const tester = new RuleTester({ languageOptions: { sourceType: 'module' } });
const { rules } = local;

tester.run(
  'no-layout-read-before-resize-observer',
  rules['no-layout-read-before-resize-observer'],
  {
    valid: [
      // Measured by the observer alone.
      `function setup(el) {
      const ro = new ResizeObserver(([entry]) => { el.dataset.w = entry.contentRect.width; });
      ro.observe(el);
    }`,
      // A measuring function handed to the observer (and an event) but not called here.
      `function setup(el) {
      const measure = () => el.getBoundingClientRect().width;
      const ro = new ResizeObserver(measure);
      el.addEventListener('scroll', measure);
      ro.observe(el);
    }`,
      // Deferred to a frame: not synchronous.
      `function setup(el) {
      const measure = () => el.offsetHeight;
      requestAnimationFrame(() => measure());
      new ResizeObserver(measure).observe(el);
    }`,
      // A write, not a read.
      `function setup(el) {
      el.scrollTop = 0;
      new ResizeObserver(() => {}).observe(el);
    }`,
      // A function declared beside the observer is defined there, not run.
      `function setup(el) {
      function apply() { return el.clientWidth; }
      const ro = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(apply);
      return { update() { apply(); } };
    }`,
      // No observer here: the read is someone else's business.
      `function setup(el) { return el.clientWidth; }`
    ],
    invalid: [
      {
        code: `function setup(el) {
        const measure = () => el.getBoundingClientRect().width;
        measure();
        new ResizeObserver(measure).observe(el);
      }`,
        errors: [{ messageId: 'call', data: { name: 'measure' } }]
      },
      {
        code: `function setup(el) {
        let last = el.clientWidth;
        new ResizeObserver(() => { last = 0; }).observe(el);
      }`,
        errors: [{ messageId: 'read' }]
      },
      {
        // At the top level of a module.
        code: `const el = document.body;
      const measure = () => el.offsetWidth;
      measure();
      new ResizeObserver(measure).observe(el);`,
        errors: [{ messageId: 'call', data: { name: 'measure' } }]
      },
      {
        // Through a helper defined elsewhere in the file, and after the observer is made.
        code: `function edges(el) { return el.scrollWidth; }
      function setup(el) {
        const centre = () => edges(el);
        const ro = new ResizeObserver(centre);
        centre();
      }`,
        errors: [{ messageId: 'call', data: { name: 'centre' } }]
      }
    ]
  }
);

tester.run('no-song-scan', rules['no-song-scan'], {
  valid: [
    'const n = songsStore.songs.length;',
    'const hits = songsStore.builtSongs.filter((s) => s.liked);',
    'const row = songsStore.songsById.get(id);'
  ],
  invalid: [
    { code: 'const liked = songsStore.songs.filter((s) => s.liked);', errors: 1 },
    { code: 'const all = songsStore.songs;', errors: 1 },
    { code: 'count(songsStore.songs);', errors: 1 },
    { code: 'for (const s of songsStore.songs) use(s);', errors: 1 },
    { code: 'const copy = [...songsStore.songs];', errors: 1 }
  ]
});

tester.run('no-bare-poll', rules['no-bare-poll'], {
  valid: ['poll(refresh, 5000);', 'setTimeout(refresh, 5000);'],
  invalid: [
    { code: 'setInterval(refresh, 5000);', errors: 1 },
    { code: 'window.setInterval(refresh, 5000);', errors: 1 }
  ]
});

tester.run('sideways-scroller', rules['sideways-scroller'], {
  valid: [
    "const row = 'flex overflow-x-auto overflow-y-hidden';",
    'const row = `overflow-x-scroll overflow-y-hidden`;',
    "const box = 'overflow-auto';"
  ],
  invalid: [
    { code: "const row = 'flex overflow-x-auto';", errors: 1 },
    { code: 'const row = `flex overflow-x-scroll`;', errors: 1 }
  ]
});

describe('sideways-scroller in .svelte markup', () => {
  const source = [
    '<script>',
    "  const ok = 'overflow-x-auto overflow-y-hidden';",
    '</script>',
    '',
    '<div class="flex overflow-x-auto {ok}">overflow-x-auto as text is fine</div>',
    '<div class="overflow-x-auto overflow-y-hidden"></div>',
    "<div class={cn('gap-2', 'overflow-x-scroll')}></div>",
    '<div class={`overflow-x-auto ${ok}`}></div>'
  ].join('\n');

  it('reports each sideways scroller in the markup at its own line and column', () => {
    expect(checkSvelteMarkup('Row.svelte', source).map((p) => p.split(': ')[0])).toEqual([
      'Row.svelte:5:13',
      'Row.svelte:7:25',
      'Row.svelte:8:14'
    ]);
  });

  it('leaves the script blocks to oxlint', () => {
    const script = "<script>const row = 'overflow-x-auto';</script>";
    expect(checkSvelteMarkup('Row.svelte', script)).toEqual([]);
  });
});
