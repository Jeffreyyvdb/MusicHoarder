"use client"

import { useState, useEffect, useCallback, Suspense } from "react"
import { useRouter, useSearchParams } from "next/navigation"
import { Button } from "@/components/ui/button"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
import {
  fetchSpotifyStatus,
  fetchSpotifyConnectUrl,
  disconnectSpotify,
  fetchSpotifyLikedSongs,
  fetchSpotifyPlaylists,
  fetchSpotifyPlaylistTracks,
  fetchSpotifyCredentials,
} from "@/lib/api-client"
import { isDemoMode } from "@/lib/app-mode"
import type {
  SpotifyStatusResponse,
  SpotifyApiTrack,
  SpotifyApiPlaylist,
  SpotifyCredentialsResponse,
  SpotifyLibraryMatchStatus,
} from "@/lib/api-client"
import {
  Music,
  Search,
  Heart,
  ListMusic,
  Clock,
  AlertCircle,
  CheckCircle2,
  LogOut,
  ExternalLink,
  ChevronLeft,
  ChevronRight,
  Loader2,
  Settings,
  ArrowLeft,
  KeyRound,
  Link2,
  ChevronDown,
  ChevronUp,
  Columns2,
  Download,
} from "lucide-react"
import Link from "next/link"

function formatDuration(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000)
  const mins = Math.floor(totalSeconds / 60)
  const secs = totalSeconds % 60
  return `${mins}:${secs.toString().padStart(2, "0")}`
}

function formatDateAdded(dateStr: string): string {
  const date = new Date(dateStr)
  return date.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" })
}

function formatMatchConfidence(confidence: number | null | undefined): string {
  if (confidence == null || !Number.isFinite(confidence)) return ""
  const pct = confidence <= 1 ? Math.round(confidence * 100) : Math.round(confidence)
  return `${pct}%`
}

function SpotifyTrackRowWithLibraryMatch({
  track,
  index,
  showDateAdded,
  expanded,
  onToggleExpand,
}: {
  track: SpotifyApiTrack
  index: number
  showDateAdded: boolean
  expanded: boolean
  onToggleExpand: () => void
}) {
  const m = track.libraryMatch
  const status = m?.matchStatus as SpotifyLibraryMatchStatus | undefined
  const songId = m?.matchedSongId
  const isPossible = status === "PossibleMatch"
  const hasMatchInfo = Boolean(m && status)

  return (
    <div className="rounded-lg">
      <div
        role={isPossible ? "button" : undefined}
        tabIndex={isPossible ? 0 : undefined}
        onClick={() => {
          if (isPossible) onToggleExpand()
        }}
        onKeyDown={(e) => {
          if (!isPossible) return
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault()
            onToggleExpand()
          }
        }}
        className={`flex items-center gap-3 px-3 py-2.5 transition-colors group ${
          isPossible ? "cursor-pointer hover:bg-secondary/50" : "hover:bg-secondary/50"
        }`}
      >
        <span className="w-8 text-right text-xs text-muted-foreground tabular-nums shrink-0">
          {index + 1}
        </span>

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

        <div className="flex-1 min-w-0">
          <p className="truncate text-sm font-medium">{track.title}</p>
          <p className="text-xs text-muted-foreground truncate">{track.artist}</p>
        </div>

        <span className="text-sm text-muted-foreground truncate hidden md:block max-w-[200px]">
          {track.album}
        </span>

        {showDateAdded && (
          <span className="text-xs text-muted-foreground shrink-0 hidden lg:block w-24 text-right">
            {formatDateAdded(track.addedAt)}
          </span>
        )}

        <span className="text-xs text-muted-foreground shrink-0 w-12 text-right">
          {formatDuration(track.durationMs)}
        </span>

        <div className="shrink-0 flex items-center gap-1 justify-end min-w-[120px] max-w-[168px]">
          {!hasMatchInfo && (
            <span className="text-[10px] text-muted-foreground whitespace-nowrap hidden sm:inline" title="Match pending">
              —
            </span>
          )}
          {status === "InLibrary" && songId != null && (
            <Button
              asChild
              size="sm"
              variant="outline"
              className="h-8 border-primary/40 bg-primary/15 px-2.5 text-xs font-medium text-primary hover:bg-primary/25"
            >
              <Link href={`/app?song=${songId}`} onClick={(e) => e.stopPropagation()}>
                <CheckCircle2 className="size-3.5 shrink-0" />
                In library
              </Link>
            </Button>
          )}
          {status === "PossibleMatch" && songId != null && (
            <div className="flex items-center gap-0.5">
              <Button
                asChild
                size="sm"
                variant="outline"
                className="h-8 border-amber-500/45 bg-amber-500/15 px-2.5 text-xs font-medium text-amber-900 hover:bg-amber-500/25 dark:text-amber-200"
                title="Open best-guess local track"
              >
                <Link href={`/app?song=${songId}`} onClick={(e) => e.stopPropagation()}>
                  ~{formatMatchConfidence(m?.matchConfidence)}
                  <Link2 className="size-3.5 shrink-0" />
                </Link>
              </Button>
              {expanded ? (
                <ChevronUp className="size-3.5 text-muted-foreground shrink-0" />
              ) : (
                <ChevronDown className="size-3.5 text-muted-foreground shrink-0" />
              )}
            </div>
          )}
          {status === "NotInLibrary" && (
            <Tooltip>
              <TooltipTrigger asChild>
                <span className="inline-flex">
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    disabled
                    className="h-8 border-rose-500/35 bg-rose-500/10 px-2.5 text-xs font-medium text-rose-900 dark:text-rose-200"
                  >
                    <Download className="size-3.5 shrink-0" />
                    Download
                  </Button>
                </span>
              </TooltipTrigger>
              <TooltipContent side="left" className="max-w-[220px]">
                <span className="font-medium">Coming soon</span>
                <span className="mt-1 block text-[11px] leading-snug opacity-90">
                  Track acquisition will be wired here later.
                </span>
              </TooltipContent>
            </Tooltip>
          )}
        </div>
      </div>

      {isPossible && expanded && songId != null && (
        <div className="border-t border-border/80 bg-secondary/20 px-3 py-3 md:px-5">
          <p className="text-xs font-medium text-muted-foreground mb-2 flex items-center gap-1.5">
            <Columns2 className="size-3.5" />
            Spotify vs local ({formatMatchConfidence(m?.matchConfidence)})
          </p>
          <div className="grid gap-3 md:grid-cols-2">
            <div className="rounded-lg border border-border bg-card/50 p-2.5 space-y-1">
              <p className="text-[10px] uppercase tracking-wide text-muted-foreground">Spotify</p>
              <p className="text-sm font-medium">{track.title}</p>
              <p className="text-xs text-muted-foreground">{track.artist}</p>
              <p className="text-xs text-muted-foreground">{track.album}</p>
            </div>
            <div className="rounded-lg border border-border bg-card/50 p-2.5 space-y-1">
              <div className="flex items-center justify-between gap-2">
                <p className="text-[10px] uppercase tracking-wide text-muted-foreground">MusicHoarder</p>
                <Link
                  href={`/app?song=${songId}`}
                  className="text-xs text-primary inline-flex items-center gap-1 hover:underline"
                >
                  Open
                  <Link2 className="size-3" />
                </Link>
              </div>
              <p className="text-sm font-medium">{m?.matchedTitle ?? "—"}</p>
              <p className="text-xs text-muted-foreground">{m?.matchedArtist ?? "—"}</p>
              {m?.matchedEnrichmentStatus && (
                <p className="text-xs text-muted-foreground">Enrichment: {m.matchedEnrichmentStatus}</p>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function PlaylistCard({
  playlist,
  onClick,
}: {
  playlist: SpotifyApiPlaylist
  onClick: () => void
}) {
  return (
    <button
      onClick={onClick}
      className="flex flex-col items-center gap-3 rounded-xl border border-border bg-card p-4 transition-colors hover:border-primary/30 hover:bg-card/80 text-left w-full"
    >
      <div className="w-full aspect-square rounded-lg overflow-hidden bg-secondary">
        {playlist.imageUrl ? (
          <img
            src={playlist.imageUrl}
            alt={playlist.name}
            className="size-full object-cover"
            crossOrigin="anonymous"
          />
        ) : (
          <div className="flex size-full items-center justify-center">
            <ListMusic className="size-10 text-muted-foreground" />
          </div>
        )}
      </div>

      <div className="w-full min-w-0">
        <h3 className="font-semibold truncate text-sm">{playlist.name}</h3>
        <p className="text-xs text-muted-foreground mt-0.5">
          {playlist.trackCount} tracks{playlist.ownerName ? ` \u00b7 ${playlist.ownerName}` : ""}
        </p>
      </div>
    </button>
  )
}

function PaginationControls({
  offset,
  limit,
  total,
  onPageChange,
  isLoading,
}: {
  offset: number
  limit: number
  total: number
  onPageChange: (newOffset: number) => void
  isLoading: boolean
}) {
  const currentPage = Math.floor(offset / limit) + 1
  const totalPages = Math.ceil(total / limit)

  if (totalPages <= 1) return null

  return (
    <div className="flex items-center justify-center gap-2 py-4">
      <Button
        variant="outline"
        size="sm"
        disabled={offset === 0 || isLoading}
        onClick={() => onPageChange(Math.max(0, offset - limit))}
      >
        <ChevronLeft className="size-4" />
      </Button>
      <span className="text-sm text-muted-foreground px-2">
        Page {currentPage} of {totalPages}
      </span>
      <Button
        variant="outline"
        size="sm"
        disabled={offset + limit >= total || isLoading}
        onClick={() => onPageChange(offset + limit)}
      >
        <ChevronRight className="size-4" />
      </Button>
    </div>
  )
}

function TrackListSkeleton() {
  return (
    <div className="space-y-2 p-4">
      {Array.from({ length: 10 }).map((_, i) => (
        <div key={i} className="flex items-center gap-3 px-3 py-2.5">
          <Skeleton className="w-8 h-4" />
          <Skeleton className="size-10 rounded" />
          <div className="flex-1 space-y-1.5">
            <Skeleton className="h-4 w-48" />
            <Skeleton className="h-3 w-32" />
          </div>
          <Skeleton className="h-3 w-12" />
        </div>
      ))}
    </div>
  )
}

function PlaylistGridSkeleton() {
  return (
    <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-4 p-4 md:p-6">
      {Array.from({ length: 10 }).map((_, i) => (
        <div key={i} className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4">
          <Skeleton className="w-full aspect-square rounded-lg" />
          <div className="space-y-1.5">
            <Skeleton className="h-4 w-3/4" />
            <Skeleton className="h-3 w-1/2" />
          </div>
        </div>
      ))}
    </div>
  )
}

function PlaylistDetailView({
  playlist,
  onBack,
}: {
  playlist: SpotifyApiPlaylist
  onBack: () => void
}) {
  const [tracks, setTracks] = useState<SpotifyApiTrack[]>([])
  const [total, setTotal] = useState(0)
  const [offset, setOffset] = useState(0)
  const [isLoading, setIsLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [searchQuery, setSearchQuery] = useState("")
  const [expandedMatchIds, setExpandedMatchIds] = useState<Set<string>>(() => new Set())
  const limit = 50

  const loadTracks = useCallback(async (newOffset: number) => {
    setIsLoading(true)
    setError(null)
    try {
      const result = await fetchSpotifyPlaylistTracks(playlist.spotifyId, newOffset, limit)
      setTracks(result.items)
      setTotal(result.total)
      setOffset(result.offset)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load tracks")
    } finally {
      setIsLoading(false)
    }
  }, [playlist.spotifyId])

  useEffect(() => {
    loadTracks(0)
  }, [loadTracks])

  const filteredTracks = searchQuery
    ? tracks.filter(
        (t) =>
          t.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
          t.artist.toLowerCase().includes(searchQuery.toLowerCase()) ||
          t.album.toLowerCase().includes(searchQuery.toLowerCase())
      )
    : tracks

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div className="flex items-center gap-4 border-b border-border px-4 py-4 md:px-6">
        <Button variant="ghost" size="sm" onClick={onBack} className="shrink-0">
          <ArrowLeft className="size-4 mr-1.5" />
          Back
        </Button>

        <div className="flex items-center gap-3 min-w-0">
          <div className="size-12 shrink-0 rounded-lg overflow-hidden bg-secondary">
            {playlist.imageUrl ? (
              <img
                src={playlist.imageUrl}
                alt={playlist.name}
                className="size-full object-cover"
                crossOrigin="anonymous"
              />
            ) : (
              <div className="flex size-full items-center justify-center">
                <ListMusic className="size-5 text-muted-foreground" />
              </div>
            )}
          </div>
          <div className="min-w-0">
            <h2 className="font-semibold truncate">{playlist.name}</h2>
            <p className="text-xs text-muted-foreground">
              {playlist.trackCount} tracks{playlist.ownerName ? ` \u00b7 ${playlist.ownerName}` : ""}
            </p>
          </div>
        </div>
      </div>

      <div className="flex items-center gap-3 border-b border-border px-4 py-3 md:px-6">
        <div className="relative flex-1 max-w-md">
          <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            placeholder="Filter tracks..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="pl-9 bg-secondary border-0"
          />
        </div>
        <span className="text-sm text-muted-foreground shrink-0">
          {total} tracks
        </span>
      </div>

      {error ? (
        <div className="flex flex-col items-center justify-center py-12 text-center">
          <AlertCircle className="size-10 text-destructive mb-3" />
          <p className="text-muted-foreground">{error}</p>
          <Button variant="outline" size="sm" className="mt-4" onClick={() => loadTracks(offset)}>
            Retry
          </Button>
        </div>
      ) : isLoading ? (
        <TrackListSkeleton />
      ) : (
        <ScrollArea className="min-h-0 flex-1">
          <div className="hidden md:flex items-center gap-3 px-6 py-2 text-xs text-muted-foreground border-b border-border/50">
            <span className="w-8 text-right">#</span>
            <span className="size-10" />
            <span className="flex-1">Title</span>
            <span className="hidden md:block max-w-[200px]">Album</span>
            <span className="w-12 text-right">
              <Clock className="size-3.5 inline" />
            </span>
            <span className="w-[120px] text-right shrink-0">Library</span>
          </div>
          <div className="flex flex-col gap-2 p-2 md:px-4">
            {filteredTracks.map((track, i) => (
              <SpotifyTrackRowWithLibraryMatch
                key={`${track.spotifyId}-${i}`}
                track={track}
                index={offset + i}
                showDateAdded={false}
                expanded={expandedMatchIds.has(track.spotifyId)}
                onToggleExpand={() => {
                  setExpandedMatchIds((prev) => {
                    const next = new Set(prev)
                    if (next.has(track.spotifyId)) next.delete(track.spotifyId)
                    else next.add(track.spotifyId)
                    return next
                  })
                }}
              />
            ))}
            {filteredTracks.length === 0 && (
              <div className="flex flex-col items-center justify-center py-12 text-center">
                <AlertCircle className="size-10 text-muted-foreground mb-3" />
                <p className="text-muted-foreground">No tracks found</p>
              </div>
            )}
          </div>
          <PaginationControls
            offset={offset}
            limit={limit}
            total={total}
            onPageChange={(o) => loadTracks(o)}
            isLoading={isLoading}
          />
        </ScrollArea>
      )}
    </div>
  )
}

function SpotifyPageContent() {
  const router = useRouter()
  const searchParams = useSearchParams()
  const [status, setStatus] = useState<SpotifyStatusResponse | null>(null)
  const [credentials, setCredentials] = useState<SpotifyCredentialsResponse | null>(null)
  const [isLoadingStatus, setIsLoadingStatus] = useState(true)
  const [isConnecting, setIsConnecting] = useState(false)
  const [isDisconnecting, setIsDisconnecting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [oauthBanner, setOauthBanner] = useState<{ type: "success" | "error"; message: string } | null>(null)

  const [likedSongs, setLikedSongs] = useState<SpotifyApiTrack[]>([])
  const [likedTotal, setLikedTotal] = useState(0)
  const [likedOffset, setLikedOffset] = useState(0)
  const [isLoadingLiked, setIsLoadingLiked] = useState(false)
  const [likedError, setLikedError] = useState<string | null>(null)
  const [likedSearchQuery, setLikedSearchQuery] = useState("")

  const [playlists, setPlaylists] = useState<SpotifyApiPlaylist[]>([])
  const [isLoadingPlaylists, setIsLoadingPlaylists] = useState(false)
  const [playlistsError, setPlaylistsError] = useState<string | null>(null)
  const [selectedPlaylist, setSelectedPlaylist] = useState<SpotifyApiPlaylist | null>(null)
  const [playlistSearchQuery, setPlaylistSearchQuery] = useState("")

  const [expandedLikedMatchIds, setExpandedLikedMatchIds] = useState<Set<string>>(() => new Set())

  const likedLimit = 50

  const loadStatus = useCallback(async () => {
    setIsLoadingStatus(true)
    setError(null)
    try {
      const [statusResult, credsResult] = await Promise.all([
        fetchSpotifyStatus().catch(() => ({ connected: false, hasCredentials: false, tokenExpired: false }) as SpotifyStatusResponse),
        fetchSpotifyCredentials().catch(() => ({ clientId: null, hasClientSecret: false }) as SpotifyCredentialsResponse),
      ])
      setStatus(statusResult)
      setCredentials(credsResult)
    } catch {
      setStatus({ connected: false, hasCredentials: false, tokenExpired: false })
      setCredentials({ clientId: null, hasClientSecret: false })
    } finally {
      setIsLoadingStatus(false)
    }
  }, [])

  useEffect(() => {
    loadStatus()
  }, [loadStatus])

  useEffect(() => {
    const connected = searchParams.get("spotify_connected")
    const oauthErr = searchParams.get("spotify_error")
    if (connected !== "1" && oauthErr == null) return

    if (connected === "1") {
      setOauthBanner({ type: "success", message: "Spotify connected successfully." })
    } else if (oauthErr != null) {
      setOauthBanner({ type: "error", message: oauthErr })
    }

    router.replace("/spotify", { scroll: false })
    void loadStatus()
  }, [searchParams, router, loadStatus])

  const loadLikedSongs = useCallback(async (offset: number) => {
    setIsLoadingLiked(true)
    setLikedError(null)
    try {
      const result = await fetchSpotifyLikedSongs(offset, likedLimit)
      setLikedSongs(result.items)
      setLikedTotal(result.total)
      setLikedOffset(result.offset)
    } catch (err) {
      setLikedError(err instanceof Error ? err.message : "Failed to load liked songs")
    } finally {
      setIsLoadingLiked(false)
    }
  }, [])

  const loadPlaylists = useCallback(async () => {
    setIsLoadingPlaylists(true)
    setPlaylistsError(null)
    try {
      const result = await fetchSpotifyPlaylists()
      setPlaylists(result.items)
    } catch (err) {
      setPlaylistsError(err instanceof Error ? err.message : "Failed to load playlists")
    } finally {
      setIsLoadingPlaylists(false)
    }
  }, [])

  useEffect(() => {
    if (status?.connected) {
      loadLikedSongs(0)
      loadPlaylists()
    }
  }, [status?.connected, loadLikedSongs, loadPlaylists])

  const handleConnect = async () => {
    setIsConnecting(true)
    setError(null)
    try {
      const result = await fetchSpotifyConnectUrl()
      window.location.href = result.authorizationUrl
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to start Spotify connection")
      setIsConnecting(false)
    }
  }

  const handleDisconnect = async () => {
    setIsDisconnecting(true)
    try {
      await disconnectSpotify()
      setStatus((prev) => prev ? { ...prev, connected: false, connectedAt: null, tokenExpired: false } : null)
      setLikedSongs([])
      setPlaylists([])
      setSelectedPlaylist(null)
      setExpandedLikedMatchIds(new Set())
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to disconnect")
    } finally {
      setIsDisconnecting(false)
    }
  }

  const filteredLikedSongs = likedSearchQuery
    ? likedSongs.filter(
        (t) =>
          t.title.toLowerCase().includes(likedSearchQuery.toLowerCase()) ||
          t.artist.toLowerCase().includes(likedSearchQuery.toLowerCase()) ||
          t.album.toLowerCase().includes(likedSearchQuery.toLowerCase())
      )
    : likedSongs

  const filteredPlaylists = playlistSearchQuery
    ? playlists.filter((p) =>
        p.name.toLowerCase().includes(playlistSearchQuery.toLowerCase())
      )
    : playlists

  if (isLoadingStatus) {
    return (
      <div className="flex flex-1 items-center justify-center">
        <Loader2 className="size-8 animate-spin text-muted-foreground" />
      </div>
    )
  }

  if (!status?.connected) {
    const hasCredentials = credentials?.hasClientSecret && credentials?.clientId

    return (
      <div className="flex flex-1 items-center justify-center p-6">
        <div className="max-w-md text-center">
            <div className="mx-auto mb-6 flex size-20 items-center justify-center rounded-full bg-[#1DB954]/10">
              <Music className="size-10 text-[#1DB954]" />
            </div>
            <h1 className="text-2xl font-bold mb-2">Connect Spotify</h1>
            <p className="text-muted-foreground mb-8">
              Link your Spotify account to browse your playlists and liked songs.
            </p>

            {!isDemoMode && hasCredentials && process.env.NODE_ENV === "development" && (
              <p className="text-muted-foreground mb-6 max-w-lg mx-auto text-left text-xs leading-relaxed">
                Spotify does not allow <code className="rounded bg-muted px-1 py-0.5">localhost</code> in redirect
                URIs—use loopback IP. Register{" "}
                <code className="rounded bg-muted px-1 py-0.5">http://127.0.0.1:5142/api/spotify/callback</code> in
                your Spotify app. If Spotify sends you to 5142 but the API uses another port, change the host/port in
                the address bar (keep the path and query string) then press Enter.
              </p>
            )}

            {oauthBanner && (
              <div
                className={`mb-6 rounded-lg border px-4 py-3 text-sm text-left ${
                  oauthBanner.type === "success"
                    ? "border-primary/40 bg-primary/10 text-primary"
                    : "border-destructive/50 bg-destructive/10 text-destructive"
                }`}
              >
                <div className="flex items-start gap-2">
                  {oauthBanner.type === "success" ? (
                    <CheckCircle2 className="size-4 shrink-0 mt-0.5" />
                  ) : (
                    <AlertCircle className="size-4 shrink-0 mt-0.5" />
                  )}
                  <p className="flex-1">{oauthBanner.message}</p>
                  <button
                    type="button"
                    onClick={() => setOauthBanner(null)}
                    className="text-xs underline opacity-80 hover:opacity-100 shrink-0"
                  >
                    Dismiss
                  </button>
                </div>
              </div>
            )}

            {error && (
              <div className="mb-6 rounded-lg border border-destructive/50 bg-destructive/10 px-4 py-3 text-sm text-destructive">
                {error}
              </div>
            )}

            {!hasCredentials ? (
              <div className="space-y-4">
                <div className="rounded-lg border border-border bg-card p-4 text-left">
                  <div className="flex items-center gap-2 mb-2">
                    <KeyRound className="size-4 text-muted-foreground" />
                    <span className="text-sm font-medium">Spotify API credentials required</span>
                  </div>
                  <p className="text-xs text-muted-foreground mb-3">
                    You need to configure your Spotify Client ID and Client Secret before connecting.
                  </p>
                  <Link href="/settings">
                    <Button variant="outline" size="sm" className="w-full">
                      <Settings className="size-4 mr-2" />
                      Go to Settings
                    </Button>
                  </Link>
                </div>
              </div>
            ) : (
              <Button
                size="lg"
                className="bg-[#1DB954] hover:bg-[#1DB954]/90 text-white px-8"
                onClick={handleConnect}
                disabled={isConnecting}
              >
                {isConnecting ? (
                  <Loader2 className="size-5 mr-2 animate-spin" />
                ) : (
                  <ExternalLink className="size-5 mr-2" />
                )}
                Connect with Spotify
              </Button>
            )}
        </div>
      </div>
    )
  }

  if (selectedPlaylist) {
    return (
        <PlaylistDetailView
          playlist={selectedPlaylist}
          onBack={() => setSelectedPlaylist(null)}
        />
    )
  }

  return (
      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
        {isDemoMode && (
          <div className="mx-4 mt-4 md:mx-6 rounded-md border border-border bg-card px-3 py-2 text-sm text-muted-foreground">
            <p>
              Demo mode: Spotify data is sample content only. Deploy the MusicHoarder API and disable demo mode to
              connect a real account.
            </p>
          </div>
        )}
        {oauthBanner && (
          <div
            className={`mx-4 mt-4 md:mx-6 rounded-lg border px-4 py-3 text-sm ${
              oauthBanner.type === "success"
                ? "border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400"
                : "border-destructive/50 bg-destructive/10 text-destructive"
            }`}
          >
            <div className="flex items-start gap-2">
              {oauthBanner.type === "success" ? (
                <CheckCircle2 className="size-4 shrink-0 mt-0.5" />
              ) : (
                <AlertCircle className="size-4 shrink-0 mt-0.5" />
              )}
              <p className="flex-1">{oauthBanner.message}</p>
              <button
                type="button"
                onClick={() => setOauthBanner(null)}
                className="text-xs underline opacity-80 hover:opacity-100 shrink-0"
              >
                Dismiss
              </button>
            </div>
          </div>
        )}
        <div className="border-b border-border bg-card/30 px-4 py-5 md:px-6">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <div className="flex items-center gap-2">
                <h1 className="text-2xl font-bold">Spotify</h1>
                <Badge className="bg-[#1DB954]/20 text-[#1DB954] border-0">Connected</Badge>
              </div>
              {status.connectedAt && (
                <p className="text-sm text-muted-foreground mt-1">
                  {isDemoMode
                    ? "Demo session (not a real Spotify connection)"
                    : `Connected since ${new Date(status.connectedAt).toLocaleDateString()}`}
                </p>
              )}
            </div>
            {!isDemoMode && (
              <Button
                variant="outline"
                onClick={handleDisconnect}
                disabled={isDisconnecting}
              >
                {isDisconnecting ? (
                  <Loader2 className="size-4 mr-2 animate-spin" />
                ) : (
                  <LogOut className="size-4 mr-2" />
                )}
                Disconnect
              </Button>
            )}
          </div>
        </div>

        <Tabs defaultValue="liked" className="flex min-h-0 flex-1 flex-col overflow-hidden">
          <div className="border-b border-border px-4 md:px-6">
            <TabsList className="h-12 bg-transparent p-0">
              <TabsTrigger
                value="liked"
                className="h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:bg-transparent data-[state=active]:shadow-none"
              >
                <Heart className="size-4 mr-2" />
                Liked Songs
              </TabsTrigger>
              <TabsTrigger
                value="playlists"
                className="h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:bg-transparent data-[state=active]:shadow-none"
              >
                <ListMusic className="size-4 mr-2" />
                Playlists
              </TabsTrigger>
            </TabsList>
          </div>

          {/* Liked Songs Tab */}
          <TabsContent value="liked" className="m-0 flex min-h-0 flex-1 flex-col overflow-hidden">
            <div className="flex items-center gap-3 border-b border-border px-4 py-3 md:px-6">
              <div className="relative flex-1 max-w-md">
                <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  placeholder="Search liked songs..."
                  value={likedSearchQuery}
                  onChange={(e) => setLikedSearchQuery(e.target.value)}
                  className="pl-9 bg-secondary border-0"
                />
              </div>
              <span className="text-sm text-muted-foreground shrink-0">
                {likedTotal} songs
              </span>
            </div>

            {likedError ? (
              <div className="flex flex-col items-center justify-center py-12 text-center">
                <AlertCircle className="size-10 text-destructive mb-3" />
                <p className="text-muted-foreground">{likedError}</p>
                <Button
                  variant="outline"
                  size="sm"
                  className="mt-4"
                  onClick={() => loadLikedSongs(likedOffset)}
                >
                  Retry
                </Button>
              </div>
            ) : isLoadingLiked ? (
              <TrackListSkeleton />
            ) : (
              <ScrollArea className="min-h-0 flex-1">
                <div className="hidden md:flex items-center gap-3 px-6 py-2 text-xs text-muted-foreground border-b border-border/50">
                  <span className="w-8 text-right">#</span>
                  <span className="size-10" />
                  <span className="flex-1">Title</span>
                  <span className="hidden md:block max-w-[200px]">Album</span>
                  <span className="hidden lg:block w-24 text-right">Date Added</span>
                  <span className="w-12 text-right">
                    <Clock className="size-3.5 inline" />
                  </span>
                  <span className="w-[120px] text-right shrink-0">Library</span>
                </div>
                <div className="flex flex-col gap-2 p-2 md:px-4">
                  {filteredLikedSongs.map((track, i) => (
                    <SpotifyTrackRowWithLibraryMatch
                      key={`${track.spotifyId}-${i}`}
                      track={track}
                      index={likedOffset + i}
                      showDateAdded
                      expanded={expandedLikedMatchIds.has(track.spotifyId)}
                      onToggleExpand={() => {
                        setExpandedLikedMatchIds((prev) => {
                          const next = new Set(prev)
                          if (next.has(track.spotifyId)) next.delete(track.spotifyId)
                          else next.add(track.spotifyId)
                          return next
                        })
                      }}
                    />
                  ))}
                  {filteredLikedSongs.length === 0 && !isLoadingLiked && (
                    <div className="flex flex-col items-center justify-center py-12 text-center">
                      <Heart className="size-10 text-muted-foreground mb-3" />
                      <p className="text-muted-foreground">
                        {likedSearchQuery ? "No matching songs found" : "No liked songs yet"}
                      </p>
                    </div>
                  )}
                </div>
                <PaginationControls
                  offset={likedOffset}
                  limit={likedLimit}
                  total={likedTotal}
                  onPageChange={(o) => loadLikedSongs(o)}
                  isLoading={isLoadingLiked}
                />
              </ScrollArea>
            )}
          </TabsContent>

          {/* Playlists Tab */}
          <TabsContent value="playlists" className="m-0 flex min-h-0 flex-1 flex-col overflow-hidden">
            <div className="flex items-center gap-3 border-b border-border px-4 py-3 md:px-6">
              <div className="relative flex-1 max-w-md">
                <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  placeholder="Search playlists..."
                  value={playlistSearchQuery}
                  onChange={(e) => setPlaylistSearchQuery(e.target.value)}
                  className="pl-9 bg-secondary border-0"
                />
              </div>
              <span className="text-sm text-muted-foreground shrink-0">
                {playlists.length} playlists
              </span>
            </div>

            {playlistsError ? (
              <div className="flex flex-col items-center justify-center py-12 text-center">
                <AlertCircle className="size-10 text-destructive mb-3" />
                <p className="text-muted-foreground">{playlistsError}</p>
                <Button variant="outline" size="sm" className="mt-4" onClick={loadPlaylists}>
                  Retry
                </Button>
              </div>
            ) : isLoadingPlaylists ? (
              <PlaylistGridSkeleton />
            ) : (
              <ScrollArea className="min-h-0 flex-1">
                <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-4 p-4 md:p-6">
                  {filteredPlaylists.map((playlist) => (
                    <PlaylistCard
                      key={playlist.spotifyId}
                      playlist={playlist}
                      onClick={() => setSelectedPlaylist(playlist)}
                    />
                  ))}
                </div>
                {filteredPlaylists.length === 0 && !isLoadingPlaylists && (
                  <div className="flex flex-col items-center justify-center py-12 text-center">
                    <ListMusic className="size-10 text-muted-foreground mb-3" />
                    <p className="text-muted-foreground">
                      {playlistSearchQuery ? "No matching playlists" : "No playlists found"}
                    </p>
                  </div>
                )}
              </ScrollArea>
            )}
          </TabsContent>
        </Tabs>
      </div>
  )
}

function SpotifyPageFallback() {
  return (
    <div className="flex flex-1 items-center justify-center">
      <Loader2 className="size-8 animate-spin text-muted-foreground" />
    </div>
  )
}

export default function SpotifyPage() {
  return (
    <Suspense fallback={<SpotifyPageFallback />}>
      <SpotifyPageContent />
    </Suspense>
  )
}
