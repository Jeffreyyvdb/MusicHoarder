"use client"

import { useState, useCallback } from "react"
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
  Pause,
  Play,
} from "lucide-react"
import { Button } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Slider } from "@/components/ui/slider"
import type { FileItem } from "@/lib/types"
import { cn } from "@/lib/utils"
import { resetSongEnrichment, getSongStreamUrl, parseSongId } from "@/lib/api-client"
import { usePlayer } from "@/lib/player-context"

interface TrackDetailsProps {
  file: FileItem | null
  onClose: () => void
  onResetEnrichment?: () => void
}

function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || Number.isNaN(seconds) || seconds < 0) return "0:00"
  const m = Math.floor(seconds / 60)
  const s = Math.floor(seconds % 60)
  return `${m}:${s.toString().padStart(2, "0")}`
}

export function TrackDetails({ file, onClose, onResetEnrichment }: TrackDetailsProps) {
  const [resetState, setResetState] = useState<"idle" | "loading" | "success" | "error">("idle")
  const [resetError, setResetError] = useState<string | null>(null)
  const { currentSong, isPlaying, currentTime, duration, playSong, togglePlay, seek } = usePlayer()

  const songId = file ? parseSongId(file.id) : null
  const isThisSong = currentSong?.id === songId && songId !== null

  const handlePlay = useCallback(() => {
    if (songId === null || !file?.metadata) return
    playSong({
      id: songId,
      title: file.metadata.title,
      artist: file.metadata.artist,
      streamUrl: getSongStreamUrl(songId),
    })
  }, [songId, file, playSong])

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
  const playerCurrentTime = isThisSong ? currentTime : 0
  const playerDuration = isThisSong ? duration : 0
  const playerIsPlaying = isThisSong && isPlaying

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

          {/* Inline Player */}
          {songId !== null && (
            <div className="mb-4 rounded-lg border border-border bg-secondary/30 p-3">
              <div className="flex items-center justify-center">
                <Button
                  variant="ghost"
                  size="icon"
                  className={cn(
                    "size-10 rounded-full transition-all",
                    playerIsPlaying
                      ? "bg-primary text-primary-foreground hover:bg-primary/90"
                      : "hover:bg-primary/10 hover:text-primary"
                  )}
                  onClick={isThisSong ? togglePlay : handlePlay}
                  aria-label={playerIsPlaying ? "Pause" : "Play"}
                >
                  {playerIsPlaying ? (
                    <Pause className="size-4" />
                  ) : (
                    <Play className="size-4 translate-x-px" />
                  )}
                </Button>
              </div>
              <div className="mt-2 flex items-center gap-2">
                <span className="w-9 shrink-0 text-right text-xs tabular-nums text-muted-foreground">
                  {formatTime(playerCurrentTime)}
                </span>
                <Slider
                  value={[playerCurrentTime]}
                  max={playerDuration > 0 ? playerDuration : 1}
                  min={0}
                  step={1}
                  disabled={!isThisSong || playerDuration === 0}
                  className={cn("flex-1", !isThisSong && "opacity-40")}
                  onValueChange={([val]) => {
                    if (isThisSong && val !== undefined) seek(val)
                  }}
                  aria-label="Seek"
                />
                <span className="w-9 shrink-0 text-xs tabular-nums text-muted-foreground">
                  {formatTime(playerDuration)}
                </span>
              </div>
            </div>
          )}

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
              {metadata.lyrics ? (
                <div className="rounded-lg bg-secondary/50 p-4">
                  <pre className="whitespace-pre-wrap font-sans text-sm leading-relaxed">
                    {metadata.lyrics}
                  </pre>
                </div>
              ) : (
                <div className="flex flex-col items-center justify-center py-8 text-center">
                  <FileText className="size-10 text-muted-foreground opacity-50" />
                  <p className="mt-2 text-sm text-muted-foreground">No lyrics available</p>
                  <Button variant="outline" size="sm" className="mt-3">
                    Fetch Lyrics
                  </Button>
                </div>
              )}
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
