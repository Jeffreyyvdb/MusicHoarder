import { afterEach, beforeEach, describe, expect, test } from 'bun:test'
import { existsSync, mkdtempSync, rmSync, utimesSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { acquire, LockBusyError, readLock, ref, releaseLock, releaseMine, tryAcquire, updateLock, type LockHolder } from './lock.ts'

const LOCK_MODULE = join(import.meta.dir, 'lock.ts')
let dir: string
let path: string

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'mh-perf-lock-'))
  path = join(dir, 'stack.lock')
})
afterEach(() => rmSync(dir, { recursive: true, force: true }))

const holder = (over: Partial<LockHolder> = {}): LockHolder => ({
  owner: ref(process.pid),
  stack: [],
  command: 'all',
  workDir: '/work/a/small',
  size: 'small',
  startedAt: new Date().toISOString(),
  ...over,
})

/** A pid that certainly belonged to a process that has exited. */
const deadPid = () => Bun.spawnSync(['true']).pid

/** Another process that takes the lock, prints "taken", and then does `then`. */
function child(then: string) {
  const code = `
    const { acquire, ref, releaseMine } = await import(${JSON.stringify(LOCK_MODULE)})
    await acquire(${JSON.stringify(path)}, { owner: ref(process.pid), stack: [], command: 'all', workDir: '/work/b/small', size: 'small', startedAt: new Date().toISOString() }, { wait: false })
    process.on('exit', () => releaseMine(${JSON.stringify(path)}))
    console.log('taken')
    ${then}
  `
  return Bun.spawn(['bun', '-e', code], { stdout: 'pipe', stderr: 'pipe' })
}

async function untilTaken(proc: ReturnType<typeof child>) {
  const reader = proc.stdout.getReader()
  let out = ''
  while (!out.includes('taken')) {
    const { value, done } = await reader.read()
    if (done) throw new Error(`child exited before taking the lock: ${await new Response(proc.stderr).text()}`)
    out += new TextDecoder().decode(value)
  }
  reader.releaseLock()
}

describe('stack lock', () => {
  test('takes a free lock and records who holds it', () => {
    expect(tryAcquire(path, holder())).toEqual({ ok: true })
    expect(readLock(path)?.owner?.pid).toBe(process.pid)
  })

  test('replaces a lock whose processes are all gone', () => {
    writeFileSync(path, JSON.stringify(holder({ owner: { pid: deadPid(), start: null }, stack: [{ pid: deadPid(), start: null }] })))
    expect(tryAcquire(path, holder())).toEqual({ ok: true })
    expect(readLock(path)?.owner?.pid).toBe(process.pid)
  })

  test('a reused pid does not keep a stale lock alive', () => {
    // Our own pid, but with a start time that is not ours: the process that took it is gone.
    writeFileSync(path, JSON.stringify(holder({ owner: { pid: process.pid, start: '1' } })))
    expect(tryAcquire(path, holder({ workDir: '/work/b/small' })).ok).toBe(true)
  })

  test('replaces an empty or unreadable lock only once it is old', () => {
    writeFileSync(path, '')
    expect(tryAcquire(path, holder()).ok).toBe(false)
    const old = new Date(Date.now() - 60_000)
    utimesSync(path, old, old)
    expect(tryAcquire(path, holder()).ok).toBe(true)
  })

  test('a live holder keeps it: --no-wait fails naming the holder', async () => {
    const other = child('await new Promise((r) => setTimeout(r, 30_000))')
    try {
      await untilTaken(other)
      const attempt = acquire(path, holder(), { wait: false })
      await expect(attempt).rejects.toBeInstanceOf(LockBusyError)
      await expect(attempt).rejects.toThrow(`pid ${other.pid}`)
      await expect(attempt).rejects.toThrow('/work/b/small')
    } finally {
      other.kill('SIGKILL')
      await other.exited
    }
  })

  test('waiting takes the lock once the holder exits, and the holder released it on exit', async () => {
    const other = child('await new Promise((r) => setTimeout(r, 600))')
    await untilTaken(other)
    const busy: (LockHolder | null)[] = []
    const started = Date.now()
    await acquire(path, holder(), { wait: true, pollMs: 50, onBusy: (h) => busy.push(h) })
    expect(Date.now() - started).toBeGreaterThan(300)
    expect(busy).toHaveLength(1)
    expect(busy[0]?.owner?.pid).toBe(other.pid)
    expect(await other.exited).toBe(0)
    expect(readLock(path)?.owner?.pid).toBe(process.pid)
  })

  test('released when the holder exits normally or on an error', async () => {
    for (const then of ['', 'throw new Error("boom")']) {
      const other = child(then)
      await other.exited
      expect(existsSync(path)).toBe(false)
    }
  })

  test('a holder killed outright leaves a stale lock that the next taker replaces', async () => {
    const other = child('await new Promise((r) => setTimeout(r, 30_000))')
    await untilTaken(other)
    other.kill('SIGKILL')
    await other.exited
    expect(readLock(path)?.owner?.pid).toBe(other.pid)
    expect(tryAcquire(path, holder()).ok).toBe(true)
  })

  test('after a hand-over the stack keeps it, until its processes stop', async () => {
    const stack = Bun.spawn(['sleep', '30'])
    try {
      expect(tryAcquire(path, holder({ command: 'up' })).ok).toBe(true)
      expect(updateLock(path, (h) => ({ ...h, stack: [ref(stack.pid)] }))).toBe(true)
      expect(updateLock(path, (h) => ({ ...h, owner: null }))).toBe(true)
      // The CLI that took it no longer owns it, so its exit does not release it.
      expect(releaseMine(path)).toBe(false)
      const theirs = holder({ owner: { pid: deadPid(), start: null }, workDir: '/work/b/small' })
      expect(tryAcquire(path, theirs).ok).toBe(false)
    } finally {
      stack.kill('SIGKILL')
      await stack.exited
    }
    expect(tryAcquire(path, holder({ workDir: '/work/b/small' })).ok).toBe(true)
  })

  test('take-over replaces a live holder only when the predicate says so', () => {
    const stack = { pid: process.pid, start: ref(process.pid).start }
    writeFileSync(path, JSON.stringify(holder({ owner: null, stack: [stack], workDir: '/work/a/small' })))
    // What the CLI passes: the holder is this taker's own left-running stack.
    const sameWorkDir = (workDir: string) => (h: LockHolder) => h.workDir === workDir
    expect(tryAcquire(path, holder({ workDir: '/work/b/small' }), sameWorkDir('/work/b/small')).ok).toBe(false)
    expect(tryAcquire(path, holder({ workDir: '/work/a/small' }), sameWorkDir('/work/a/small')).ok).toBe(true)
  })

  test('release removes the lock only when it matches', () => {
    tryAcquire(path, holder())
    expect(releaseLock(path, (h) => h.workDir === '/elsewhere')).toBe(false)
    expect(existsSync(path)).toBe(true)
    expect(releaseLock(path, (h) => h.workDir === '/work/a/small')).toBe(true)
    expect(existsSync(path)).toBe(false)
  })

  test('the guard is a kernel lock: a holder killed inside it does not block the next one', async () => {
    tryAcquire(path, holder())
    // Another process takes the guard and dies holding it; the kernel lets go of it.
    const code = `
      const { dlopen, FFIType } = await import('bun:ffi')
      const { openSync } = await import('node:fs')
      const { symbols } = dlopen(process.platform === 'darwin' ? 'libc.dylib' : 'libc.so.6', { flock: { args: [FFIType.i32, FFIType.i32], returns: FFIType.i32 } })
      symbols.flock(openSync(${JSON.stringify(`${path}.guard`)}, 'a'), 2)
      console.log('taken')
      await new Promise((r) => setTimeout(r, 30_000))
    `
    const other = Bun.spawn(['bun', '-e', code], { stdout: 'pipe' })
    await untilTaken(other as unknown as ReturnType<typeof child>)
    other.kill('SIGKILL')
    await other.exited
    expect(releaseLock(path, () => true)).toBe(true)
  })
})
