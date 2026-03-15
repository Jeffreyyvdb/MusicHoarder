"use client"

import { useState, use } from "react"
import Link from "next/link"
import { AppHeader } from "@/components/app-header"
import { Button } from "@/components/ui/button"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Progress } from "@/components/ui/progress"
import { mockArtists, mockBeatlesDiscography } from "@/lib/mock-data"
import type { DiscographyAlbum, DiscographyTrack } from "@/lib/types"
import {
  ArrowLeft,
  Music,
  Disc,
  Check,
  Download,
  Loader2,
  ChevronDown,
  ChevronUp,
  CheckCircle2,
  Circle,
  Clock,
} from "lucide-react"

function formatDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${mins}:${secs.toString().padStart(2, "0")}`
}

function TrackRow({
  track,
  onDownload,
}: {
  track: DiscographyTrack
  onDownload: (trackId: string) => void
}) {
  return (
    <div className="flex items-center gap-3 rounded-lg px-3 py-2 hover:bg-secondary/50 transition-colors">
      {/* Status Icon */}
      <div className="w-6 shrink-0 text-center">
        {track.inLibrary ? (
          <CheckCircle2 className="size-4 text-primary mx-auto" />
        ) : (
          <span className="text-sm text-muted-foreground">{track.trackNumber}</span>
        )}
      </div>

      {/* Track Name */}
      <div className="flex-1 min-w-0">
        <p className={`truncate text-sm ${track.inLibrary ? "font-medium" : "text-muted-foreground"}`}>
          {track.name}
        </p>
      </div>

      {/* Duration */}
      <span className="text-xs text-muted-foreground shrink-0">
        {formatDuration(track.duration)}
      </span>

      {/* Download Button */}
      <div className="w-20 shrink-0 flex justify-end">
        {track.inLibrary ? (
          <Badge variant="secondary" className="text-xs">
            <Check className="size-3 mr-1" />
            Owned
          </Badge>
        ) : track.downloadStatus === "downloading" ? (
          <Button size="sm" variant="ghost" disabled className="h-7 px-2">
            <Loader2 className="size-3 animate-spin" />
          </Button>
        ) : track.downloadStatus === "available" ? (
          <Button
            size="sm"
            variant="ghost"
            className="h-7 px-2 text-primary hover:bg-primary/10 hover:text-primary"
            onClick={() => onDownload(track.id)}
          >
            <Download className="size-3 mr-1" />
            Get
          </Button>
        ) : (
          <span className="text-xs text-muted-foreground">Unavailable</span>
        )}
      </div>
    </div>
  )
}

function AlbumCard({
  album,
  onDownloadTrack,
  onDownloadAlbum,
}: {
  album: DiscographyAlbum
  onDownloadTrack: (trackId: string) => void
  onDownloadAlbum: (albumId: string) => void
}) {
  const [expanded, setExpanded] = useState(album.inLibrary)
  const completionPercent = Math.round((album.tracksOwned / album.totalTracks) * 100)
  const missingTracks = album.tracks.filter((t) => !t.inLibrary)

  return (
    <div className="rounded-xl border border-border bg-card overflow-hidden">
      {/* Album Header */}
      <div className="flex gap-4 p-4">
        {/* Album Art */}
        <div className="size-24 shrink-0 rounded-lg overflow-hidden bg-secondary sm:size-32">
          {album.albumArt ? (
            <img
              src={album.albumArt}
              alt={album.name}
              className="size-full object-cover"
              crossOrigin="anonymous"
            />
          ) : (
            <div className="flex size-full items-center justify-center">
              <Disc className="size-10 text-muted-foreground" />
            </div>
          )}
        </div>

        {/* Album Info */}
        <div className="flex-1 min-w-0">
          <div className="flex items-start justify-between gap-2">
            <div>
              <h3 className="font-semibold truncate">{album.name}</h3>
              <p className="text-sm text-muted-foreground">{album.year}</p>
              <Badge variant="outline" className="mt-1 text-xs capitalize">
                {album.type}
              </Badge>
            </div>
          </div>

          {/* Progress */}
          <div className="mt-3">
            <div className="flex items-center justify-between text-xs mb-1">
              <span className="text-muted-foreground">
                {album.tracksOwned} of {album.totalTracks} tracks
              </span>
              <span className={completionPercent === 100 ? "text-primary font-medium" : "text-muted-foreground"}>
                {completionPercent}%
              </span>
            </div>
            <Progress value={completionPercent} className="h-1.5" />
          </div>

          {/* Actions */}
          <div className="mt-3 flex flex-wrap items-center gap-2">
            {completionPercent < 100 && (
              <Button
                size="sm"
                variant="default"
                className="h-8"
                onClick={() => onDownloadAlbum(album.id)}
              >
                <Download className="size-3.5 mr-1.5" />
                Get Missing ({missingTracks.length})
              </Button>
            )}
            <Button
              size="sm"
              variant="ghost"
              className="h-8"
              onClick={() => setExpanded(!expanded)}
            >
              {expanded ? (
                <>
                  <ChevronUp className="size-3.5 mr-1.5" />
                  Hide Tracks
                </>
              ) : (
                <>
                  <ChevronDown className="size-3.5 mr-1.5" />
                  Show Tracks
                </>
              )}
            </Button>
          </div>
        </div>
      </div>

      {/* Track List */}
      {expanded && (
        <div className="border-t border-border bg-secondary/20 px-2 py-2">
          {album.tracks.map((track) => (
            <TrackRow key={track.id} track={track} onDownload={onDownloadTrack} />
          ))}
        </div>
      )}
    </div>
  )
}

export default function ArtistDiscographyPage({
  params,
}: {
  params: Promise<{ id: string }>
}) {
  const { id } = use(params)
  const [downloadingTracks, setDownloadingTracks] = useState<Set<string>>(new Set())

  // Find artist
  const artist = mockArtists.find((a) => a.id === id)

  // For demo, use Beatles discography for any artist
  const discography = mockBeatlesDiscography

  const handleDownloadTrack = (trackId: string) => {
    setDownloadingTracks((prev) => new Set(prev).add(trackId))
    // Simulate download
    setTimeout(() => {
      setDownloadingTracks((prev) => {
        const next = new Set(prev)
        next.delete(trackId)
        return next
      })
    }, 2000)
  }

  const handleDownloadAlbum = (albumId: string) => {
    const album = discography.find((a) => a.id === albumId)
    if (!album) return
    const missingTracks = album.tracks.filter((t) => !t.inLibrary)
    missingTracks.forEach((t) => handleDownloadTrack(t.id))
  }

  if (!artist) {
    return (
      <div className="flex h-screen flex-col bg-background">
        <AppHeader />
        <div className="flex flex-1 items-center justify-center">
          <p className="text-muted-foreground">Artist not found</p>
        </div>
      </div>
    )
  }

  const totalTracks = discography.reduce((sum, a) => sum + a.totalTracks, 0)
  const ownedTracks = discography.reduce((sum, a) => sum + a.tracksOwned, 0)
  const completionPercent = Math.round((ownedTracks / totalTracks) * 100)

  const albumsWithAll = discography.filter((a) => a.tracksOwned === a.totalTracks).length
  const albumsPartial = discography.filter((a) => a.tracksOwned > 0 && a.tracksOwned < a.totalTracks).length
  const albumsMissing = discography.filter((a) => a.tracksOwned === 0).length

  return (
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />

      <ScrollArea className="flex-1">
        <div className="p-4 md:p-6">
          {/* Back Button */}
          <Link
            href="/artists"
            className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground mb-4"
          >
            <ArrowLeft className="size-4" />
            Back to Artists
          </Link>

          {/* Artist Header */}
          <div className="flex flex-col gap-6 sm:flex-row sm:items-end mb-8">
            {/* Artist Image */}
            <div className="size-32 shrink-0 rounded-xl overflow-hidden bg-secondary sm:size-48">
              {artist.image ? (
                <img
                  src={artist.image}
                  alt={artist.name}
                  className="size-full object-cover"
                  crossOrigin="anonymous"
                />
              ) : (
                <div className="flex size-full items-center justify-center">
                  <Music className="size-16 text-muted-foreground" />
                </div>
              )}
            </div>

            {/* Artist Info */}
            <div className="flex-1">
              <p className="text-sm text-muted-foreground mb-1">Artist</p>
              <h1 className="text-3xl font-bold sm:text-4xl">{artist.name}</h1>
              <p className="text-muted-foreground mt-1">
                {artist.genres.join(", ")}
              </p>

              {/* Overall Stats */}
              <div className="mt-4 flex flex-wrap gap-4">
                <div className="rounded-lg bg-secondary/50 px-4 py-2">
                  <p className="text-2xl font-bold text-primary">{completionPercent}%</p>
                  <p className="text-xs text-muted-foreground">Library Complete</p>
                </div>
                <div className="rounded-lg bg-secondary/50 px-4 py-2">
                  <p className="text-2xl font-bold">{ownedTracks}</p>
                  <p className="text-xs text-muted-foreground">of {totalTracks} tracks</p>
                </div>
                <div className="rounded-lg bg-secondary/50 px-4 py-2">
                  <p className="text-2xl font-bold">{discography.length}</p>
                  <p className="text-xs text-muted-foreground">Albums</p>
                </div>
              </div>
            </div>
          </div>

          {/* Tabs */}
          <Tabs defaultValue="all">
            <TabsList className="mb-4">
              <TabsTrigger value="all" className="gap-1.5">
                <Disc className="size-3.5" />
                All ({discography.length})
              </TabsTrigger>
              <TabsTrigger value="complete" className="gap-1.5">
                <CheckCircle2 className="size-3.5" />
                Complete ({albumsWithAll})
              </TabsTrigger>
              <TabsTrigger value="partial" className="gap-1.5">
                <Clock className="size-3.5" />
                Partial ({albumsPartial})
              </TabsTrigger>
              <TabsTrigger value="missing" className="gap-1.5">
                <Circle className="size-3.5" />
                Missing ({albumsMissing})
              </TabsTrigger>
            </TabsList>

            <TabsContent value="all" className="space-y-4">
              {discography.map((album) => (
                <AlbumCard
                  key={album.id}
                  album={album}
                  onDownloadTrack={handleDownloadTrack}
                  onDownloadAlbum={handleDownloadAlbum}
                />
              ))}
            </TabsContent>

            <TabsContent value="complete" className="space-y-4">
              {discography
                .filter((a) => a.tracksOwned === a.totalTracks)
                .map((album) => (
                  <AlbumCard
                    key={album.id}
                    album={album}
                    onDownloadTrack={handleDownloadTrack}
                    onDownloadAlbum={handleDownloadAlbum}
                  />
                ))}
              {albumsWithAll === 0 && (
                <div className="rounded-xl border border-dashed border-border p-8 text-center">
                  <p className="text-muted-foreground">No complete albums yet</p>
                </div>
              )}
            </TabsContent>

            <TabsContent value="partial" className="space-y-4">
              {discography
                .filter((a) => a.tracksOwned > 0 && a.tracksOwned < a.totalTracks)
                .map((album) => (
                  <AlbumCard
                    key={album.id}
                    album={album}
                    onDownloadTrack={handleDownloadTrack}
                    onDownloadAlbum={handleDownloadAlbum}
                  />
                ))}
              {albumsPartial === 0 && (
                <div className="rounded-xl border border-dashed border-border p-8 text-center">
                  <p className="text-muted-foreground">No partial albums</p>
                </div>
              )}
            </TabsContent>

            <TabsContent value="missing" className="space-y-4">
              {discography
                .filter((a) => a.tracksOwned === 0)
                .map((album) => (
                  <AlbumCard
                    key={album.id}
                    album={album}
                    onDownloadTrack={handleDownloadTrack}
                    onDownloadAlbum={handleDownloadAlbum}
                  />
                ))}
              {albumsMissing === 0 && (
                <div className="rounded-xl border border-dashed border-border p-8 text-center">
                  <p className="text-muted-foreground">You have all albums!</p>
                </div>
              )}
            </TabsContent>
          </Tabs>
        </div>
      </ScrollArea>
    </div>
  )
}
