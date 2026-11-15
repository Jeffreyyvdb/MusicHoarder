import { describe, expect, test } from 'bun:test'
import { join } from 'node:path'
import { instanceName, stackConfig } from './config.ts'

const HOME_CACHE = { XDG_CACHE_HOME: '/home/u/.cache' }

describe('per-instance names', () => {
  test('the default work dir keeps the names it always had', () => {
    const cfg = stackConfig('medium', HOME_CACHE)
    expect(cfg.instance).toBe('')
    expect(cfg.pgContainer).toBe('mh-perf-postgres-medium')
    expect(cfg.pgVolume).toBe('mh-perf-pgdata-medium')
    expect(cfg.workDir).toBe('/home/u/.cache/musichoarder-perf/medium')
  })

  test('naming the default work dir explicitly is still the default', () => {
    expect(instanceName({ ...HOME_CACHE, PERF_WORK_DIR: '/home/u/.cache/musichoarder-perf/' })).toBe('')
  })

  test('another work dir gets its own, stable container and volume', () => {
    const a = stackConfig('small', { ...HOME_CACHE, PERF_WORK_DIR: '/home/u/.cache/musichoarder-perf-s6' })
    const again = stackConfig('small', { ...HOME_CACHE, PERF_WORK_DIR: '/home/u/.cache/musichoarder-perf-s6' })
    const b = stackConfig('small', { ...HOME_CACHE, PERF_WORK_DIR: '/home/u/.cache/musichoarder-perf-s7' })
    expect(a.instance).toMatch(/^[0-9a-f]{8}$/)
    expect(a.pgContainer).toBe(`mh-perf-postgres-small-${a.instance}`)
    expect(a.pgVolume).toBe(`mh-perf-pgdata-small-${a.instance}`)
    expect(again.pgContainer).toBe(a.pgContainer)
    expect(b.pgContainer).not.toBe(a.pgContainer)
    expect(a.workDir).toBe('/home/u/.cache/musichoarder-perf-s6/small')
  })

  test('PERF_INSTANCE names one outright, e.g. to adopt an existing volume', () => {
    const cfg = stackConfig('medium', { ...HOME_CACHE, PERF_WORK_DIR: '/tmp/x', PERF_INSTANCE: 's4' })
    expect(cfg.pgContainer).toBe('mh-perf-postgres-medium-s4')
    expect(cfg.pgVolume).toBe('mh-perf-pgdata-medium-s4')
    expect(() => instanceName({ PERF_INSTANCE: 'Bad Name' })).toThrow('PERF_INSTANCE')
  })

  test('the lock lives in the shared cache folder, whatever the work dir', () => {
    const cfg = stackConfig('tiny', { ...HOME_CACHE, PERF_WORK_DIR: '/tmp/elsewhere' })
    expect(cfg.lockFile).toBe(join('/home/u/.cache/musichoarder-perf', 'stack.lock'))
  })

  test('ports come from the environment and are checked', () => {
    const cfg = stackConfig('tiny', { ...HOME_CACHE, PERF_PG_PORT: '55861', PERF_API_PORT: '5861', PERF_WEB_PORT: '5862' })
    expect([cfg.pgPort, cfg.apiPort, cfg.webPort]).toEqual([55861, 5861, 5862])
    expect(cfg.webUrl).toBe('http://localhost:5862')
    expect(() => stackConfig('tiny', { PERF_API_PORT: 'abc' })).toThrow('PERF_API_PORT')
  })
})
