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
} from "lucide-react"
import Link from "next/link"
import { AppHeader } from "@/components/app-header"
import {
  fetchOverview,
  triggerEnrichmentScan,
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

const RUNNING_STATUSES = new Set(["Scanning", "Fingerprinting", "Enriching", "Building"])

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
    tracksCopied: 0,
    tracksReview: 0,
    tracksFailed: 0,
  },
  recentActivity: [],
}

type StatusBannerType = "success" | "error" | "info"

function isStepRunning(step?: StepSnapshot): boolean {
  return step?.status === "Running"
}

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
        const fn = step === "scan" ? triggerEnrichmentScan
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

  const isLive = liveProgress != null && !liveProgress.isComplete
  const anyRunning = isLive && RUNNING_STATUSES.has(liveProgress?.status?.split(",")[0]?.trim() ?? "")

  // Elapsed time
  const [elapsedMin, setElapsedMin] = useState<number | null>(null)
  useEffect(() => {
    const startedAt = liveProgress?.startedAt ?? overview?.job?.startedAt
    if (!startedAt) return
    const update = () =>
      setElapsedMin(Math.floor((Date.now() - new Date(startedAt).getTime()) / 60_000))
    update()
    const t = setInterval(update, 60_000)
    return () => clearInterval(t)
  }, [liveProgress?.startedAt, overview?.job?.startedAt])

  // Use SSE counters only when actively running; otherwise use cumulative DB counts
  const discovered = isLive ? liveProgress.discovered : job.tracksDiscovered
  const scanned = isLive ? liveProgress.scanned : job.tracksProcessed
  const fingerprinted = isLive ? liveProgress.fingerprinted : (job.tracksFingerprinted ?? 0)
  const enriched = isLive ? liveProgress.enriched : (job.tracksEnriched ?? 0)
  const built = isLive ? liveProgress.built : job.tracksCopied
  const failed = isLive ? liveProgress.failed : job.tracksFailed

  const scanPct = discovered > 0 ? Math.min(100, (scanned / discovered) * 100) : 0
  const fpPct = discovered > 0 ? Math.min(100, (fingerprinted / discovered) * 100) : 0
  const enrichPct = discovered > 0 ? Math.min(100, (enriched / discovered) * 100) : 0
  const buildPct = discovered > 0 ? Math.min(100, (built / discovered) * 100) : 0

  const scanSnap = liveProgress?.scan
  const fpSnap = liveProgress?.fingerprint
  const enrichSnap = liveProgress?.enrich
  const buildSnap = liveProgress?.build

  const overallStatus = isLive ? (liveProgress.status ?? "Idle") : "Idle"

  return (
    <div className="flex min-h-screen flex-col bg-background">
      <AppHeader />

      <main className="flex-1 p-4 md:p-6 lg:p-8">
        <div className="mx-auto max-w-7xl space-y-6">
          {/* Page Header */}
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h1 className="text-2xl font-bold md:text-3xl">Import Overview</h1>
              <p className="text-muted-foreground">Monitor and control your music pipeline</p>
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
              <div className="flex items-center justify-between">
                <CardTitle className="text-lg">Pipeline Progress</CardTitle>
                <div className="flex items-center gap-3">
                  <div className="flex items-center gap-1.5 text-sm text-muted-foreground">
                    <Clock className="size-4" />
                    <span>{elapsedMin !== null ? `${elapsedMin} min` : "—"}</span>
                  </div>
                  <StatusBadge status={overallStatus} />
                </div>
              </div>
            </CardHeader>
            <CardContent className="space-y-5">
              <PipelineStage
                icon={Search}
                label="Scan Metadata"
                count={scanned}
                total={discovered}
                unit="files"
                progress={scanPct}
                step={scanSnap}
                triggering={triggering}
                stepKey="scan"
                onAction={handleStepAction}
              />
              <PipelineStage
                icon={Disc3}
                label="Fingerprinting"
                count={fingerprinted}
                total={discovered}
                unit="tracks"
                progress={fpPct}
                step={fpSnap}
                triggering={triggering}
                stepKey="fingerprint"
                onAction={handleStepAction}
                autoOnly
              />
              <PipelineStage
                icon={Sparkles}
                label="Enrich Metadata"
                count={enriched}
                total={discovered}
                unit="tracks"
                progress={enrichPct}
                step={enrichSnap}
                triggering={triggering}
                stepKey="enrich"
                onAction={handleStepAction}
              />
              <PipelineStage
                icon={PackageCheck}
                label="Build Library"
                count={built}
                total={discovered}
                unit="copied"
                progress={buildPct}
                step={buildSnap}
                triggering={triggering}
                stepKey="build"
                onAction={handleStepAction}
              />
            </CardContent>
          </Card>

          {/* Stats Grid */}
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
            <StatCard
              icon={Disc3}
              label="Discovered"
              value={job.tracksDiscovered}
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
          <div className="grid gap-6 lg:grid-cols-3">
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
                <Link href="/review" className="block">
                  <Button
                    variant="outline"
                    className="w-full justify-start gap-3 h-auto py-3 hover:bg-muted hover:text-foreground dark:hover:bg-muted/50"
                  >
                    <div className="flex size-8 items-center justify-center rounded-lg bg-amber-400/10">
                      <FileWarning className="size-4 text-amber-400" />
                    </div>
                    <div className="text-left">
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
                    <div className="flex size-8 items-center justify-center rounded-lg bg-primary/10">
                      <Music className="size-4 text-primary" />
                    </div>
                    <div className="text-left">
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
    </div>
  )
}

// ── Sub-components ────────────────────────────────────────────────────────────

function StatusBadge({ status }: { status: string }) {
  const isRunning = status !== "Idle" && status !== "Completed" && status !== "Failed" && status !== "Cancelled"
  const isError = status === "Failed" || status === "Cancelled"

  if (isRunning) {
    return (
      <Badge variant="outline" className="gap-1.5 border-primary/40 text-primary">
        <span className="size-1.5 animate-pulse rounded-full bg-primary" />
        {status}
      </Badge>
    )
  }

  if (isError) {
    return (
      <Badge variant="outline" className="gap-1.5 border-red-500/40 text-red-400">
        <span className="size-1.5 rounded-full bg-red-400" />
        {status}
      </Badge>
    )
  }

  return (
    <Badge variant="outline" className="gap-1.5 text-muted-foreground">
      <span className="size-1.5 rounded-full bg-muted-foreground/60" />
      Idle
    </Badge>
  )
}

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
  autoOnly,
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
  autoOnly?: boolean
}) {
  const running = step?.status === "Running"
  const paused = step?.isPaused ?? false

  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between text-sm">
        <span
          className={`flex items-center gap-2 font-medium ${
            running ? "text-foreground" : "text-muted-foreground"
          }`}
        >
          <Icon className={`size-4 ${running ? "text-primary" : ""}`} />
          {label}
          {running && <span className="size-1.5 animate-pulse rounded-full bg-primary" />}
          {paused && !running && (
            <Badge variant="outline" className="text-[10px] px-1.5 py-0 border-amber-500/40 text-amber-400">
              Paused
            </Badge>
          )}
        </span>
        <div className="flex items-center gap-2">
          <span className="tabular-nums text-muted-foreground">
            {count.toLocaleString()} {unit}
            {total > 0 && !running && (
              <span className="ml-1 text-xs">/ {total.toLocaleString()}</span>
            )}
          </span>
          {!autoOnly && (
            <StepControl
              stepKey={stepKey}
              running={running}
              paused={paused}
              triggering={triggering}
              onAction={onAction}
            />
          )}
          {autoOnly && (running || paused) && (
            <StepControl
              stepKey={stepKey}
              running={running}
              paused={paused}
              triggering={triggering}
              onAction={onAction}
            />
          )}
        </div>
      </div>
      <Progress value={progress} className="h-2" />
    </div>
  )
}

function StepControl({
  stepKey,
  running,
  paused,
  triggering,
  onAction,
}: {
  stepKey: string
  running: boolean
  paused: boolean
  triggering: string | null
  onAction: (step: string, action: "start" | "pause" | "resume") => void
}) {
  const isTriggering = triggering === stepKey

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
