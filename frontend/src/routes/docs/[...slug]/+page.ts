import { error } from '@sveltejs/kit';
import { docPaths, getDocPage, hasMermaid, neighbors, renderDoc } from '#lib/docs/content.ts';
import { slugForDocPath } from '#lib/docs/slugs.ts';
import type { EntryGenerator, PageLoad } from './$types';

// Every doc becomes a prerendered page; the README index is the `/docs` route itself.
export const entries: EntryGenerator = () =>
  docPaths
    .map((docPath) => slugForDocPath(docPath))
    .filter((slug) => slug !== '')
    .map((slug) => ({ slug }));

export const load: PageLoad = ({ params }) => {
  const doc = getDocPage(params.slug);
  if (!doc) error(404, `No documentation page at /docs/${params.slug}`);
  const { html, toc } = renderDoc(doc.source, doc.docPath);
  const { prev, next } = neighbors(params.slug);
  return {
    slug: params.slug,
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
