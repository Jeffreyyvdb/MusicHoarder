"use client"

import { useMemo } from "react"
import Link from "next/link"
import { Disc3, Music } from "lucide-react"
import { ScrollArea } from "@/components/ui/scroll-area"
import { cn } from "@/lib/utils"
import type { ApiSong } from "@/lib/api-client"

interface AlbumGridViewProps {
  songs: ApiSong[]
  isLoading: boolean
  searchQuery: string
}

interface AlbumSummary {
  key: string
  title: string
  artist: string
  year: number | null
  trackCount: number
  initials: string
  coverUrl: string | null
}

const UNKNOWN_ALBUM = "Unknown Album"
const UNKNOWN_ARTIST = "Unknown Artist"

function computeInitials(title: string): string {
  const letters = title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((word) => word[0]?.toUpperCase() ?? "")
    .join("")
  return letters || title.slice(0, 2).toUpperCase()
}

function groupByAlbum(songs: ApiSong[]): AlbumSummary[] {
  const map = new Map<string, AlbumSummary>()
  for (const song of songs) {
    const title = (song.album ?? UNKNOWN_ALBUM).trim() || UNKNOWN_ALBUM
    const artist =
      (song.albumArtist ?? song.artist ?? UNKNOWN_ARTIST).trim() ||
      UNKNOWN_ARTIST
    const key = `${artist.toLowerCase()}::${title.toLowerCase()}`
    const existing = map.get(key)
    if (existing) {
      existing.trackCount += 1
      if (song.year && (!existing.year || song.year < existing.year)) {
        existing.year = song.year
      }
    } else {
      map.set(key, {
        key,
        title,
        artist,
        year: song.year ?? null,
        trackCount: 1,
        initials: computeInitials(title),
        coverUrl: song.albumArt ?? null,
      })
    }
  }
  return Array.from(map.values()).sort((a, b) => {
    const artistCmp = a.artist.localeCompare(b.artist)
    if (artistCmp !== 0) return artistCmp
    return a.title.localeCompare(b.title)
  })
}

export function AlbumGridView({
  songs,
  isLoading,
  searchQuery,
}: AlbumGridViewProps) {
  const albums = useMemo(() => groupByAlbum(songs), [songs])

  const filtered = useMemo(() => {
    const q = searchQuery.trim().toLowerCase()
    if (!q) return albums
    return albums.filter(
      (a) =>
        a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q)
    )
  }, [albums, searchQuery])

  if (isLoading && albums.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center p-8 text-sm text-muted-foreground">
        Loading albums...
      </div>
    )
  }

  if (filtered.length === 0) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center text-muted-foreground">
        <Disc3 className="size-10 opacity-40" />
        <p className="text-sm">
          {searchQuery ? "No albums match your search." : "No albums yet."}
        </p>
      </div>
    )
  }

  return (
    <ScrollArea className="min-h-0 flex-1">
      <div className="p-4 md:p-6">
        <div className="mb-4 flex items-end justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold">All albums</h2>
            <p className="text-sm text-muted-foreground">
              {filtered.length} album{filtered.length === 1 ? "" : "s"}
              {searchQuery ? ` matching "${searchQuery}"` : ""}
            </p>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
          {filtered.map((album) => (
            <AlbumCard key={album.key} album={album} />
          ))}
        </div>
      </div>
    </ScrollArea>
  )
}

function AlbumCard({ album }: { album: AlbumSummary }) {
  const coverUrl = album.coverUrl
  return (
    <Link
      href={`/app?album=${encodeURIComponent(album.key)}`}
      className="group flex flex-col gap-2 rounded-lg outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
      aria-label={`Open album ${album.title} by ${album.artist}`}
    >
      <div className="relative aspect-square overflow-hidden rounded-lg border border-border bg-gradient-to-br from-secondary to-muted shadow-sm transition-all group-hover:border-primary/40 group-hover:shadow-md">
        {coverUrl ? (
          <img
            key={coverUrl}
            src={coverUrl}
            alt=""
            loading="lazy"
            className="size-full object-cover transition-transform group-hover:scale-[1.02]"
            onError={(e) => {
              // Hide broken images so the initials fallback underneath shows through.
              ;(e.currentTarget as HTMLImageElement).style.display = "none"
            }}
          />
        ) : null}
        <div
          className={cn(
            "pointer-events-none absolute inset-0 flex items-center justify-center",
            coverUrl && "opacity-0"
          )}
        >
          <span className="text-3xl font-semibold tracking-wide text-muted-foreground/60">
            {album.initials}
          </span>
        </div>
        <div className="absolute bottom-2 left-2 flex items-center gap-1 rounded-full bg-background/80 px-2 py-0.5 text-[10px] text-muted-foreground backdrop-blur-sm">
          <Music className="size-3" />
          {album.trackCount}
        </div>
      </div>
      <div className="min-w-0">
        <p className="truncate text-sm font-medium">{album.title}</p>
        <p className="truncate text-xs text-muted-foreground">
          {album.artist}
          {album.year ? ` · ${album.year}` : ""}
        </p>
      </div>
    </Link>
  )
}
