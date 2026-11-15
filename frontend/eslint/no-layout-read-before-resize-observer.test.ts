import { RuleTester } from 'eslint';
import { afterAll, describe, it } from 'vitest';
import rule from './no-layout-read-before-resize-observer.mjs';

RuleTester.afterAll = afterAll;
RuleTester.describe = describe;
RuleTester.it = it;
RuleTester.itOnly = it.only;

const tester = new RuleTester({ languageOptions: { ecmaVersion: 2022, sourceType: 'module' } });

tester.run('no-layout-read-before-resize-observer', rule, {
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
});
