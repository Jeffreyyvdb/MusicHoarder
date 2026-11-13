// The browser half of a run, as its own Node process: `node src/web/run.ts <job.json>`.
// The CLI (Bun) writes the job, starts this, and reads the report back from the job's `out` path.

import { readFileSync, writeFileSync } from 'node:fs'
import type { Target } from '../config.ts'
import { runWebBench, type WebBenchOptions } from './bench.ts'

interface Job {
  target: Target
  options: WebBenchOptions
  out: string
}

const job = JSON.parse(readFileSync(process.argv[2]!, 'utf8')) as Job
try {
  writeFileSync(job.out, JSON.stringify(await runWebBench(job.target, job.options)))
} catch (error) {
  console.error((error as Error).message)
  process.exit(1)
}
