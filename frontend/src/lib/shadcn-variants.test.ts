import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { compile } from '@tailwindcss/node';
import { describe, expect, it } from 'vitest';

const here = dirname(fileURLToPath(import.meta.url));
const appCss = readFileSync(resolve(here, '../app.css'), 'utf8');
const variantLines = appCss
  .split('\n')
  .filter((line) => line.startsWith('@custom-variant data-'))
  .join('\n');

async function css(...candidates: string[]) {
  const compiler = await compile(`@tailwind utilities;\n${variantLines}`, {
    base: resolve(here, '..'),
    onDependency: () => {}
  });
  return compiler.build(candidates);
}

describe("shadcn's open and closed variants", () => {
  it('match the data-state bits-ui renders', async () => {
    const out = await css('data-open:opacity-50', 'data-closed:opacity-0');
    expect(out).toContain('.data-open\\:opacity-50[data-state="open"]');
    expect(out).toContain('.data-closed\\:opacity-0[data-state="closed"]');
  });
});
