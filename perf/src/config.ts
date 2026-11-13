import { homedir } from 'node:os'
import { join, resolve } from 'node:path'

export const PERF_ROOT = resolve(import.meta.dir, '..')
export const REPO_ROOT = resolve(PERF_ROOT, '..')

// The generated library lives outside the checkout: its paths end up in API responses, so a short,
// stable location keeps payload sizes comparable between checkouts and worktrees.
const WORK_ROOT = process.env.PERF_WORK_DIR ?? join(process.env.XDG_CACHE_HOME ?? join(homedir(), '.cache'), 'musichoarder-perf')

/** Library sizes the seeder knows. `medium` is roughly a large personal library. */
export const SIZES = { tiny: 300, small: 2_000, medium: 10_000, large: 30_000 } as const
export type SizeName = keyof typeof SIZES

const port = (name: string, fallback: number) => Number(process.env[name] ?? fallback)

export interface StackConfig {
  size: SizeName
  songs: number
  workDir: string
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

export function stackConfig(size: SizeName): StackConfig {
  const pgPort = port('PERF_PG_PORT', 55432)
  const apiPort = port('PERF_API_PORT', 5599)
  const webPort = port('PERF_WEB_PORT', 5598)
  return {
    size,
    songs: SIZES[size],
    workDir: join(WORK_ROOT, size),
    pgPort,
    apiPort,
    webPort,
    pgContainer: `mh-perf-postgres-${size}`,
    pgVolume: `mh-perf-pgdata-${size}`,
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
