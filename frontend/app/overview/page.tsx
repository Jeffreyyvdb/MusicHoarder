"use client"

import { useState, useEffect } from "react"
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
  Play,
  Pause,
  RotateCcw,
  Disc3,
  Sparkles,
  Copy,
  FileWarning,
  ArrowRight,
} from "lucide-react"
import Link from "next/link"
import { mockImportJob, mockRecentActivity } from "@/lib/mock-data"
import { AppHeader } from "@/components/app-header"
import { fetchStats, startScan } from "@/lib/api-client"

export default function OverviewPage() {
  const [job, setJob] = useState(mockImportJob)
  const [isRunning, setIsRunning] = useState(job.status === "running")
  const [apiError, setApiError] = useState<string | null>(null)
  const [scanMessage, setScanMessage] = useState<string | null>(null)

  // Keep API-backed stats fresh while preserving mock fallback behavior.
  useEffect(() => {
    let active = true

    const loadStats = async () => {
      try {
        const stats = await fetchStats()
        if (!active) return

        const totalTracks = stats.tracks?.total ?? job.tracksDiscovered
        const deletedTracks = stats.tracks?.deleted ?? 0

        setJob((prev) => ({
          ...prev,
          tracksDiscovered: totalTracks,
          tracksProcessed: totalTracks,
          tracksCopied: totalTracks,
          tracksReview: Math.max(0, prev.tracksReview - deletedTracks),
          tracksFailed: deletedTracks,
        }))
        setApiError(null)
      } catch {
        if (!active) return
        setApiError("Using mock overview data because API is currently unavailable.")
      }
    }

    loadStats()
    const interval = setInterval(loadStats, 15000)
    return () => {
      active = false
      clearInterval(interval)
    }
  }, [job.tracksDiscovered])

  useEffect(() => {
    if (!isRunning) return

    const interval = setInterval(() => {
      setJob((prev) => {
        if (prev.tracksProcessed >= prev.tracksDiscovered) return prev
        const newProcessed = Math.min(prev.tracksProcessed + 1, prev.tracksDiscovered)
        const newCopied = Math.min(prev.tracksCopied + (Math.random() > 0.15 ? 1 : 0), newProcessed)
        return {
          ...prev,
          tracksProcessed: newProcessed,
          tracksCopied: newCopied,
        }
      })
    }, 2000)

    return () => clearInterval(interval)
  }, [isRunning])

  const progress = job.tracksDiscovered > 0 ? (job.tracksProcessed / job.tracksDiscovered) * 100 : 0
  const elapsedTime = Math.floor((Date.now() - job.startedAt.getTime()) / 1000 / 60)

  return (
    <div className="flex min-h-screen flex-col bg-background">
      <AppHeader />

      <main className="flex-1 p-4 md:p-6 lg:p-8">
        <div className="mx-auto max-w-7xl space-y-6">
          {/* Page Title */}
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h1 className="text-2xl font-bold md:text-3xl">Import Overview</h1>
              <p className="text-muted-foreground">Monitor your music import progress</p>
            </div>
            <div className="flex gap-2">
              <Button
                variant={isRunning ? "secondary" : "default"}
                onClick={() => setIsRunning(!isRunning)}
                className="gap-2"
              >
                {isRunning ? (
                  <>
                    <Pause className="size-4" /> Pause
                  </>
                ) : (
                  <>
                    <Play className="size-4" /> Resume
                  </>
                )}
              </Button>
              <Button
                variant="outline"
                className="gap-2"
                onClick={async () => {
                  try {
                    const response = await startScan()
                    setScanMessage(`Scan started (${response.scanId})`)
                    setApiError(null)
                  } catch {
                    setScanMessage("Could not start scan. API may be unavailable.")
                  }
                }}
              >
                <RotateCcw className="size-4" />
                <span className="hidden sm:inline">Start Scan</span>
              </Button>
            </div>
          </div>
          {(apiError || scanMessage) && (
            <div className="rounded-md border border-border bg-card px-3 py-2 text-sm text-muted-foreground">
              {scanMessage ?? apiError}
            </div>
          )}

          {/* Source/Destination */}
          <Card>
            <CardContent className="p-4 md:p-6">
              <div className="flex flex-col gap-4 md:flex-row md:items-center md:gap-6">
                <div className="flex flex-1 items-center gap-3 min-w-0">
                  <div className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-secondary">
                    <FolderInput className="size-5 text-muted-foreground" />
                  </div>
                  <div className="min-w-0">
                    <p className="text-xs text-muted-foreground">Source</p>
                    <p className="truncate font-medium">{job.sourcePath}</p>
                  </div>
                </div>
                <ArrowRight className="hidden size-5 text-muted-foreground md:block" />
                <div className="flex flex-1 items-center gap-3 min-w-0">
                  <div className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
                    <FolderOutput className="size-5 text-primary" />
                  </div>
                  <div className="min-w-0">
                    <p className="text-xs text-muted-foreground">Destination</p>
                    <p className="truncate font-medium">{job.destinationPath}</p>
                  </div>
                </div>
              </div>
            </CardContent>
          </Card>

          {/* Progress Section */}
          <Card>
            <CardHeader className="pb-2">
              <div className="flex items-center justify-between">
                <CardTitle className="text-lg">Progress</CardTitle>
                <div className="flex items-center gap-2 text-sm text-muted-foreground">
                  <Clock className="size-4" />
                  <span>{elapsedTime} min elapsed</span>
                </div>
              </div>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <div className="flex items-center justify-between text-sm">
                  <span className="text-muted-foreground">
                    {job.tracksProcessed} of {job.tracksDiscovered} tracks processed
                  </span>
                  <span className="font-medium">{progress.toFixed(1)}%</span>
                </div>
                <Progress value={progress} className="h-3" />
              </div>

              {/* Status indicator */}
              <div className="flex items-center gap-2">
                {isRunning ? (
                  <>
                    <div className="size-2 animate-pulse rounded-full bg-primary" />
                    <span className="text-sm text-primary">Processing...</span>
                  </>
                ) : (
                  <>
                    <div className="size-2 rounded-full bg-muted-foreground" />
                    <span className="text-sm text-muted-foreground">Paused</span>
                  </>
                )}
              </div>
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
              label="Processed"
              value={job.tracksProcessed}
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
            {/* Recent Activity */}
            <Card className="lg:col-span-2">
              <CardHeader className="pb-2">
                <CardTitle className="text-lg">Recent Activity</CardTitle>
              </CardHeader>
              <CardContent className="p-0">
                <ScrollArea className="h-[320px]">
                  <div className="space-y-1 p-4 pt-0">
                    {mockRecentActivity.map((activity) => (
                      <ActivityItem key={activity.id} activity={activity} />
                    ))}
                  </div>
                </ScrollArea>
              </CardContent>
            </Card>

            {/* Quick Actions */}
            <Card>
              <CardHeader className="pb-2">
                <CardTitle className="text-lg">Quick Actions</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <Link href="/review" className="block">
                  <Button variant="outline" className="w-full justify-start gap-3 h-auto py-3">
                    <div className="flex size-8 items-center justify-center rounded-lg bg-amber-400/10">
                      <FileWarning className="size-4 text-amber-400" />
                    </div>
                    <div className="text-left">
                      <p className="font-medium">Review Tracks</p>
                      <p className="text-xs text-muted-foreground">{job.tracksReview} tracks need attention</p>
                    </div>
                  </Button>
                </Link>
                <Link href="/" className="block">
                  <Button variant="outline" className="w-full justify-start gap-3 h-auto py-3">
                    <div className="flex size-8 items-center justify-center rounded-lg bg-primary/10">
                      <Music className="size-4 text-primary" />
                    </div>
                    <div className="text-left">
                      <p className="font-medium">Browse Library</p>
                      <p className="text-xs text-muted-foreground">View imported tracks</p>
                    </div>
                  </Button>
                </Link>
                <Button variant="outline" className="w-full justify-start gap-3 h-auto py-3">
                  <div className="flex size-8 items-center justify-center rounded-lg bg-secondary">
                    <FolderInput className="size-4 text-muted-foreground" />
                  </div>
                  <div className="text-left">
                    <p className="font-medium">New Import</p>
                    <p className="text-xs text-muted-foreground">Select another folder</p>
                  </div>
                </Button>
              </CardContent>
            </Card>
          </div>
        </div>
      </main>
    </div>
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

  if (href) {
    return <Link href={href}>{content}</Link>
  }

  return content
}

function ActivityItem({
  activity,
}: {
  activity: { type: "discovered" | "copied" | "enriched" | "review" | "failed"; track: string; artist: string; time: string }
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
