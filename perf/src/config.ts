import { createHash } from 'node:crypto'
import { homedir } from 'node:os'
import { join, resolve } from 'node:path'

export const PERF_ROOT = resolve(import.meta.dir, '..')
export const REPO_ROOT = resolve(PERF_ROOT, '..')

type Env = Record<string, string | undefined>

/**
 * The machine's shared perf folder. The default work dir lives here, and so does the stack lock,
 * whatever work dir a session picks: the lock is what keeps a second stack off the machine.
 */
export const cacheRoot = (env: Env = process.env) =>
  join(env.XDG_CACHE_HOME || join(homedir(), '.cache'), 'musichoarder-perf')

// The generated library lives outside the checkout: its paths end up in API responses, so a short,
// stable location keeps payload sizes comparable between checkouts and worktrees.
export const workRoot = (env: Env = process.env) => resolve(env.PERF_WORK_DIR || cacheRoot(env))

/**
 * Names this work dir's Docker container and volume apart from every other work dir's, so two
 * worktrees never share a database. The default work dir has no instance name, which keeps the
 * names (and the seeded volumes) it had before instances existed. `PERF_INSTANCE` names one
 * outright, e.g. to adopt an existing `mh-perf-pgdata-medium-s4` volume.
 */
export function instanceName(env: Env = process.env): string {
  const explicit = env.PERF_INSTANCE?.trim()
  if (explicit) {
    if (!/^[a-z0-9][a-z0-9_.-]{0,31}$/.test(explicit))
      throw new Error(`PERF_INSTANCE "${explicit}" must be lowercase letters, digits, ".", "_" or "-" (at most 32).`)
    return explicit
  }
  const root = workRoot(env)
  if (root === resolve(cacheRoot(env))) return ''
  return createHash('sha256').update(root).digest('hex').slice(0, 8)
}

/** Library sizes the seeder knows. `medium` is roughly a large personal library. */
export const SIZES = { tiny: 300, small: 2_000, medium: 10_000, large: 30_000 } as const
export type SizeName = keyof typeof SIZES

export interface StackConfig {
  size: SizeName
  songs: number
  workDir: string
  /** '' for the default work dir; see `instanceName`. */
  instance: string
  /** The machine-wide lock file: one local stack at a time. */
  lockFile: string
  pgPort: number
  apiPort: number
  webPort: number
  pgContainer: string
  pgVolume: string
  apiUrl: string
  webUrl: string
  connection: string
  ownerEmail: string
}

export function stackConfig(size: SizeName, env: Env = process.env): StackConfig {
  const port = (name: string, fallback: number) => {
    const value = Number(env[name] || fallback)
    if (!Number.isInteger(value) || value < 1 || value > 65535) throw new Error(`${name} must be a port number, not "${env[name]}".`)
    return value
  }
  const pgPort = port('PERF_PG_PORT', 55432)
  const apiPort = port('PERF_API_PORT', 5599)
  const webPort = port('PERF_WEB_PORT', 5598)
  const instance = instanceName(env)
  const suffix = instance ? `${size}-${instance}` : size
  return {
    size,
    songs: SIZES[size],
    workDir: join(workRoot(env), size),
    instance,
    lockFile: join(cacheRoot(env), 'stack.lock'),
    pgPort,
    apiPort,
    webPort,
    pgContainer: `mh-perf-postgres-${suffix}`,
    pgVolume: `mh-perf-pgdata-${suffix}`,
    apiUrl: `http://127.0.0.1:${apiPort}`,
    // "localhost", not 127.0.0.1: Chromium accepts the session cookie's Secure flag on it.
    webUrl: `http://localhost:${webPort}`,
    // A throwaway database bound to loopback, holding only generated rows.
    connection: `Host=127.0.0.1;Port=${pgPort};Database=musichoarderdb;Username=postgres;Password=perf`,
    ownerEmail: 'perf-admin@example.test',
  }
}

/** What a run measures: a local stack this suite started, or any instance by URL. */
export interface Target {
  /** Root the API paths are appended to: the API itself, or `<frontend>/api/mh`. */
  apiBase: string
  /** Frontend origin, when the browser scenarios can run. */
  webBase: string | null
  /** `<frontend>/api/mh`, when the proxy hop can be measured next to the direct call. */
  proxyBase: string | null
  headers: Record<string, string>
  /** Cookies for the browser, as `name=value` pairs. */
  cookies: string[]
  local: StackConfig | null
  label: string
}
