import { getDocPage, hasMermaid, neighbors, renderDoc } from '#lib/docs/content.ts';
import type { PageLoad } from './$types';

// `/docs` renders the docs directory's README — the documentation index.
export const load: PageLoad = () => {
  const doc = getDocPage('');
  if (!doc) throw new Error('docs/README.md is missing from the documentation bundle');
  const { html, toc } = renderDoc(doc.source, doc.docPath);
  const { prev, next } = neighbors('');
  return {
    slug: '',
    docPath: doc.docPath,
    title: doc.title,
    description: doc.description,
    html,
    toc,
    mermaid: hasMermaid(doc.source),
    prev,
    next
  };
};
