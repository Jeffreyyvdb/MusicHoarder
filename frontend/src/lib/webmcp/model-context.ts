/**
 * The WebMCP browser API (https://webmachinelearning.github.io/webmcp/), typed by hand from the
 * Community Group draft rather than through a package: the surface is small, and the draft still
 * moves, so the copy we depend on is the one written down here.
 *
 * A page registers "tools" — a name, a description, a JSON Schema for the input and an `execute`
 * callback — and an agent in the browser (built in, or an extension) calls them in the open tab.
 * The tools run the page's own code, so the UI follows whatever the agent does.
 *
 * Chrome ships it behind `chrome://flags/#enable-webmcp-testing` and an origin trial (see
 * `WEBMCP_ORIGIN_TRIAL_TOKEN` in `hooks.server.ts`). The API moved from `navigator.modelContext`
 * to `document.modelContext`; both are looked for, newest first. Everywhere else this module does
 * nothing.
 */

/** Hints an agent may use to decide when a call is safe or needs the user's confirmation. */
export interface ToolAnnotations {
  /** The tool only reads. */
  readOnlyHint?: boolean;
  /** The output holds text the site does not vouch for (here: tags from downloaded files). */
  untrustedContentHint?: boolean;
  /** The tool does something significant or hard to undo. */
  consequentialHint?: boolean;
}

export interface ToolExecuteOptions {
  /** Aborted when the agent cancels the call or the page goes away. */
  signal: AbortSignal;
}

/** A JSON Schema object, as far as the tools here use one. */
export type JsonSchema = Record<string, unknown>;

export interface ModelContextTool {
  /** 1–128 characters of `[A-Za-z0-9_.-]`. */
  name: string;
  title?: string;
  description: string;
  inputSchema?: JsonSchema;
  annotations?: ToolAnnotations;
  /** Resolves to something JSON can serialize; the agent receives that JSON. */
  execute: (input: Record<string, unknown>, options: ToolExecuteOptions) => Promise<unknown>;
}

export interface ModelContextRegisterToolOptions {
  /** Aborting it unregisters the tool. */
  signal?: AbortSignal;
}

export interface ModelContext {
  registerTool(tool: ModelContextTool, options?: ModelContextRegisterToolOptions): Promise<void>;
}

/** The page's model context, or null where the browser has no WebMCP. */
export function findModelContext(
  doc: object | undefined = typeof document === 'undefined' ? undefined : document,
  nav: object | undefined = typeof navigator === 'undefined' ? undefined : navigator
): ModelContext | null {
  for (const host of [doc, nav]) {
    const candidate = (host as { modelContext?: unknown } | undefined)?.modelContext;
    if (candidate && typeof (candidate as Partial<ModelContext>).registerTool === 'function') {
      return candidate as ModelContext;
    }
  }
  return null;
}

const TOOL_NAME = /^[A-Za-z0-9_.-]{1,128}$/;

/**
 * Register `tools` until `signal` aborts. A tool the browser refuses (a bad schema, a name taken)
 * is skipped rather than taking the others down with it; the names that did register come back.
 */
export async function registerTools(
  context: ModelContext,
  tools: readonly ModelContextTool[],
  signal: AbortSignal
): Promise<string[]> {
  const registered: string[] = [];
  for (const tool of tools) {
    if (signal.aborted) break;
    if (!TOOL_NAME.test(tool.name)) {
      console.warn(`[webmcp] skipped a tool with an invalid name: ${tool.name}`);
      continue;
    }
    try {
      await context.registerTool(tool, { signal });
      registered.push(tool.name);
    } catch (err) {
      console.warn(`[webmcp] could not register ${tool.name}`, err);
    }
  }
  return registered;
}
