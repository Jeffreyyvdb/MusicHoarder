"use client"

import { useState } from "react"
import { AppHeader } from "@/components/app-header"
import { Button } from "@/components/ui/button"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Progress } from "@/components/ui/progress"
import { Input } from "@/components/ui/input"
import { Checkbox } from "@/components/ui/checkbox"
import { mockSpotifyPlaylists, mockSpotifyLikedSongs } from "@/lib/mock-data"
import type { SpotifyPlaylist, SpotifyTrack } from "@/lib/types"
import {
  Music,
  Search,
  RefreshCw,
  Download,
  Check,
  CheckCircle2,
  Circle,
  Loader2,
  Link2,
  Heart,
  ListMusic,
  Clock,
  AlertCircle,
} from "lucide-react"

function formatDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${mins}:${secs.toString().padStart(2, "0")}`
}

function PlaylistCard({
  playlist,
  onSync,
  isSyncing,
}: {
  playlist: SpotifyPlaylist
  onSync: (id: string) => void
  isSyncing: boolean
}) {
  const matchPercent = Math.round((playlist.tracksMatched / playlist.trackCount) * 100)

  const statusColors = {
    not_synced: "bg-secondary text-muted-foreground",
    syncing: "bg-chart-4/20 text-chart-4",
    synced: "bg-primary/20 text-primary",
    partial: "bg-chart-5/20 text-chart-5",
  }

  const statusLabels = {
    not_synced: "Not Synced",
    syncing: "Syncing...",
    synced: "Synced",
    partial: "Partial Match",
  }

  return (
    <div className="flex gap-4 rounded-xl border border-border bg-card p-4 transition-colors hover:border-border/80">
      {/* Playlist Image */}
      <div className="size-20 shrink-0 rounded-lg overflow-hidden bg-secondary sm:size-24">
        {playlist.image ? (
          <img
            src={playlist.image}
            alt={playlist.name}
            className="size-full object-cover"
            crossOrigin="anonymous"
          />
        ) : (
          <div className="flex size-full items-center justify-center">
            <ListMusic className="size-8 text-muted-foreground" />
          </div>
        )}
      </div>

      {/* Playlist Info */}
      <div className="flex-1 min-w-0">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <h3 className="font-semibold truncate">{playlist.name}</h3>
            <p className="text-sm text-muted-foreground truncate">
              {playlist.trackCount} tracks - by {playlist.owner}
            </p>
          </div>
          <Badge className={statusColors[playlist.syncStatus]}>
            {playlist.syncStatus === "syncing" && (
              <Loader2 className="size-3 mr-1 animate-spin" />
            )}
            {statusLabels[playlist.syncStatus]}
          </Badge>
        </div>

        {playlist.description && (
          <p className="text-xs text-muted-foreground mt-1 line-clamp-1">
            {playlist.description}
          </p>
        )}

        {/* Progress */}
        {playlist.syncStatus !== "not_synced" && (
          <div className="mt-3">
            <div className="flex items-center justify-between text-xs mb-1">
              <span className="text-muted-foreground">
                {playlist.tracksMatched} of {playlist.trackCount} matched in library
              </span>
              <span className={matchPercent === 100 ? "text-primary font-medium" : "text-muted-foreground"}>
                {matchPercent}%
              </span>
            </div>
            <Progress value={matchPercent} className="h-1.5" />
          </div>
        )}

        {/* Actions */}
        <div className="mt-3 flex items-center gap-2">
          <Button
            size="sm"
            variant={playlist.syncStatus === "not_synced" ? "default" : "outline"}
            className="h-8"
            onClick={() => onSync(playlist.id)}
            disabled={isSyncing}
          >
            {isSyncing ? (
              <Loader2 className="size-3.5 mr-1.5 animate-spin" />
            ) : playlist.syncStatus === "not_synced" ? (
              <Link2 className="size-3.5 mr-1.5" />
            ) : (
              <RefreshCw className="size-3.5 mr-1.5" />
            )}
            {playlist.syncStatus === "not_synced" ? "Sync" : "Re-sync"}
          </Button>
          {playlist.syncStatus !== "not_synced" && playlist.tracksMatched < playlist.trackCount && (
            <Button size="sm" variant="ghost" className="h-8 text-primary">
              <Download className="size-3.5 mr-1.5" />
              Get Missing
            </Button>
          )}
        </div>

        {playlist.lastSynced && (
          <p className="text-xs text-muted-foreground mt-2">
            Last synced: {playlist.lastSynced.toLocaleDateString()} at{" "}
            {playlist.lastSynced.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
          </p>
        )}
      </div>
    </div>
  )
}

function TrackRow({
  track,
  isSelected,
  onSelect,
  onDownload,
}: {
  track: SpotifyTrack
  isSelected: boolean
  onSelect: (id: string) => void
  onDownload: (id: string) => void
}) {
  return (
    <div className="flex items-center gap-3 rounded-lg px-3 py-2.5 hover:bg-secondary/50 transition-colors">
      {/* Checkbox for batch selection */}
      {!track.inLibrary && (
        <Checkbox
          checked={isSelected}
          onCheckedChange={() => onSelect(track.id)}
          className="shrink-0"
        />
      )}
      {track.inLibrary && (
        <div className="w-4 shrink-0">
          <CheckCircle2 className="size-4 text-primary" />
        </div>
      )}

      {/* Album Art */}
      <div className="size-10 shrink-0 rounded overflow-hidden bg-secondary">
        {track.albumArt ? (
          <img
            src={track.albumArt}
            alt={track.album}
            className="size-full object-cover"
            crossOrigin="anonymous"
          />
        ) : (
          <div className="flex size-full items-center justify-center">
            <Music className="size-4 text-muted-foreground" />
          </div>
        )}
      </div>

      {/* Track Info */}
      <div className="flex-1 min-w-0">
        <p className={`truncate text-sm ${track.inLibrary ? "font-medium" : ""}`}>
          {track.name}
        </p>
        <p className="text-xs text-muted-foreground truncate">
          {track.artist} - {track.album}
        </p>
      </div>

      {/* Duration */}
      <span className="text-xs text-muted-foreground shrink-0 hidden sm:block">
        {formatDuration(track.duration)}
      </span>

      {/* Status / Action */}
      <div className="w-24 shrink-0 flex justify-end">
        {track.inLibrary ? (
          <Badge variant="secondary" className="text-xs">
            <Check className="size-3 mr-1" />
            In Library
          </Badge>
        ) : (
          <Button
            size="sm"
            variant="ghost"
            className="h-7 px-2 text-primary hover:bg-primary/10 hover:text-primary"
            onClick={() => onDownload(track.id)}
          >
            <Download className="size-3 mr-1" />
            Get
          </Button>
        )}
      </div>
    </div>
  )
}

export default function SpotifyPage() {
  const [isConnected, setIsConnected] = useState(true)
  const [syncingPlaylist, setSyncingPlaylist] = useState<string | null>(null)
  const [searchQuery, setSearchQuery] = useState("")
  const [selectedTracks, setSelectedTracks] = useState<Set<string>>(new Set())
  const [showOnlyMissing, setShowOnlyMissing] = useState(false)

  const handleSyncPlaylist = (id: string) => {
    setSyncingPlaylist(id)
    setTimeout(() => setSyncingPlaylist(null), 3000)
  }

  const handleSelectTrack = (id: string) => {
    setSelectedTracks((prev) => {
      const next = new Set(prev)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }

  const handleDownloadTrack = (id: string) => {
    // Simulate download
    console.log("Downloading track:", id)
  }

  const handleDownloadSelected = () => {
    selectedTracks.forEach((id) => handleDownloadTrack(id))
    setSelectedTracks(new Set())
  }

  const filteredTracks = mockSpotifyLikedSongs.filter((track) => {
    const matchesSearch =
      track.name.toLowerCase().includes(searchQuery.toLowerCase()) ||
      track.artist.toLowerCase().includes(searchQuery.toLowerCase())
    const matchesFilter = showOnlyMissing ? !track.inLibrary : true
    return matchesSearch && matchesFilter
  })

  const missingTracks = filteredTracks.filter((t) => !t.inLibrary)
  const ownedTracks = mockSpotifyLikedSongs.filter((t) => t.inLibrary)

  if (!isConnected) {
    return (
      <div className="flex h-screen flex-col bg-background">
        <AppHeader />
        <div className="flex flex-1 items-center justify-center p-6">
          <div className="max-w-md text-center">
            <div className="mx-auto mb-4 flex size-16 items-center justify-center rounded-full bg-[#1DB954]/20">
              <Music className="size-8 text-[#1DB954]" />
            </div>
            <h2 className="text-xl font-semibold mb-2">Connect Spotify</h2>
            <p className="text-muted-foreground mb-6">
              Link your Spotify account to sync your playlists and liked songs with your local
              library.
            </p>
            <Button
              size="lg"
              className="bg-[#1DB954] hover:bg-[#1DB954]/90 text-white"
              onClick={() => setIsConnected(true)}
            >
              <Music className="size-5 mr-2" />
              Connect with Spotify
            </Button>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />

      <div className="flex flex-1 flex-col overflow-hidden">
        {/* Page Header */}
        <div className="border-b border-border bg-card/30 px-4 py-6 md:px-6">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <div className="flex items-center gap-2">
                <h1 className="text-2xl font-bold">Spotify Sync</h1>
                <Badge className="bg-[#1DB954]/20 text-[#1DB954]">Connected</Badge>
              </div>
              <p className="text-sm text-muted-foreground mt-1">
                Sync your Spotify playlists and liked songs
              </p>
            </div>
            <Button variant="outline" onClick={() => setIsConnected(false)}>
              Disconnect
            </Button>
          </div>
        </div>

        {/* Tabs */}
        <Tabs defaultValue="playlists" className="flex flex-1 flex-col overflow-hidden">
          <div className="border-b border-border px-4 md:px-6">
            <TabsList className="h-12 bg-transparent p-0">
              <TabsTrigger
                value="playlists"
                className="h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:border-l-transparent data-[state=active]:border-r-transparent data-[state=active]:border-t-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
              >
                <ListMusic className="size-4 mr-2" />
                Playlists
              </TabsTrigger>
              <TabsTrigger
                value="liked"
                className="h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:border-l-transparent data-[state=active]:border-r-transparent data-[state=active]:border-t-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
              >
                <Heart className="size-4 mr-2" />
                Liked Songs
              </TabsTrigger>
            </TabsList>
          </div>

          {/* Playlists Tab */}
          <TabsContent value="playlists" className="flex-1 overflow-hidden m-0">
            <ScrollArea className="h-full">
              <div className="p-4 md:p-6 space-y-4">
                {/* Summary Cards */}
                <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                  <div className="rounded-xl border border-border bg-card p-4">
                    <p className="text-2xl font-bold">{mockSpotifyPlaylists.length}</p>
                    <p className="text-xs text-muted-foreground">Playlists</p>
                  </div>
                  <div className="rounded-xl border border-border bg-card p-4">
                    <p className="text-2xl font-bold text-primary">
                      {mockSpotifyPlaylists.filter((p) => p.syncStatus === "synced").length}
                    </p>
                    <p className="text-xs text-muted-foreground">Fully Synced</p>
                  </div>
                  <div className="rounded-xl border border-border bg-card p-4">
                    <p className="text-2xl font-bold text-chart-5">
                      {mockSpotifyPlaylists.filter((p) => p.syncStatus === "partial").length}
                    </p>
                    <p className="text-xs text-muted-foreground">Partial</p>
                  </div>
                  <div className="rounded-xl border border-border bg-card p-4">
                    <p className="text-2xl font-bold text-muted-foreground">
                      {mockSpotifyPlaylists.filter((p) => p.syncStatus === "not_synced").length}
                    </p>
                    <p className="text-xs text-muted-foreground">Not Synced</p>
                  </div>
                </div>

                {/* Playlist List */}
                <div className="space-y-3">
                  {mockSpotifyPlaylists.map((playlist) => (
                    <PlaylistCard
                      key={playlist.id}
                      playlist={playlist}
                      onSync={handleSyncPlaylist}
                      isSyncing={syncingPlaylist === playlist.id}
                    />
                  ))}
                </div>
              </div>
            </ScrollArea>
          </TabsContent>

          {/* Liked Songs Tab */}
          <TabsContent value="liked" className="flex-1 overflow-hidden m-0 flex flex-col">
            {/* Toolbar */}
            <div className="flex flex-col gap-3 border-b border-border px-4 py-3 sm:flex-row sm:items-center md:px-6">
              <div className="relative flex-1 max-w-md">
                <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  placeholder="Search liked songs..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="pl-9 bg-secondary border-0"
                />
              </div>
              <div className="flex items-center gap-3">
                <label className="flex items-center gap-2 text-sm">
                  <Checkbox
                    checked={showOnlyMissing}
                    onCheckedChange={(checked) => setShowOnlyMissing(checked === true)}
                  />
                  <span className="text-muted-foreground">Show missing only</span>
                </label>
                {selectedTracks.size > 0 && (
                  <Button size="sm" onClick={handleDownloadSelected}>
                    <Download className="size-4 mr-1.5" />
                    Get Selected ({selectedTracks.size})
                  </Button>
                )}
              </div>
            </div>

            {/* Stats Bar */}
            <div className="flex items-center gap-4 border-b border-border bg-secondary/30 px-4 py-2 text-sm md:px-6">
              <span className="text-muted-foreground">
                {mockSpotifyLikedSongs.length} liked songs
              </span>
              <span className="flex items-center gap-1 text-primary">
                <CheckCircle2 className="size-3.5" />
                {ownedTracks.length} in library
              </span>
              <span className="flex items-center gap-1 text-muted-foreground">
                <Circle className="size-3.5" />
                {mockSpotifyLikedSongs.length - ownedTracks.length} missing
              </span>
            </div>

            {/* Track List */}
            <ScrollArea className="flex-1">
              <div className="p-2 md:px-4">
                {filteredTracks.map((track) => (
                  <TrackRow
                    key={track.id}
                    track={track}
                    isSelected={selectedTracks.has(track.id)}
                    onSelect={handleSelectTrack}
                    onDownload={handleDownloadTrack}
                  />
                ))}
                {filteredTracks.length === 0 && (
                  <div className="flex flex-col items-center justify-center py-12 text-center">
                    <AlertCircle className="size-10 text-muted-foreground mb-3" />
                    <p className="text-muted-foreground">No tracks found</p>
                  </div>
                )}
              </div>
            </ScrollArea>
          </TabsContent>
        </Tabs>
      </div>
    </div>
  )
}
