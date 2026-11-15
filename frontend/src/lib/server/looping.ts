/**
 * @looping/server — tag every request with an id so a Looping report can be matched to backend logs.
 *
 * - Every response gets `x-request-id` (and exposes it to cross-origin frontends).
 * - Every log line written during the request carries the id: console.* output is prefixed
 *   with `[req:<id>]`, and `log.*` writes JSON lines with a `requestId` field.
 *
 * Self-contained on purpose (Node built-ins only): copying this one file into an app works too.
 * Vendored from github.com/Jeffreyyvdb/looping (skills/looping-install/assets/looping-server.ts);
 * wired in hooks.server.ts, and the API proxy forwards the id (`RequestIdMiddleware` on the API).
 */
import { AsyncLocalStorage } from 'node:async_hooks';
import { randomBytes } from 'node:crypto';

export const REQUEST_ID_HEADER = 'x-request-id';
const VALID_INCOMING = /^[\w.:-]{6,128}$/;

const store = new AsyncLocalStorage<{ requestId: string }>();

export function newRequestId(): string {
  return randomBytes(9).toString('base64url');
}

/** Reuses an upstream id (proxy, other service) when present and sane, else makes a new one. */
export function requestIdFrom(headers: Headers): string {
  const incoming = headers.get(REQUEST_ID_HEADER);
  return incoming && VALID_INCOMING.test(incoming) ? incoming : newRequestId();
}

export function currentRequestId(): string | undefined {
  return store.getStore()?.requestId;
}

export function runWithRequestId<T>(requestId: string, fn: () => T): T {
  return store.run({ requestId }, fn);
}

// ---------------------------------------------------------------------------
// Logging

type Level = 'debug' | 'info' | 'warn' | 'error';

function serialize(value: unknown): unknown {
  if (value instanceof Error) return { name: value.name, message: value.message, stack: value.stack, cause: value.cause ? serialize(value.cause) : undefined };
  return value;
}

function write(level: Level, msg: string, data?: Record<string, unknown>) {
  const line: Record<string, unknown> = { time: new Date().toISOString(), level, msg, requestId: currentRequestId() };
  if (data) for (const [k, v] of Object.entries(data)) line[k] = serialize(v);
  const text = `${JSON.stringify(line)}\n`;
  (level === 'error' || level === 'warn' ? process.stderr : process.stdout).write(text);
}

/** Structured JSON logger; `requestId` is filled in automatically inside a request. */
export const log = {
  debug: (msg: string, data?: Record<string, unknown>) => write('debug', msg, data),
  info: (msg: string, data?: Record<string, unknown>) => write('info', msg, data),
  warn: (msg: string, data?: Record<string, unknown>) => write('warn', msg, data),
  error: (msg: string, data?: Record<string, unknown>) => write('error', msg, data),
};

let consolePatched = false;
/** Prefixes console.* output with `[req:<id>]` while inside a request, so existing logs become searchable. */
export function patchConsole() {
  if (consolePatched) return;
  consolePatched = true;
  for (const method of ['log', 'info', 'warn', 'error', 'debug'] as const) {
    const original = console[method].bind(console);
    console[method] = (...args: unknown[]) => {
      const id = currentRequestId();
      if (id) original(`[req:${id}]`, ...args);
      else original(...args);
    };
  }
}

// ---------------------------------------------------------------------------
// Request wrapping

export interface LoopingServerOptions {
  /** Prefix console.* output with the request id (default true). */
  patchConsole?: boolean;
  /** Write one JSON line per request with status and duration (default true). */
  logRequests?: boolean;
  /** Skip request logging for these paths, e.g. health checks (prefix match). */
  ignorePaths?: string[];
}

function tagResponse(res: Response, requestId: string): Response {
  const apply = (headers: Headers) => {
    headers.set(REQUEST_ID_HEADER, requestId);
    const exposed = headers.get('access-control-expose-headers');
    if (!exposed) headers.set('access-control-expose-headers', REQUEST_ID_HEADER);
    else if (exposed !== '*' && !exposed.toLowerCase().includes(REQUEST_ID_HEADER)) headers.set('access-control-expose-headers', `${exposed}, ${REQUEST_ID_HEADER}`);
  };
  try {
    apply(res.headers);
    return res;
  } catch {
    // Responses from fetch() have immutable headers; copy them.
    const copy = new Response(res.body, res);
    apply(copy.headers);
    return copy;
  }
}

async function handleTimed(
  requestId: string,
  method: string,
  path: string,
  run: () => Response | Promise<Response>,
  opts: LoopingServerOptions,
): Promise<Response> {
  const start = performance.now();
  const shouldLog = opts.logRequests !== false && !opts.ignorePaths?.some((p) => path.startsWith(p));
  try {
    const res = tagResponse(await run(), requestId);
    if (shouldLog) write(res.status >= 500 ? 'error' : 'info', 'request', { method, path, status: res.status, ms: Math.round(performance.now() - start) });
    return res;
  } catch (error) {
    write('error', 'request failed', { method, path, ms: Math.round(performance.now() - start), error });
    throw error;
  }
}

/** Wraps any fetch-style handler (Hono `app.fetch`, Bun.serve, Deno.serve, …). */
export function withRequestId<A extends unknown[]>(
  handler: (request: Request, ...rest: A) => Response | Promise<Response>,
  opts: LoopingServerOptions = {},
): (request: Request, ...rest: A) => Promise<Response> {
  if (opts.patchConsole !== false) patchConsole();
  return (request, ...rest) => {
    const id = requestIdFrom(request.headers);
    return runWithRequestId(id, () => handleTimed(id, request.method, new URL(request.url).pathname, () => handler(request, ...rest), opts));
  };
}

// ---------------------------------------------------------------------------
// SvelteKit (structural types so this file doesn't depend on @sveltejs/kit)

interface KitEvent {
  request: Request;
  url: URL;
  locals: object;
}
// Generic over the event, so the handle is assignable to the app's own `Handle` (whose `resolve`
// takes the full RequestEvent, which a structural KitEvent alone is not).
type KitHandle<E extends KitEvent = KitEvent> = (input: { event: E; resolve: (event: E) => Response | Promise<Response> }) => Promise<Response>;

/**
 * SvelteKit `handle` hook. Use in src/hooks.server.ts:
 *   export const handle = sequence(loopingHandle(), yourHandle);
 * Sets `event.locals.requestId` (add `requestId: string` to App.Locals).
 */
export function loopingHandle<E extends KitEvent = KitEvent>(opts: LoopingServerOptions = {}): KitHandle<E> {
  if (opts.patchConsole !== false) patchConsole();
  return ({ event, resolve }) => {
    const id = requestIdFrom(event.request.headers);
    (event.locals as { requestId?: string }).requestId = id;
    return runWithRequestId(id, () => handleTimed(id, event.request.method, event.url.pathname, () => resolve(event), opts));
  };
}

/**
 * SvelteKit `handleError` hook that logs the full error with its request id:
 *   export const handleError = loopingHandleError;
 */
export function loopingHandleError(input: { error: unknown; event: KitEvent; status?: number; message?: string }): void {
  const requestId = (input.event.locals as { requestId?: string }).requestId;
  write('error', 'unhandled error', { requestId, status: input.status, path: input.event.url.pathname, error: input.error });
}
