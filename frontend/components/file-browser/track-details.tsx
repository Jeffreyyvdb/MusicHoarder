"use client"

import { useState, useCallback, useMemo } from "react"
import {
  X,
  Music,
  Disc3,
  User,
  Calendar,
  Clock,
  HardDrive,
  Waves,
  FileAudio,
  CheckCircle2,
  AlertCircle,
  Loader2,
  FileText,
  Fingerprint,
  ExternalLink,
  RotateCcw,
  Eye,
  Timer,
  AlignLeft,
} from "lucide-react"
import { Button } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import type { FileItem, LyricsStatus } from "@/lib/types"
import { cn } from "@/lib/utils"
import { resetSongEnrichment, fetchTrackLyrics } from "@/lib/api-client"

interface TrackDetailsProps {
  file: FileItem | null
  onClose: () => void
  onResetEnrichment?: () => void
}

function parseSongId(fileId: string): number | null {
  if (!fileId.startsWith("song:")) return null
  const parsed = Number(fileId.slice(5))
  return Number.isFinite(parsed) ? parsed : null
}

export function TrackDetails({ file, onClose, onResetEnrichment }: TrackDetailsProps) {
  const [resetState, setResetState] = useState<"idle" | "loading" | "success" | "error">("idle")
  const [resetError, setResetError] = useState<string | null>(null)

  const songId = file ? parseSongId(file.id) : null

  const handleResetEnrichment = useCallback(async () => {
    if (songId === null) return
    setResetState("loading")
    setResetError(null)
    try {
      await resetSongEnrichment(songId)
      setResetState("success")
      onResetEnrichment?.()
      setTimeout(() => setResetState("idle"), 3000)
    } catch (err) {
      setResetState("error")
      setResetError(err instanceof Error ? err.message : "Failed to reset enrichment")
      setTimeout(() => { setResetState("idle"); setResetError(null) }, 5000)
    }
  }, [songId, onResetEnrichment])

  if (!file || file.type !== "audio" || !file.metadata) {
    return null
  }

  const { metadata } = file

  return (
    <div className="flex h-full max-h-full flex-col overflow-hidden border-l border-border bg-card">
      <div className="flex items-center justify-between border-b border-border px-4 py-3">
        <h2 className="font-semibold">Track Details</h2>
        <Button variant="ghost" size="icon" onClick={onClose} className="size-8">
          <X className="size-4" />
        </Button>
      </div>

      <ScrollArea className="flex-1 min-h-0">
        <div className="p-4">
          {/* Album Art and Title - Horizontal on mobile sheets */}
          <div className="flex gap-4 mb-4 sm:flex-col sm:gap-0">
            <div className="size-24 shrink-0 overflow-hidden rounded-lg bg-secondary sm:aspect-square sm:size-auto sm:mb-4">
              {metadata.albumArt ? (
                <img
                  src={metadata.albumArt}
                  alt={metadata.album}
                  className="size-full object-cover"
                  crossOrigin="anonymous"
                />
              ) : (
                <div className="flex size-full items-center justify-center">
                  <Music className="size-10 text-muted-foreground sm:size-16" />
                </div>
              )}
            </div>

            {/* Title and Artist */}
            <div className="min-w-0 flex-1 sm:text-center sm:mb-4">
              <h3 className="text-base font-semibold text-balance sm:text-lg">{metadata.title}</h3>
              <p className="text-sm text-muted-foreground sm:text-base">{metadata.artist}</p>
              {/* Status Badge - inline on mobile */}
              <div className="mt-2 sm:hidden">
                <StatusBadge status={metadata.enrichmentStatus} />
              </div>
            </div>
          </div>

          {/* Status Badge - Hidden on mobile (shown inline above) */}
          <div className="mb-4 hidden justify-center sm:flex">
            <StatusBadge status={metadata.enrichmentStatus} />
          </div>

          <Separator className="my-4" />

          {/* Tabs for different info */}
          <Tabs defaultValue="info" className="w-full">
            <TabsList className="grid w-full grid-cols-3">
              <TabsTrigger value="info">Info</TabsTrigger>
              <TabsTrigger value="lyrics">Lyrics</TabsTrigger>
              <TabsTrigger value="sources">Sources</TabsTrigger>
            </TabsList>

            <TabsContent value="info" className="mt-4 space-y-3">
              <InfoRow icon={Disc3} label="Album" value={metadata.album} />
              <InfoRow icon={User} label="Artist" value={metadata.artist} />
              <InfoRow icon={Calendar} label="Year" value={metadata.year > 0 ? metadata.year.toString() : "Unknown"} />
              <InfoRow icon={Music} label="Genre" value={metadata.genre} />
              <Separator className="my-3" />
              <InfoRow icon={Clock} label="Duration" value={formatDuration(metadata.duration)} />
              <InfoRow icon={FileAudio} label="Format" value={metadata.format} />
              <InfoRow icon={Waves} label="Bitrate" value={`${metadata.bitrate} kbps`} />
              <InfoRow icon={Waves} label="Sample Rate" value={`${(metadata.sampleRate / 1000).toFixed(1)} kHz`} />
              <InfoRow icon={HardDrive} label="File Size" value={formatFileSize(metadata.fileSize)} />
              {metadata.fingerprint && (
                <>
                  <Separator className="my-3" />
                  <div className="space-y-1.5">
                    <div className="flex items-center gap-2 text-sm text-muted-foreground">
                      <Fingerprint className="size-4" />
                      <span>Audio Fingerprint</span>
                    </div>
                    <code className="block rounded bg-secondary px-2 py-1.5 font-mono text-xs break-all">
                      {metadata.fingerprint}
                    </code>
                  </div>
                </>
              )}
            </TabsContent>

            <TabsContent value="lyrics" className="mt-4">
              <LyricsPanel
                songId={songId}
                syncedLyrics={metadata.syncedLyrics}
                plainLyrics={metadata.plainLyrics}
                lyricsStatus={metadata.lyricsStatus}
                hasSyncedLyrics={metadata.hasSyncedLyrics}
                hasPlainLyrics={metadata.hasPlainLyrics}
                isInstrumental={metadata.isInstrumental}
              />
            </TabsContent>

            <TabsContent value="sources" className="mt-4 space-y-3">
              <p className="text-sm text-muted-foreground">
                Metadata enrichment sources
              </p>
              <div className="space-y-2">
                <SourceRow
                  name="MusicBrainz"
                  connected={metadata.sources.musicbrainz}
                  url="https://musicbrainz.org"
                />
                <SourceRow
                  name="Last.fm"
                  connected={metadata.sources.lastfm}
                  url="https://last.fm"
                />
                <SourceRow
                  name="Spotify"
                  connected={metadata.sources.spotify}
                  url="https://spotify.com"
                />
                <SourceRow
                  name="Genius"
                  connected={metadata.sources.genius}
                  url="https://genius.com"
                />
              </div>
              <Separator className="my-3" />
              <Button
                variant="outline"
                className={cn(
                  "w-full",
                  resetState === "success" && "border-primary/50 text-primary",
                  resetState === "error" && "border-destructive/50 text-destructive"
                )}
                size="sm"
                disabled={resetState === "loading" || songId === null}
                onClick={handleResetEnrichment}
              >
                {resetState === "loading" ? (
                  <>
                    <Loader2 className="mr-1.5 size-3.5 animate-spin" />
                    Resetting…
                  </>
                ) : resetState === "success" ? (
                  <>
                    <CheckCircle2 className="mr-1.5 size-3.5" />
                    Queued for Re-enrichment
                  </>
                ) : resetState === "error" ? (
                  <>
                    <AlertCircle className="mr-1.5 size-3.5" />
                    Reset Failed
                  </>
                ) : (
                  <>
                    <RotateCcw className="mr-1.5 size-3.5" />
                    Re-enrich Metadata
                  </>
                )}
              </Button>
              {resetError && (
                <p className="text-xs text-destructive mt-1.5">{resetError}</p>
              )}
            </TabsContent>
          </Tabs>
        </div>
      </ScrollArea>

      {/* File Path */}
      <div className="border-t border-border p-3">
        <p className="text-xs text-muted-foreground truncate">
          <span className="font-medium">Path:</span> {file.path}
        </p>
      </div>
    </div>
  )
}

function InfoRow({
  icon: Icon,
  label,
  value,
}: {
  icon: React.ComponentType<{ className?: string }>
  label: string
  value: string
}) {
  return (
    <div className="flex items-center justify-between">
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Icon className="size-4" />
        <span>{label}</span>
      </div>
      <span className="text-sm font-medium">{value}</span>
    </div>
  )
}

function SourceRow({
  name,
  connected,
  url,
}: {
  name: string
  connected?: boolean
  url: string
}) {
  return (
    <div className="flex items-center justify-between rounded-lg bg-secondary/50 px-3 py-2">
      <div className="flex items-center gap-2">
        {connected ? (
          <CheckCircle2 className="size-4 text-primary" />
        ) : (
          <div className="size-4 rounded-full border-2 border-muted-foreground/30" />
        )}
        <span className="text-sm">{name}</span>
      </div>
      <a
        href={url}
        target="_blank"
        rel="noopener noreferrer"
        className="text-muted-foreground transition-colors hover:text-foreground"
      >
        <ExternalLink className="size-3.5" />
      </a>
    </div>
  )
}

function StatusBadge({ status }: { status: string }) {
  switch (status) {
    case "complete":
      return (
        <Badge className="bg-primary/10 text-primary hover:bg-primary/20">
          <CheckCircle2 className="mr-1.5 size-3" />
          Enriched
        </Badge>
      )
    case "processing":
      return (
        <Badge className="bg-chart-2/10 text-chart-2 hover:bg-chart-2/20">
          <Loader2 className="mr-1.5 size-3 animate-spin" />
          Processing
        </Badge>
      )
    case "needsreview":
      return (
        <Badge className="bg-amber-500/10 text-amber-600 hover:bg-amber-500/20 dark:text-amber-500">
          <Eye className="mr-1.5 size-3" />
          Needs Review
        </Badge>
      )
    case "pending":
      return (
        <Badge variant="secondary">
          <Clock className="mr-1.5 size-3" />
          Pending
        </Badge>
      )
    case "failed":
      return (
        <Badge variant="destructive">
          <AlertCircle className="mr-1.5 size-3" />
          Failed
        </Badge>
      )
    default:
      return null
  }
}

function formatDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${mins}:${secs.toString().padStart(2, "0")}`
}

function formatFileSize(bytes: number): string {
  const mb = bytes / (1024 * 1024)
  return `${mb.toFixed(1)} MB`
}

interface LrcLine {
  timeMs: number
  text: string
}

function parseLrc(lrc: string): LrcLine[] {
  const lines: LrcLine[] = []
  for (const raw of lrc.split("\n")) {
    const match = raw.match(/^\[(\d{2}):(\d{2})\.(\d{2,3})\]\s*(.*)$/)
    if (!match) continue
    const mins = parseInt(match[1], 10)
    const secs = parseInt(match[2], 10)
    const cs = parseInt(match[3].padEnd(3, "0"), 10)
    const timeMs = mins * 60000 + secs * 1000 + cs
    lines.push({ timeMs, text: match[4] ?? "" })
  }
  return lines
}

function LyricsStatusBadge({ status }: { status?: LyricsStatus }) {
  switch (status) {
    case "Fetched":
      return (
        <Badge className="bg-primary/10 text-primary hover:bg-primary/20 text-xs">
          <CheckCircle2 className="mr-1 size-3" />
          Lyrics Found
        </Badge>
      )
    case "Instrumental":
      return (
        <Badge className="bg-blue-500/10 text-blue-600 hover:bg-blue-500/20 dark:text-blue-400 text-xs">
          <Music className="mr-1 size-3" />
          Instrumental
        </Badge>
      )
    case "NotFound":
      return (
        <Badge variant="secondary" className="text-xs">
          <FileText className="mr-1 size-3" />
          No Lyrics Found
        </Badge>
      )
    case "Failed":
      return (
        <Badge variant="destructive" className="text-xs">
          <AlertCircle className="mr-1 size-3" />
          Fetch Failed
        </Badge>
      )
    case "NotFetched":
    default:
      return (
        <Badge variant="outline" className="text-xs text-muted-foreground">
          <Clock className="mr-1 size-3" />
          Not Fetched
        </Badge>
      )
  }
}

function LyricsPanel({
  songId,
  syncedLyrics: syncedLyricsFromProps,
  plainLyrics: plainLyricsFromProps,
  lyricsStatus,
  hasSyncedLyrics: hasSyncedFromProps,
  hasPlainLyrics: hasPlainFromProps,
  isInstrumental,
}: {
  songId: number | null
  syncedLyrics?: string
  plainLyrics?: string
  lyricsStatus?: LyricsStatus
  hasSyncedLyrics?: boolean
  hasPlainLyrics?: boolean
  isInstrumental?: boolean
}) {
  const [showSynced, setShowSynced] = useState(true)
  const [loadedSynced, setLoadedSynced] = useState<string | null | undefined>(syncedLyricsFromProps)
  const [loadedPlain, setLoadedPlain] = useState<string | null | undefined>(plainLyricsFromProps)
  const [loadState, setLoadState] = useState<"idle" | "loading" | "error">("idle")

  // Flags: trust the API-provided booleans when content isn't preloaded in props
  const hasSynced = Boolean(loadedSynced) || Boolean(hasSyncedFromProps)
  const hasPlain = Boolean(loadedPlain) || Boolean(hasPlainFromProps)
  const hasAny = hasSynced || hasPlain

  // Content may not be preloaded (the /songs endpoint returns flags only, not full text).
  // Auto-fetch when we know lyrics exist but content isn't loaded yet.
  const needsLoad =
    hasAny &&
    !Boolean(loadedSynced) &&
    !Boolean(loadedPlain) &&
    loadState === "idle" &&
    songId !== null

  useEffect(() => {
    if (!needsLoad || songId === null) return
    setLoadState("loading")
    fetchTrackLyrics(songId)
      .then((data) => {
        setLoadedSynced(data.synced ?? undefined)
        setLoadedPlain(data.plain ?? undefined)
        setLoadState("idle")
      })
      .catch(() => {
        setLoadState("error")
      })
  }, [needsLoad, songId])

  const parsedLines = useMemo(
    () => (Boolean(loadedSynced) && showSynced ? parseLrc(loadedSynced!) : null),
    [loadedSynced, showSynced]
  )

  if (isInstrumental) {
    return (
      <div className="flex flex-col items-center justify-center py-8 text-center gap-2">
        <Music className="size-10 text-muted-foreground opacity-40" />
        <p className="text-sm text-muted-foreground">This track is instrumental — no lyrics expected.</p>
        <LyricsStatusBadge status="Instrumental" />
      </div>
    )
  }

  if (loadState === "loading") {
    return (
      <div className="flex flex-col items-center justify-center py-8 text-center gap-2">
        <Loader2 className="size-8 text-muted-foreground animate-spin" />
        <p className="text-sm text-muted-foreground">Loading lyrics…</p>
      </div>
    )
  }

  if (loadState === "error") {
    return (
      <div className="flex flex-col items-center justify-center py-8 text-center gap-2">
        <AlertCircle className="size-8 text-destructive opacity-70" />
        <p className="text-sm text-muted-foreground">Failed to load lyrics.</p>
        <Button variant="outline" size="sm" onClick={() => setLoadState("idle")}>Retry</Button>
      </div>
    )
  }

  if (!hasAny) {
    return (
      <div className="flex flex-col items-center justify-center py-8 text-center gap-2">
        <FileText className="size-10 text-muted-foreground opacity-50" />
        <p className="text-sm text-muted-foreground">
          {lyricsStatus === "NotFound"
            ? "No lyrics found in LRCLIB for this track."
            : lyricsStatus === "Failed"
              ? "Lyrics fetch encountered an error."
              : "Lyrics have not been fetched yet — they are enriched automatically after a successful metadata match."}
        </p>
        <LyricsStatusBadge status={lyricsStatus} />
      </div>
    )
  }

  const contentSynced = loadedSynced
  const contentPlain = loadedPlain
  const showSyncedToggle = Boolean(contentSynced) && Boolean(contentPlain)

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <LyricsStatusBadge status={lyricsStatus} />
        {showSyncedToggle && (
          <div className="flex rounded-md border border-border overflow-hidden text-xs">
            <button
              className={cn(
                "px-2 py-1 flex items-center gap-1 transition-colors",
                showSynced
                  ? "bg-primary text-primary-foreground"
                  : "bg-transparent text-muted-foreground hover:text-foreground"
              )}
              onClick={() => setShowSynced(true)}
            >
              <Timer className="size-3" />
              Synced
            </button>
            <button
              className={cn(
                "px-2 py-1 flex items-center gap-1 transition-colors",
                !showSynced
                  ? "bg-primary text-primary-foreground"
                  : "bg-transparent text-muted-foreground hover:text-foreground"
              )}
              onClick={() => setShowSynced(false)}
            >
              <AlignLeft className="size-3" />
              Plain
            </button>
          </div>
        )}
        {contentSynced && !contentPlain && (
          <Badge variant="outline" className="text-xs text-muted-foreground gap-1">
            <Timer className="size-3" />
            Synced LRC
          </Badge>
        )}
        {!contentSynced && contentPlain && (
          <Badge variant="outline" className="text-xs text-muted-foreground gap-1">
            <AlignLeft className="size-3" />
            Plain text
          </Badge>
        )}
      </div>

      <div className="rounded-lg bg-secondary/50 p-4 max-h-72 overflow-y-auto">
        {parsedLines ? (
          <div className="space-y-1 font-sans text-sm leading-relaxed">
            {parsedLines.map((line, i) => (
              <div key={i} className="flex gap-2">
                <span className="shrink-0 text-xs text-muted-foreground/60 font-mono w-12 pt-0.5">
                  {formatLrcTime(line.timeMs)}
                </span>
                <span className={cn("flex-1", !line.text && "opacity-0 select-none")}>
                  {line.text || "·"}
                </span>
              </div>
            ))}
          </div>
        ) : (
          <pre className="whitespace-pre-wrap font-sans text-sm leading-relaxed">
            {showSynced ? contentSynced : contentPlain}
          </pre>
        )}
      </div>
    </div>
  )
}

function formatLrcTime(ms: number): string {
  const totalSecs = Math.floor(ms / 1000)
  const mins = Math.floor(totalSecs / 60)
  const secs = totalSecs % 60
  return `${mins}:${secs.toString().padStart(2, "0")}`
}
