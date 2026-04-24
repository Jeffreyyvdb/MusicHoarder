"use client"

import { useState, useEffect, useRef, useCallback } from "react"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Progress } from "@/components/ui/progress"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import {
  Music,
  FolderInput,
  FolderOutput,
  Clock,
  CheckCircle2,
  AlertCircle,
  XCircle,
  Disc3,
  Sparkles,
  Copy,
  FileWarning,
  ArrowRight,
  PackageCheck,
  Search,
  Play,
  Pause,
  RefreshCw,
  Zap,
} from "lucide-react"
import Link from "next/link"
import {
  fetchOverview,
  triggerEnrichmentScan,
  triggerFingerprint,
  triggerEnrich,
  triggerBuild,
  pauseStep,
  resumeStep,
  openProgressStream,
  type ApiOverview,
  type ProgressSnapshot,
  type StepSnapshot,
} from "@/lib/api-client"
import { isDemoMode } from "@/lib/app-mode"

const initialOverview: ApiOverview = {
  sourcePath: "/",
  destinationPath: "/",
  job: {
    status: "completed",
    startedAt: new Date().toISOString(),
    tracksDiscovered: 0,
    tracksProcessed: 0,
    tracksFingerprinted: 0,
    tracksEnriched: 0,
    tracksBuildEligible: 0,
    tracksCopied: 0,
    tracksReview: 0,
    tracksFailed: 0,
  },
  recentActivity: [],
}

type StatusBannerType = "success" | "error" | "info"

export default function OverviewPage() {
  const [overview, setOverview] = useState<ApiOverview | null>(null)
  const [liveProgress, setLiveProgress] = useState<ProgressSnapshot | null>(null)
  const [banner, setBanner] = useState<{ type: StatusBannerType; text: string } | null>(null)
  const [triggering, setTriggering] = useState<string | null>(null)
  const sseCleanupRef = useRef<(() => void) | null>(null)

  const loadOverview = useCallback(async () => {
    try {
      const data = await fetchOverview()
      setOverview(data)
    } catch {
      // Silently ignore
    }
  }, [])

  const connectSse = useCallback(() => {
    if (isDemoMode) return
    sseCleanupRef.current?.()

    sseCleanupRef.current = openProgressStream(
      (snapshot) => {
        setLiveProgress(snapshot)
      },
      () => {
        sseCleanupRef.current = null
        loadOverview()
        setTimeout(connectSse, 2000)
      }
    )
  }, [loadOverview])

  useEffect(() => {
    loadOverview()
    connectSse()
    const interval = setInterval(loadOverview, 15_000)
    return () => {
      clearInterval(interval)
      sseCleanupRef.current?.()
    }
  }, [loadOverview, connectSse])

  const showBanner = (type: StatusBannerType, text: string) => {
    setBanner({ type, text })
    setTimeout(() => setBanner(null), 6_000)
  }

  const handleStepAction = async (step: string, action: "start" | "pause" | "resume") => {
    setTriggering(step)
    try {
      if (action === "start") {
        const fn =
          step === "scan" ? triggerEnrichmentScan
          : step === "fingerprint" ? triggerFingerprint
          : step === "enrich" ? triggerEnrich
          : triggerBuild
        const result = await fn()
        if (!result.ok) {
          showBanner("error", result.message)
        } else {
          showBanner("success", `${step} started`)
        }
      } else if (action === "pause") {
        await pauseStep(step)
        showBanner("info", `${step} paused`)
      } else {
        await resumeStep(step)
        showBanner("info", `${step} resumed`)
      }
    } catch {
      showBanner("error", `Failed to ${action} ${step}. API may be unavailable.`)
    } finally {
      setTriggering(null)
    }
  }

  const job = overview?.job ?? initialOverview.job

  const scanSnap = liveProgress?.scan
  const fpSnap = liveProgress?.fingerprint
  const enrichSnap = liveProgress?.enrich
  const buildSnap = liveProgress?.build

  const scanRunning = scanSnap?.status === "Running"
  const fpRunning = fpSnap?.status === "Running"
  const enrichRunning = enrichSnap?.status === "Running"
  const buildRunning = buildSnap?.status === "Running"

  const [elapsedMin, setElapsedMin] = useState<number | null>(null)
  useEffect(() => {
    const startedAt = overview?.job?.startedAt
    if (!startedAt) return
    const update = () =>
      setElapsedMin(Math.floor((Date.now() - new Date(startedAt).getTime()) / 60_000))
    update()
    const t = setInterval(update, 60_000)
    return () => clearInterval(t)
  }, [overview?.job?.startedAt])

  const discovered = Math.max(liveProgress?.discovered ?? 0, job.tracksDiscovered)
  const scanned = scanRunning
    ? Math.max(liveProgress?.scanned ?? 0, job.tracksProcessed)
    : job.tracksProcessed
  const fingerprinted = fpRunning
    ? Math.max(liveProgress?.fingerprinted ?? 0, job.tracksFingerprinted ?? 0)
    : (job.tracksFingerprinted ?? 0)
  const enriched = enrichRunning
    ? Math.max(liveProgress?.enriched ?? 0, job.tracksEnriched ?? 0)
    : (job.tracksEnriched ?? 0)
  const buildEligible = job.tracksBuildEligible ?? 0
  const built = buildRunning
    ? Math.max(liveProgress?.built ?? 0, job.tracksCopied)
    : job.tracksCopied
  const failed = Math.max(liveProgress?.failed ?? 0, job.tracksFailed)

  const scanPct = discovered > 0 ? Math.min(100, (scanned / discovered) * 100) : 0
  const fpPct = discovered > 0 ? Math.min(100, (fingerprinted / discovered) * 100) : 0
  const enrichPct = discovered > 0 ? Math.min(100, (enriched / discovered) * 100) : 0
  const buildPct = buildEligible > 0 ? Math.min(100, (built / buildEligible) * 100) : 0

  const runningLabels: string[] = []
  if (scanRunning) runningLabels.push("Scanning")
  if (fpRunning) runningLabels.push("Fingerprinting")
  if (enrichRunning) runningLabels.push("Enriching")
  if (buildRunning) runningLabels.push("Building")
  const overallStatus = runningLabels.length > 0 ? runningLabels.join(", ") : "Idle"

  const enrichPaused = enrichSnap?.isPaused ?? false

  return (
    <main className="flex-1 p-4 md:p-6 lg:p-8">
        <div className="mx-auto max-w-7xl space-y-6">
          {/* Page Header */}
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h1 className="text-2xl font-bold md:text-3xl">Pipeline Overview</h1>
              <p className="text-muted-foreground">Your music pipeline runs automatically on startup</p>
            </div>
          </div>

          {/* Status Banner */}
          {(isDemoMode || banner) && (
            <div
              className={`rounded-md border px-3 py-2 text-sm ${
                banner?.type === "error"
                  ? "border-red-500/30 bg-red-500/10 text-red-400"
                  : banner?.type === "success"
                    ? "border-green-500/30 bg-green-500/10 text-green-400"
                    : "border-border bg-card text-muted-foreground"
              }`}
            >
              {isDemoMode && !banner && <p>Demo mode is enabled. Showing fake data.</p>}
              {banner && <p>{banner.text}</p>}
            </div>
          )}

          {/* Source / Destination */}
          <Card>
            <CardContent className="p-4 md:p-6">
              <div className="flex flex-col gap-4 md:flex-row md:items-center md:gap-6">
                <div className="flex flex-1 items-center gap-3 min-w-0">
                  <div className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-secondary">
                    <FolderInput className="size-5 text-muted-foreground" />
                  </div>
                  <div className="min-w-0">
                    <p className="text-xs text-muted-foreground">Source</p>
                    <p className="truncate font-medium">{overview?.sourcePath ?? "—"}</p>
                  </div>
                </div>
                <ArrowRight className="hidden size-5 text-muted-foreground md:block" />
                <div className="flex flex-1 items-center gap-3 min-w-0">
                  <div className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
                    <FolderOutput className="size-5 text-primary" />
                  </div>
                  <div className="min-w-0">
                    <p className="text-xs text-muted-foreground">Destination</p>
                    <p className="truncate font-medium">{overview?.destinationPath ?? "—"}</p>
                  </div>
                </div>
              </div>
            </CardContent>
          </Card>

          {/* Pipeline Progress */}
          <Card>
            <CardHeader className="pb-3">
              <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
                <CardTitle className="mr-auto text-lg">Pipeline</CardTitle>
                <div className="flex shrink-0 items-center gap-2">
                  <div className="flex items-center gap-1.5 text-sm text-muted-foreground">
                    <Clock className="size-4 shrink-0" />
                    <span className="whitespace-nowrap">{elapsedMin !== null ? `${elapsedMin} min` : "—"}</span>
                  </div>
                  <StatusBadge status={overallStatus} />
                </div>
              </div>
            </CardHeader>
            <CardContent className="space-y-5">
              <PipelineStage
                icon={Search}
                label="Scan"
                count={scanned}
                total={discovered}
                unit="files"
                progress={scanPct}
                step={scanSnap}
                triggering={triggering}
                stepKey="scan"
                onAction={handleStepAction}
                mode="trigger"
              />
              <PipelineStage
                icon={Disc3}
                label="Fingerprint"
                count={fingerprinted}
                total={discovered}
                unit="tracks"
                progress={fpPct}
                step={fpSnap}
                triggering={triggering}
                stepKey="fingerprint"
                onAction={handleStepAction}
                mode="auto"
                subtitle="Runs automatically after scan"
              />
              <PipelineStage
                icon={Sparkles}
                label="Enrich"
                count={enriched}
                total={discovered}
                unit="tracks"
                progress={enrichPct}
                step={enrichSnap}
                triggering={triggering}
                stepKey="enrich"
                onAction={handleStepAction}
                mode="continuous"
                subtitle={enrichPaused
                  ? "Paused — tracks will queue until resumed"
                  : "Processes tracks as they arrive from fingerprinting"}
              />
              <PipelineStage
                icon={PackageCheck}
                label="Build Library"
                count={built}
                total={buildEligible}
                unit="copied"
                progress={buildPct}
                step={buildSnap}
                triggering={triggering}
                stepKey="build"
                onAction={handleStepAction}
                mode="trigger"
                subtitle={buildEligible < discovered && buildEligible > 0
                  ? `${(discovered - buildEligible).toLocaleString()} tracks need review first`
                  : undefined}
              />
            </CardContent>
          </Card>

          {/* Stats Grid */}
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
            <StatCard
              icon={Disc3}
              label="Discovered"
              value={discovered}
              color="text-foreground"
              bgColor="bg-secondary"
            />
            <StatCard
              icon={Sparkles}
              label="Enriched"
              value={job.tracksEnriched ?? 0}
              color="text-blue-400"
              bgColor="bg-blue-400/10"
            />
            <StatCard
              icon={Copy}
              label="Copied"
              value={job.tracksCopied}
              color="text-primary"
              bgColor="bg-primary/10"
            />
            <StatCard
              icon={FileWarning}
              label="Need Review"
              value={job.tracksReview}
              color="text-amber-400"
              bgColor="bg-amber-400/10"
              href="/review"
            />
            <StatCard
              icon={XCircle}
              label="Failed"
              value={job.tracksFailed}
              color="text-red-400"
              bgColor="bg-red-400/10"
            />
          </div>

          {/* Recent Activity & Quick Actions */}
          <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
            <Card className="lg:col-span-2">
              <CardHeader className="pb-2">
                <CardTitle className="text-lg">Recent Activity</CardTitle>
              </CardHeader>
              <CardContent className="p-0">
                <ScrollArea className="h-[320px]">
                  <div className="space-y-1 p-4 pt-0">
                    {(overview?.recentActivity ?? []).length > 0 ? (
                      (overview?.recentActivity ?? []).map((activity) => (
                        <ActivityItem key={activity.id} activity={activity} />
                      ))
                    ) : (
                      <p className="py-4 text-center text-sm text-muted-foreground">
                        No recent activity yet
                      </p>
                    )}
                  </div>
                </ScrollArea>
              </CardContent>
            </Card>

            <Card>
              <CardHeader className="pb-2">
                <CardTitle className="text-lg">Quick Actions</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <Button
                  variant="outline"
                  className="w-full justify-start gap-3 h-auto py-3 hover:bg-muted hover:text-foreground dark:hover:bg-muted/50"
                  disabled={triggering === "scan" || scanRunning}
                  onClick={() => handleStepAction("scan", "start")}
                >
                  <div className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-secondary">
                    <RefreshCw className={`size-4 text-muted-foreground ${scanRunning ? "animate-spin" : ""}`} />
                  </div>
                  <div className="min-w-0 text-left whitespace-normal">
                    <p className="font-medium">Re-scan Source</p>
                    <p className="text-xs text-muted-foreground">
                      {scanRunning ? "Scanning..." : "Check for new or changed files"}
                    </p>
                  </div>
                </Button>
                <Link href="/review" className="block">
                  <Button
                    variant="outline"
                    className="w-full justify-start gap-3 h-auto py-3 hover:bg-muted hover:text-foreground dark:hover:bg-muted/50"
                  >
                    <div className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-amber-400/10">
                      <FileWarning className="size-4 text-amber-400" />
                    </div>
                    <div className="min-w-0 text-left whitespace-normal">
                      <p className="font-medium">Review Tracks</p>
                      <p className="text-xs text-muted-foreground">
                        {job.tracksReview} tracks need attention
                      </p>
                    </div>
                  </Button>
                </Link>
                <Link href="/app" className="block">
                  <Button
                    variant="outline"
                    className="w-full justify-start gap-3 h-auto py-3 hover:bg-muted hover:text-foreground dark:hover:bg-muted/50"
                  >
                    <div className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-primary/10">
                      <Music className="size-4 text-primary" />
                    </div>
                    <div className="min-w-0 text-left whitespace-normal">
                      <p className="font-medium">Browse Library</p>
                      <p className="text-xs text-muted-foreground">View imported tracks</p>
                    </div>
                  </Button>
                </Link>
              </CardContent>
            </Card>
          </div>
        </div>
    </main>
  )
}

// ── Sub-components ────────────────────────────────────────────────────────────

function StatusBadge({ status }: { status: string }) {
  const isRunning = status !== "Idle"

  if (isRunning) {
    return (
      <Badge variant="outline" className="max-w-[160px] gap-1.5 border-primary/40 text-primary sm:max-w-xs">
        <span className="size-1.5 shrink-0 animate-pulse rounded-full bg-primary" />
        <span className="truncate">{status}</span>
      </Badge>
    )
  }

  return (
    <Badge variant="outline" className="gap-1.5 text-muted-foreground">
      <span className="size-1.5 shrink-0 rounded-full bg-muted-foreground/60" />
      Idle
    </Badge>
  )
}

type PipelineMode = "trigger" | "auto" | "continuous"

function PipelineStage({
  icon: Icon,
  label,
  count,
  total,
  unit,
  progress,
  step,
  triggering,
  stepKey,
  onAction,
  subtitle,
  mode = "trigger",
}: {
  icon: React.ComponentType<{ className?: string }>
  label: string
  count: number
  total: number
  unit: string
  progress: number
  step?: StepSnapshot
  triggering: string | null
  stepKey: string
  onAction: (step: string, action: "start" | "pause" | "resume") => void
  subtitle?: string
  mode?: PipelineMode
}) {
  const running = step?.status === "Running"
  const paused = step?.isPaused ?? false
  const done = !running && total > 0 && count >= total
  const isContinuous = mode === "continuous"

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between gap-2 text-sm">
        <span
          className={`flex min-w-0 items-center gap-2 font-medium ${
            running ? "text-foreground"
            : done ? "text-green-400"
            : "text-muted-foreground"
          }`}
        >
          <Icon className={`size-4 shrink-0 ${running ? "text-primary" : done ? "text-green-400" : ""}`} />
          <span className="truncate">{label}</span>
          {running && <span className="size-1.5 shrink-0 animate-pulse rounded-full bg-primary" />}
          {isContinuous && !paused && !running && (
            <Badge variant="outline" className="shrink-0 border-primary/30 px-1.5 py-0 text-[10px] text-primary/70">
              <Zap className="mr-0.5 size-2.5" />
              Active
            </Badge>
          )}
          {paused && !running && (
            <Badge variant="outline" className="shrink-0 border-amber-500/40 px-1.5 py-0 text-[10px] text-amber-400">
              Paused
            </Badge>
          )}
          {done && !running && !isContinuous && (
            <CheckCircle2 className="size-3.5 shrink-0 text-green-400" />
          )}
        </span>
        <div className="flex shrink-0 items-center gap-2">
          <span className="tabular-nums text-muted-foreground">
            {count.toLocaleString()} {unit}
            {total > 0 && (
              <span className="ml-1 text-xs">/ {total.toLocaleString()}</span>
            )}
          </span>
          <StepControl
            stepKey={stepKey}
            running={running}
            paused={paused}
            triggering={triggering}
            onAction={onAction}
            mode={mode}
          />
        </div>
      </div>
      <Progress value={progress} className="h-2" />
      {subtitle && (
        <p className="text-xs text-muted-foreground">{subtitle}</p>
      )}
    </div>
  )
}

function StepControl({
  stepKey,
  running,
  paused,
  triggering,
  onAction,
  mode = "trigger",
}: {
  stepKey: string
  running: boolean
  paused: boolean
  triggering: string | null
  onAction: (step: string, action: "start" | "pause" | "resume") => void
  mode?: PipelineMode
}) {
  const isTriggering = triggering === stepKey
  const isContinuous = mode === "continuous"

  if (running) {
    return (
      <Button
        variant="ghost"
        size="icon"
        className="size-6"
        disabled={isTriggering}
        onClick={() => onAction(stepKey, "pause")}
        title={`Pause ${stepKey}`}
      >
        <Pause className="size-3.5" />
      </Button>
    )
  }

  if (paused) {
    return (
      <Button
        variant="ghost"
        size="icon"
        className="size-6 text-amber-400 hover:text-amber-300"
        disabled={isTriggering}
        onClick={() => onAction(stepKey, "resume")}
        title={`Resume ${stepKey}`}
      >
        <Play className="size-3.5" />
      </Button>
    )
  }

  if (isContinuous) {
    return (
      <Button
        variant="ghost"
        size="icon"
        className="size-6"
        disabled={isTriggering}
        onClick={() => onAction(stepKey, "pause")}
        title={`Pause ${stepKey}`}
      >
        <Pause className="size-3.5" />
      </Button>
    )
  }

  if (mode === "auto") {
    return (
      <Button
        variant="ghost"
        size="icon"
        className="size-6"
        disabled={isTriggering}
        onClick={() => onAction(stepKey, "start")}
        title={`Trigger ${stepKey}`}
      >
        <RefreshCw className="size-3.5" />
      </Button>
    )
  }

  return (
    <Button
      variant="ghost"
      size="icon"
      className="size-6"
      disabled={isTriggering}
      onClick={() => onAction(stepKey, "start")}
      title={`Start ${stepKey}`}
    >
      <Play className="size-3.5" />
    </Button>
  )
}

function StatCard({
  icon: Icon,
  label,
  value,
  color,
  bgColor,
  href,
}: {
  icon: React.ComponentType<{ className?: string }>
  label: string
  value: number
  color: string
  bgColor: string
  href?: string
}) {
  const content = (
    <Card className={href ? "transition-colors hover:bg-secondary/50" : ""}>
      <CardContent className="p-4">
        <div className="flex items-center gap-3">
          <div className={`flex size-10 items-center justify-center rounded-lg ${bgColor}`}>
            <Icon className={`size-5 ${color}`} />
          </div>
          <div>
            <p className="text-2xl font-bold">{value.toLocaleString()}</p>
            <p className="text-xs text-muted-foreground">{label}</p>
          </div>
        </div>
      </CardContent>
    </Card>
  )

  if (href) return <Link href={href}>{content}</Link>
  return content
}

function ActivityItem({
  activity,
}: {
  activity: {
    type: "discovered" | "copied" | "enriched" | "review" | "failed"
    track: string
    artist: string
    time: string
  }
}) {
  const config = {
    discovered: { icon: Disc3, color: "text-foreground", bg: "bg-secondary", label: "Discovered" },
    copied: { icon: CheckCircle2, color: "text-primary", bg: "bg-primary/10", label: "Copied" },
    enriched: { icon: Sparkles, color: "text-blue-400", bg: "bg-blue-400/10", label: "Enriched" },
    review: { icon: AlertCircle, color: "text-amber-400", bg: "bg-amber-400/10", label: "Needs Review" },
    failed: { icon: XCircle, color: "text-red-400", bg: "bg-red-400/10", label: "Failed" },
  }

  const { icon: Icon, color, bg, label } = config[activity.type]

  return (
    <div className="flex items-center gap-3 rounded-lg p-2 transition-colors hover:bg-secondary/50">
      <div className={`flex size-8 shrink-0 items-center justify-center rounded-lg ${bg}`}>
        <Icon className={`size-4 ${color}`} />
      </div>
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium">{activity.track}</p>
        <p className="truncate text-xs text-muted-foreground">{activity.artist}</p>
      </div>
      <div className="shrink-0 text-right">
        <Badge variant="outline" className="text-xs">
          {label}
        </Badge>
        <p className="mt-1 text-xs text-muted-foreground">{activity.time}</p>
      </div>
    </div>
  )
}
