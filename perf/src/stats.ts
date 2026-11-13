export interface Summary {
  n: number
  min: number
  median: number
  mean: number
  p95: number
  max: number
}

/** Nearest-rank percentile over an unsorted sample; `p` in 0..100. */
export function percentile(values: number[], p: number): number {
  if (values.length === 0) return NaN
  const sorted = [...values].sort((a, b) => a - b)
  const rank = Math.ceil((p / 100) * sorted.length)
  return sorted[Math.min(sorted.length - 1, Math.max(0, rank - 1))]!
}

export function summarize(values: number[]): Summary {
  const n = values.length
  if (n === 0) return { n: 0, min: NaN, median: NaN, mean: NaN, p95: NaN, max: NaN }
  return {
    n,
    min: Math.min(...values),
    median: percentile(values, 50),
    mean: values.reduce((a, b) => a + b, 0) / n,
    p95: percentile(values, 95),
    max: Math.max(...values),
  }
}

export function median(values: number[]): number {
  return percentile(values, 50)
}

export function formatMs(ms: number | null | undefined): string {
  if (ms == null || Number.isNaN(ms)) return '–'
  if (ms >= 10_000) return `${(ms / 1000).toFixed(1)} s`
  if (ms >= 1000) return `${(ms / 1000).toFixed(2)} s`
  if (ms >= 100) return `${Math.round(ms)} ms`
  return `${ms.toFixed(1)} ms`
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || Number.isNaN(bytes)) return '–'
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(bytes >= 10 * 1024 * 1024 ? 0 : 1)} MB`
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(bytes >= 10 * 1024 ? 0 : 1)} kB`
  return `${Math.round(bytes)} B`
}
