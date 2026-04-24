"use client"

import { useMemo } from "react"
import Link from "next/link"
import { ArrowLeft, Disc3, Pause, Play } from "lucide-react"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"
import { usePlayer } from "@/lib/player-context"
import { getSongStreamUrl, type ApiSong } from "@/lib/api-client"

interface AlbumDetailViewProps {
  songs: ApiSong[]
  albumKey: string
  isLoading: boolean
}

const UNKNOWN_ALBUM = "Unknown Album"
const UNKNOWN_ARTIST = "Unknown Artist"

// Matches the key used in AlbumGridView.groupByAlbum — both must stay in sync.
function albumKeyForSong(song: ApiSong): string {
  const title = (song.album ?? UNKNOWN_ALBUM).trim() || UNKNOWN_ALBUM
  const artist =
    (song.albumArtist ?? song.artist ?? UNKNOWN_ARTIST).trim() ||
    UNKNOWN_ARTIST
  return `${artist.toLowerCase()}::${title.toLowerCase()}`
}

function computeInitials(title: string): string {
  const letters = title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? "")
    .join("")
  return letters || title.slice(0, 2).toUpperCase()
}

function formatDuration(seconds: number | null | undefined): string {
  if (!seconds || seconds <= 0) return "—"
  const total = Math.floor(seconds)
  const hrs = Math.floor(total / 3600)
  const mins = Math.floor((total % 3600) / 60)
  const secs = total % 60
  if (hrs > 0) {
    return `${hrs}:${mins.toString().padStart(2, "0")}:${secs.toString().padStart(2, "0")}`
  }
  return `${mins}:${secs.toString().padStart(2, "0")}`
}

export function AlbumDetailView({
  songs,
  albumKey,
  isLoading,
}: AlbumDetailViewProps) {
  const { currentSong, isPlaying, playSong } = usePlayer()

  const albumSongs = useMemo(() => {
    return songs
      .filter((s) => albumKeyForSong(s) === albumKey)
      .sort((a, b) => {
        const na = a.trackNumber ?? Number.POSITIVE_INFINITY
        const nb = b.trackNumber ?? Number.POSITIVE_INFINITY
        if (na !== nb) return na - nb
        return (a.title ?? a.fileName).localeCompare(b.title ?? b.fileName)
      })
  }, [songs, albumKey])

  if (isLoading && albumSongs.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center p-8 text-sm text-muted-foreground">
        Loading album...
      </div>
    )
  }

  if (albumSongs.length === 0) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center text-muted-foreground">
        <Disc3 className="size-10 opacity-40" />
        <p className="text-sm">Album not found in your library.</p>
        <Link
          href="/app"
          className="text-sm text-primary underline-offset-4 hover:underline"
        >
          Back to all albums
        </Link>
      </div>
    )
  }

  const first = albumSongs[0]
  const title = (first.album ?? UNKNOWN_ALBUM).trim() || UNKNOWN_ALBUM
  const artist =
    (first.albumArtist ?? first.artist ?? UNKNOWN_ARTIST).trim() ||
    UNKNOWN_ARTIST
  const year = albumSongs.reduce<number | null>((acc, s) => {
    if (!s.year) return acc
    return acc == null ? s.year : Math.min(acc, s.year)
  }, null)
  const totalSeconds = albumSongs.reduce(
    (sum, s) => sum + (s.durationSeconds ?? 0),
    0
  )
  const initials = computeInitials(title)
  const coverUrl = albumSongs.find((s) => s.albumArt)?.albumArt ?? null

  const playFirst = () => {
    const target = albumSongs[0]
    playSong({
      id: target.id,
      title: (target.title ?? target.fileName).trim() || target.fileName,
      artist: (target.artist ?? artist).trim() || artist,
      streamUrl: getSongStreamUrl(target.id),
    })
  }

  return (
    <ScrollArea className="min-h-0 flex-1">
      <div className="p-4 md:p-6">
        <Link
          href="/app"
          className="mb-4 inline-flex items-center gap-1.5 text-sm text-muted-foreground transition-colors hover:text-foreground"
        >
          <ArrowLeft className="size-4" />
          All albums
        </Link>

        <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-end sm:gap-6">
          <div className="relative aspect-square w-40 shrink-0 overflow-hidden rounded-lg border border-border bg-gradient-to-br from-secondary to-muted shadow-sm sm:w-48">
            {coverUrl ? (
              <img
                key={coverUrl}
                src={coverUrl}
                alt=""
                className="size-full object-cover"
                onError={(e) => {
                  ;(e.currentTarget as HTMLImageElement).style.display = "none"
                }}
              />
            ) : (
              <div className="flex size-full items-center justify-center">
                <span className="text-4xl font-semibold tracking-wide text-muted-foreground/60">
                  {initials}
                </span>
              </div>
            )}
          </div>
          <div className="min-w-0 flex-1">
            <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
              Album
            </p>
            <h1 className="truncate text-3xl font-bold sm:text-4xl">
              {title}
            </h1>
            <p className="mt-1 truncate text-sm text-muted-foreground">
              {artist}
              {year ? ` · ${year}` : ""}
              {` · ${albumSongs.length} track${albumSongs.length === 1 ? "" : "s"}`}
              {totalSeconds > 0 ? ` · ${formatDuration(totalSeconds)}` : ""}
            </p>
            <div className="mt-4">
              <Button size="sm" onClick={playFirst}>
                <Play className="mr-1.5 size-3.5" />
                Play
              </Button>
            </div>
          </div>
        </div>

        <div className="overflow-hidden rounded-lg border border-border">
          <div className="grid grid-cols-[32px_minmax(0,1fr)_72px] items-center gap-3 border-b border-border bg-card/30 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground sm:grid-cols-[32px_minmax(0,1fr)_minmax(0,160px)_72px]">
            <span className="text-right">#</span>
            <span>Title</span>
            <span className="hidden sm:block">Artist</span>
            <span className="text-right">Duration</span>
          </div>
          {albumSongs.map((song, i) => {
            const isCurrentlyLoaded = currentSong?.id === song.id
            const isCurrentlyPlaying = isCurrentlyLoaded && isPlaying
            const trackNum = song.trackNumber ?? i + 1
            const trackTitle =
              (song.title ?? song.fileName).trim() || song.fileName
            const trackArtist =
              (song.artist ?? artist).trim() || artist
            return (
              <button
                key={song.id}
                type="button"
                onClick={() =>
                  playSong({
                    id: song.id,
                    title: trackTitle,
                    artist: trackArtist,
                    streamUrl: getSongStreamUrl(song.id),
                  })
                }
                className={cn(
                  "group grid w-full grid-cols-[32px_minmax(0,1fr)_72px] items-center gap-3 border-b border-border px-3 py-2.5 text-left transition-colors last:border-b-0 hover:bg-secondary/40 sm:grid-cols-[32px_minmax(0,1fr)_minmax(0,160px)_72px]",
                  isCurrentlyLoaded && "bg-primary/5"
                )}
                aria-label={
                  isCurrentlyPlaying
                    ? `Pause ${trackTitle}`
                    : `Play ${trackTitle}`
                }
              >
                <span className="relative flex h-6 items-center justify-end">
                  <span
                    className={cn(
                      "text-sm tabular-nums text-muted-foreground transition-opacity group-hover:opacity-0",
                      isCurrentlyPlaying && "opacity-0"
                    )}
                  >
                    {trackNum}
                  </span>
                  <span
                    className={cn(
                      "absolute inset-0 flex items-center justify-end text-primary opacity-0 transition-opacity group-hover:opacity-100",
                      isCurrentlyPlaying && "opacity-100"
                    )}
                  >
                    {isCurrentlyPlaying ? (
                      <Pause className="size-4" />
                    ) : (
                      <Play className="size-4" />
                    )}
                  </span>
                </span>
                <span className="min-w-0">
                  <span
                    className={cn(
                      "block truncate text-sm font-medium",
                      isCurrentlyLoaded && "text-primary"
                    )}
                  >
                    {trackTitle}
                  </span>
                  <span className="block truncate text-xs text-muted-foreground sm:hidden">
                    {trackArtist}
                  </span>
                </span>
                <span className="hidden truncate text-sm text-muted-foreground sm:block">
                  {trackArtist}
                </span>
                <span className="text-right text-xs tabular-nums text-muted-foreground">
                  {formatDuration(song.durationSeconds)}
                </span>
              </button>
            )
          })}
        </div>
      </div>
    </ScrollArea>
  )
}
