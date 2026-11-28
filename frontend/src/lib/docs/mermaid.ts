/**
 * Client-side hydration for mermaid fences in documentation Markdown.
 *
 * The prerendered HTML ships the diagrams as plain `<pre><code class="language-mermaid">` fences,
 * so the pages work with JavaScript disabled and the mermaid bundle (large) is only downloaded by
 * readers who actually opened a page that has a diagram. Each rendered diagram keeps its source
 * in `data-code`, so a light/dark switch re-renders in the new theme without the Markdown again.
 */

type MermaidApi = (typeof import('mermaid'))['default'];

let mermaidPromise: Promise<MermaidApi> | null = null;
let diagramCounter = 0;

function loadMermaid(): Promise<MermaidApi> {
  mermaidPromise ??= import('mermaid').then((module) => module.default);
  return mermaidPromise;
}

export async function renderMermaidDiagrams(root: HTMLElement, dark: boolean): Promise<void> {
  const theme = dark ? 'dark' : 'neutral';
  // Fences a failed render already wrapped in a .docs-mermaid fallback holder are skipped here;
  // the stale-holders loop below re-renders them from data-code.
  const fences = [...root.querySelectorAll<HTMLElement>('pre > code.language-mermaid')].filter(
    (code) => !code.closest('.docs-mermaid')
  );
  const stale = [...root.querySelectorAll<HTMLElement>(`div.docs-mermaid[data-theme]`)].filter(
    (holder) => holder.dataset.theme !== theme
  );
  if (fences.length === 0 && stale.length === 0) return;

  const mermaid = await loadMermaid();
  mermaid.initialize({ startOnLoad: false, theme, securityLevel: 'strict' });

  for (const code of fences) {
    const pre = code.parentElement;
    if (!pre) continue;
    const source = code.textContent ?? '';
    const holder = document.createElement('div');
    holder.className = 'docs-mermaid';
    holder.dataset.code = source;
    holder.dataset.theme = theme;
    pre.replaceWith(holder);
    await stamp(mermaid, holder, source, theme);
  }

  for (const holder of stale) {
    const source = holder.dataset.code ?? '';
    holder.innerHTML = '';
    await stamp(mermaid, holder, source, theme);
  }
}

async function stamp(
  mermaid: MermaidApi,
  holder: HTMLElement,
  source: string,
  theme: string
): Promise<void> {
  try {
    const { svg } = await mermaid.render(`mh-docs-diagram-${++diagramCounter}`, source);
    holder.innerHTML = svg;
    holder.dataset.theme = theme;
  } catch {
    // A diagram mermaid can't parse falls back to its readable source: put the original fence
    // back inside the holder (keeping data-code for a later theme re-render attempt).
    const pre = document.createElement('pre');
    const code = document.createElement('code');
    code.className = 'language-mermaid';
    code.textContent = source;
    pre.appendChild(code);
    holder.replaceChildren(pre);
    holder.dataset.theme = theme;
  }
}
