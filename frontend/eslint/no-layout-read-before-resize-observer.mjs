// A ResizeObserver delivers the element's size in its first callback: after the browser has laid
// the page out, before it paints. Measuring the element yourself in the same setup, synchronously
// (`measure(); new ResizeObserver(measure)`), asks for that layout early, while the page that just
// mounted is still being built, so the browser lays it out once for the question and again for
// the rest of the mount. On a phone that was most of a page switch (PageToolbarV2 renders on every
// page). This rule flags a layout read made synchronously in the function that creates a
// ResizeObserver: a read written there directly, or one reached through a call to a function
// defined in the file.
//
// When the first paint genuinely cannot wait for the observer's first callback (a value the
// callback cannot set without resizing what it observes, say), keep the read and disable the rule
// on that line with a reason:
//   // eslint-disable-next-line local/no-layout-read-before-resize-observer -- <why>

const LAYOUT_READS = new Set([
  'getBoundingClientRect',
  'getClientRects',
  'offsetWidth',
  'offsetHeight',
  'offsetTop',
  'offsetLeft',
  'clientWidth',
  'clientHeight',
  'scrollWidth',
  'scrollHeight'
]);

const FUNCTION_TYPES = new Set([
  'FunctionDeclaration',
  'FunctionExpression',
  'ArrowFunctionExpression'
]);

const MAX_DEPTH = 3;

function isResizeObserverNew(node) {
  return (
    node.type === 'NewExpression' &&
    node.callee.type === 'Identifier' &&
    node.callee.name === 'ResizeObserver'
  );
}

/** The member names `node` reads (not writes), e.g. `el.clientWidth` but not `el.scrollTop = 0`. */
function isLayoutRead(node) {
  if (node.type !== 'MemberExpression' || node.computed) return false;
  if (node.property.type !== 'Identifier' || !LAYOUT_READS.has(node.property.name)) return false;
  const parent = node.parent;
  return !(parent && parent.type === 'AssignmentExpression' && parent.left === node);
}

/** Walks `node`'s own code (not the bodies of functions it defines), calling `visit` on each node. */
function walkSync(node, visit, keys) {
  if (!node || typeof node.type !== 'string' || FUNCTION_TYPES.has(node.type)) return;
  if (visit(node) === false) return;
  for (const key of keys[node.type] ?? Object.keys(node)) {
    if (key === 'parent') continue;
    const child = node[key];
    if (Array.isArray(child)) {
      for (const c of child) {
        if (c && typeof c.type === 'string' && !FUNCTION_TYPES.has(c.type))
          walkSync(c, visit, keys);
      }
    } else if (child && typeof child.type === 'string' && !FUNCTION_TYPES.has(child.type)) {
      walkSync(child, visit, keys);
    }
  }
}

/** The function an identifier names, when it is defined in this file. */
function resolveFunction(sourceCode, identifier) {
  let scope = sourceCode.getScope(identifier);
  while (scope) {
    const variable = scope.set.get(identifier.name);
    if (variable) {
      for (const def of variable.defs) {
        if (def.node.type === 'FunctionDeclaration') return def.node;
        if (
          def.node.type === 'VariableDeclarator' &&
          def.node.init &&
          FUNCTION_TYPES.has(def.node.init.type)
        )
          return def.node.init;
      }
      return null;
    }
    scope = scope.upper;
  }
  return null;
}

/** Whether running `fn` reads layout, directly or through functions defined in this file. */
function functionReadsLayout(sourceCode, fn, keys, depth, seen) {
  if (depth > MAX_DEPTH || seen.has(fn)) return false;
  seen.add(fn);
  let reads = false;
  walkSync(
    fn.body,
    (node) => {
      if (reads) return false;
      if (isLayoutRead(node)) reads = true;
      else if (node.type === 'CallExpression' && node.callee.type === 'Identifier') {
        const callee = resolveFunction(sourceCode, node.callee);
        if (callee && functionReadsLayout(sourceCode, callee, keys, depth + 1, seen)) reads = true;
      }
    },
    keys
  );
  return reads;
}

/** @type {import('eslint').Rule.RuleModule} */
export default {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Disallow measuring layout synchronously where a ResizeObserver is set up; its first callback already reports the size.'
    },
    schema: [],
    messages: {
      read: "Layout is read synchronously while a ResizeObserver is set up here; the observer's first callback reports the size after layout and before paint. Measure there (entry.contentRect / borderBoxSize), or disable this rule on the line with the reason the first paint cannot wait.",
      call: "'{{name}}' reads layout and runs synchronously while a ResizeObserver is set up here; the observer's first callback reports the size after layout and before paint. Measure there (entry.contentRect / borderBoxSize), or disable this rule on the line with the reason the first paint cannot wait."
    }
  },
  create(context) {
    const sourceCode = context.sourceCode;
    const keys = sourceCode.visitorKeys;
    return {
      NewExpression(node) {
        if (!isResizeObserverNew(node)) return;
        let fn = node.parent;
        while (fn && !FUNCTION_TYPES.has(fn.type)) fn = fn.parent;
        const body = fn ? fn.body : sourceCode.ast;
        if (!body || (body.type !== 'BlockStatement' && body.type !== 'Program')) return;
        for (const statement of body.body) {
          walkSync(
            statement,
            (n) => {
              if (n === node) return false;
              if (isLayoutRead(n)) {
                context.report({ node: n, messageId: 'read' });
                return false;
              }
              if (n.type === 'CallExpression' && n.callee.type === 'Identifier') {
                const callee = resolveFunction(sourceCode, n.callee);
                if (callee && functionReadsLayout(sourceCode, callee, keys, 1, new Set())) {
                  context.report({ node: n, messageId: 'call', data: { name: n.callee.name } });
                  return false;
                }
              }
            },
            keys
          );
        }
      }
    };
  }
};
