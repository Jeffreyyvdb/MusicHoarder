"use client"

import { useState, useEffect, useCallback, useRef, type RefObject } from "react"
import { AppHeader } from "@/components/app-header"
import { Button } from "@/components/ui/button"
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
import type {
  SpotifyStatusResponse,
  SpotifyApiTrack,
  SpotifyApiPlaylist,
  SpotifyCredentialsResponse,
} from "@/lib/api-client"
import {
  Music,
  Search,
  Heart,
  ListMusic,
  Clock,
  AlertCircle,
  LogOut,
  ExternalLink,
  Loader2,
  Settings,
  ArrowLeft,
  KeyRound,
} from "lucide-react"
import Link from "next/link"

const TRACKS_PAGE_SIZE = 50

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

function TrackRow({ track, index }: { track: SpotifyApiTrack; index: number }) {
  return (
    <div className="flex items-center gap-3 rounded-lg px-3 py-2.5 hover:bg-secondary/50 transition-colors group">
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

      <span className="text-xs text-muted-foreground shrink-0 hidden lg:block w-24 text-right">
        {formatDateAdded(track.addedAt)}
      </span>

      <span className="text-xs text-muted-foreground shrink-0 w-12 text-right">
        {formatDuration(track.durationMs)}
      </span>
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
      type="button"
      onClick={onClick}
      className="flex w-full flex-col items-center gap-3 rounded-xl border border-border bg-card p-4 text-left transition-colors hover:border-primary/30 hover:bg-card/80"
    >
      <div className="aspect-square w-full overflow-hidden rounded-lg bg-secondary">
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
        <h3 className="truncate text-sm font-semibold">{playlist.name}</h3>
        <p className="mt-0.5 text-xs text-muted-foreground">
          {playlist.trackCount} tracks{playlist.ownerName ? ` \u00b7 ${playlist.ownerName}` : ""}
        </p>
      </div>
    </button>
  )
}

function LoadMoreSentinel({
  scrollRootRef,
  hasMore,
  isLoading,
  onLoadMore,
  error,
  onRetry,
}: {
  scrollRootRef: RefObject<HTMLDivElement | null>
  hasMore: boolean
  isLoading: boolean
  onLoadMore: () => void
  error: string | null
  onRetry: () => void
}) {
  const sentinelRef = useRef<HTMLDivElement>(null)
  const onLoadMoreRef = useRef(onLoadMore)
  onLoadMoreRef.current = onLoadMore

  useEffect(() => {
    const root = scrollRootRef.current
    const el = sentinelRef.current
    if (!root || !el || !hasMore) return

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && !isLoading) {
          onLoadMoreRef.current()
        }
      },
      { root, rootMargin: "200px", threshold: 0.01 },
    )
    observer.observe(el)
    return () => observer.disconnect()
  }, [scrollRootRef, hasMore, isLoading])

  if (!hasMore && !error) {
    return null
  }

  return (
    <div
      ref={sentinelRef}
      className="flex min-h-14 flex-col items-center justify-center gap-2 border-t border-border/40 py-4"
    >
      {isLoading && (
        <Loader2 className="size-6 animate-spin text-muted-foreground" aria-label="Loading more" />
      )}
      {error && !isLoading && (
        <>
          <p className="px-4 text-center text-sm text-destructive">{error}</p>
          <Button type="button" variant="outline" size="sm" onClick={onRetry}>
            Retry
          </Button>
        </>
      )}
    </div>
  )
}

function trackListHeaderClass() {
  return "sticky top-0 z-10 hidden items-center gap-3 border-b border-border/50 bg-background/95 px-3 py-2 text-xs text-muted-foreground backdrop-blur-md supports-[backdrop-filter]:bg-background/80 md:flex md:px-4"
}

function TrackListSkeleton() {
  return (
    <div className="space-y-2 p-4">
      {Array.from({ length: 10 }).map((_, i) => (
        <div key={i} className="flex items-center gap-3 px-3 py-2.5">
          <Skeleton className="h-4 w-8" />
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
    <div className="grid grid-cols-2 gap-4 p-4 sm:grid-cols-3 md:grid-cols-4 md:p-6 lg:grid-cols-5">
      {Array.from({ length: 10 }).map((_, i) => (
        <div key={i} className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4">
          <Skeleton className="aspect-square w-full rounded-lg" />
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
  const [isInitialLoading, setIsInitialLoading] = useState(true)
  const [isLoadingMore, setIsLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loadMoreError, setLoadMoreError] = useState<string | null>(null)
  const [searchQuery, setSearchQuery] = useState("")
  const viewportRef = useRef<HTMLDivElement>(null)
  const loadingMoreGuardRef = useRef(false)
  const tracksLenRef = useRef(0)
  const totalRef = useRef(0)
  tracksLenRef.current = tracks.length
  totalRef.current = total

  const loadInitial = useCallback(async () => {
    setIsInitialLoading(true)
    setError(null)
    setLoadMoreError(null)
    setTracks([])
    try {
      const result = await fetchSpotifyPlaylistTracks(playlist.spotifyId, 0, TRACKS_PAGE_SIZE)
      setTracks(result.items)
      setTotal(result.total)
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load tracks")
    } finally {
      setIsInitialLoading(false)
    }
  }, [playlist.spotifyId])

  useEffect(() => {
    loadInitial()
  }, [loadInitial])

  const loadMore = useCallback(async () => {
    if (loadingMoreGuardRef.current || isInitialLoading) return
    const offset = tracksLenRef.current
    if (offset >= totalRef.current) return
    loadingMoreGuardRef.current = true
    setIsLoadingMore(true)
    setLoadMoreError(null)
    try {
      const result = await fetchSpotifyPlaylistTracks(playlist.spotifyId, offset, TRACKS_PAGE_SIZE)
      setTracks((prev) => [...prev, ...result.items])
      setTotal(result.total)
    } catch (err) {
      setLoadMoreError(err instanceof Error ? err.message : "Failed to load more tracks")
    } finally {
      loadingMoreGuardRef.current = false
      setIsLoadingMore(false)
    }
  }, [playlist.spotifyId, isInitialLoading])

  const filteredTracks = searchQuery
    ? tracks.filter(
        (t) =>
          t.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
          t.artist.toLowerCase().includes(searchQuery.toLowerCase()) ||
          t.album.toLowerCase().includes(searchQuery.toLowerCase()),
      )
    : tracks

  const hasMore = tracks.length < total

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div className="flex shrink-0 items-center gap-4 border-b border-border px-4 py-4 md:px-6">
        <Button variant="ghost" size="sm" onClick={onBack} className="shrink-0">
          <ArrowLeft className="mr-1.5 size-4" />
          Back
        </Button>

        <div className="flex min-w-0 items-center gap-3">
          <div className="size-12 shrink-0 overflow-hidden rounded-lg bg-secondary">
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
            <h2 className="truncate font-semibold">{playlist.name}</h2>
            <p className="text-xs text-muted-foreground">
              {playlist.trackCount} tracks{playlist.ownerName ? ` \u00b7 ${playlist.ownerName}` : ""}
            </p>
          </div>
        </div>
      </div>

      <div className="flex shrink-0 items-center gap-3 border-b border-border px-4 py-3 md:px-6">
        <div className="relative max-w-md flex-1">
          <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input
            placeholder="Filter tracks..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="border-0 bg-secondary pl-9"
          />
        </div>
        <span className="shrink-0 text-sm text-muted-foreground">{total} tracks</span>
      </div>

      {error ? (
        <div className="flex flex-col items-center justify-center py-12 text-center">
          <AlertCircle className="mb-3 size-10 text-destructive" />
          <p className="text-muted-foreground">{error}</p>
          <Button variant="outline" size="sm" className="mt-4" onClick={() => loadInitial()}>
            Retry
          </Button>
        </div>
      ) : isInitialLoading ? (
        <TrackListSkeleton />
      ) : (
        <ScrollArea className="min-h-0 flex-1" viewportRef={viewportRef}>
          <div className={trackListHeaderClass()}>
            <span className="w-8 text-right">#</span>
            <span className="size-10 shrink-0" />
            <span className="min-w-0 flex-1">Title</span>
            <span className="hidden max-w-[200px] md:block">Album</span>
            <span className="hidden w-24 text-right lg:block">Date Added</span>
            <span className="w-12 shrink-0 text-right">
              <Clock className="inline size-3.5" />
            </span>
          </div>
          <div className="px-2 pb-2 md:px-4">
            {filteredTracks.map((track, i) => (
              <TrackRow key={`${track.spotifyId}-${i}`} track={track} index={i} />
            ))}
            {filteredTracks.length === 0 && (
              <div className="flex flex-col items-center justify-center py-12 text-center">
                <AlertCircle className="mb-3 size-10 text-muted-foreground" />
                <p className="text-muted-foreground">No tracks found</p>
              </div>
            )}
          </div>
          <LoadMoreSentinel
            scrollRootRef={viewportRef}
            hasMore={hasMore}
            isLoading={isLoadingMore}
            onLoadMore={loadMore}
            error={loadMoreError}
            onRetry={loadMore}
          />
        </ScrollArea>
      )}
    </div>
  )
}

export default function SpotifyPage() {
  const [status, setStatus] = useState<SpotifyStatusResponse | null>(null)
  const [credentials, setCredentials] = useState<SpotifyCredentialsResponse | null>(null)
  const [isLoadingStatus, setIsLoadingStatus] = useState(true)
  const [isConnecting, setIsConnecting] = useState(false)
  const [isDisconnecting, setIsDisconnecting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [likedSongs, setLikedSongs] = useState<SpotifyApiTrack[]>([])
  const [likedTotal, setLikedTotal] = useState(0)
  const [isLoadingLikedInitial, setIsLoadingLikedInitial] = useState(false)
  const [isLoadingLikedMore, setIsLoadingLikedMore] = useState(false)
  const [likedError, setLikedError] = useState<string | null>(null)
  const [likedLoadMoreError, setLikedLoadMoreError] = useState<string | null>(null)
  const [likedSearchQuery, setLikedSearchQuery] = useState("")
  const likedViewportRef = useRef<HTMLDivElement>(null)
  const likedLoadingMoreGuardRef = useRef(false)
  const likedLenRef = useRef(0)
  const likedTotalRef = useRef(0)

  const [playlists, setPlaylists] = useState<SpotifyApiPlaylist[]>([])
  const [isLoadingPlaylists, setIsLoadingPlaylists] = useState(false)
  const [playlistsError, setPlaylistsError] = useState<string | null>(null)
  const [selectedPlaylist, setSelectedPlaylist] = useState<SpotifyApiPlaylist | null>(null)
  const [playlistSearchQuery, setPlaylistSearchQuery] = useState("")

  likedLenRef.current = likedSongs.length
  likedTotalRef.current = likedTotal

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

  const loadLikedInitial = useCallback(async () => {
    setIsLoadingLikedInitial(true)
    setLikedError(null)
    setLikedLoadMoreError(null)
    setLikedSongs([])
    try {
      const result = await fetchSpotifyLikedSongs(0, TRACKS_PAGE_SIZE)
      setLikedSongs(result.items)
      setLikedTotal(result.total)
    } catch (err) {
      setLikedError(err instanceof Error ? err.message : "Failed to load liked songs")
    } finally {
      setIsLoadingLikedInitial(false)
    }
  }, [])

  const loadMoreLiked = useCallback(async () => {
    if (likedLoadingMoreGuardRef.current || isLoadingLikedInitial) return
    const offset = likedLenRef.current
    if (offset >= likedTotalRef.current) return
    likedLoadingMoreGuardRef.current = true
    setIsLoadingLikedMore(true)
    setLikedLoadMoreError(null)
    try {
      const result = await fetchSpotifyLikedSongs(offset, TRACKS_PAGE_SIZE)
      setLikedSongs((prev) => [...prev, ...result.items])
      setLikedTotal(result.total)
    } catch (err) {
      setLikedLoadMoreError(err instanceof Error ? err.message : "Failed to load more songs")
    } finally {
      likedLoadingMoreGuardRef.current = false
      setIsLoadingLikedMore(false)
    }
  }, [isLoadingLikedInitial])

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
      loadLikedInitial()
      loadPlaylists()
    }
  }, [status?.connected, loadLikedInitial, loadPlaylists])

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
      setStatus((prev) => (prev ? { ...prev, connected: false, connectedAt: null, tokenExpired: false } : null))
      setLikedSongs([])
      setPlaylists([])
      setSelectedPlaylist(null)
      setLikedLoadMoreError(null)
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
          t.album.toLowerCase().includes(likedSearchQuery.toLowerCase()),
      )
    : likedSongs

  const filteredPlaylists = playlistSearchQuery
    ? playlists.filter((p) => p.name.toLowerCase().includes(playlistSearchQuery.toLowerCase()))
    : playlists

  const likedHasMore = likedSongs.length < likedTotal

  if (isLoadingStatus) {
    return (
      <div className="flex h-screen flex-col bg-background">
        <AppHeader />
        <div className="flex flex-1 items-center justify-center">
          <Loader2 className="size-8 animate-spin text-muted-foreground" />
        </div>
      </div>
    )
  }

  if (!status?.connected) {
    const hasCredentials = credentials?.hasClientSecret && credentials?.clientId

    return (
      <div className="flex h-screen flex-col bg-background">
        <AppHeader />
        <div className="flex flex-1 items-center justify-center p-6">
          <div className="max-w-md text-center">
            <div className="mx-auto mb-6 flex size-20 items-center justify-center rounded-full bg-[#1DB954]/10">
              <Music className="size-10 text-[#1DB954]" />
            </div>
            <h2 className="mb-2 text-2xl font-bold">Connect Spotify</h2>
            <p className="mb-8 text-muted-foreground">Link your Spotify account to browse your playlists and liked songs.</p>

            {error && (
              <div className="mb-6 rounded-lg border border-destructive/50 bg-destructive/10 px-4 py-3 text-sm text-destructive">
                {error}
              </div>
            )}

            {!hasCredentials ? (
              <div className="space-y-4">
                <div className="rounded-lg border border-border bg-card p-4 text-left">
                  <div className="mb-2 flex items-center gap-2">
                    <KeyRound className="size-4 text-muted-foreground" />
                    <span className="text-sm font-medium">Spotify API credentials required</span>
                  </div>
                  <p className="mb-3 text-xs text-muted-foreground">
                    You need to configure your Spotify Client ID and Client Secret before connecting.
                  </p>
                  <Link href="/settings">
                    <Button variant="outline" size="sm" className="w-full">
                      <Settings className="mr-2 size-4" />
                      Go to Settings
                    </Button>
                  </Link>
                </div>
              </div>
            ) : (
              <Button
                size="lg"
                className="bg-[#1DB954] px-8 text-white hover:bg-[#1DB954]/90"
                onClick={handleConnect}
                disabled={isConnecting}
              >
                {isConnecting ? (
                  <Loader2 className="mr-2 size-5 animate-spin" />
                ) : (
                  <ExternalLink className="mr-2 size-5" />
                )}
                Connect with Spotify
              </Button>
            )}
          </div>
        </div>
      </div>
    )
  }

  if (selectedPlaylist) {
    return (
      <div className="flex h-screen flex-col bg-background">
        <AppHeader />
        <PlaylistDetailView playlist={selectedPlaylist} onBack={() => setSelectedPlaylist(null)} />
      </div>
    )
  }

  return (
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />

      <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
        <div className="shrink-0 border-b border-border bg-card/30 px-4 py-5 md:px-6">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <div className="flex items-center gap-2">
                <h1 className="text-2xl font-bold">Spotify</h1>
                <Badge className="border-0 bg-[#1DB954]/20 text-[#1DB954]">Connected</Badge>
              </div>
              {status.connectedAt && (
                <p className="mt-1 text-sm text-muted-foreground">
                  Connected since {new Date(status.connectedAt).toLocaleDateString()}
                </p>
              )}
            </div>
            <Button variant="outline" onClick={handleDisconnect} disabled={isDisconnecting}>
              {isDisconnecting ? (
                <Loader2 className="mr-2 size-4 animate-spin" />
              ) : (
                <LogOut className="mr-2 size-4" />
              )}
              Disconnect
            </Button>
          </div>
        </div>

        <Tabs defaultValue="liked" className="flex min-h-0 flex-1 flex-col gap-0 overflow-hidden">
          <div className="shrink-0 border-b border-border px-4 md:px-6">
            <TabsList className="h-12 bg-transparent p-0">
              <TabsTrigger
                value="liked"
                className="h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:bg-transparent data-[state=active]:shadow-none"
              >
                <Heart className="mr-2 size-4" />
                Liked Songs
              </TabsTrigger>
              <TabsTrigger
                value="playlists"
                className="h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:bg-transparent data-[state=active]:shadow-none"
              >
                <ListMusic className="mr-2 size-4" />
                Playlists
              </TabsTrigger>
            </TabsList>
          </div>

          <TabsContent value="liked" className="m-0 flex min-h-0 flex-1 flex-col overflow-hidden">
            <div className="flex shrink-0 items-center gap-3 border-b border-border px-4 py-3 md:px-6">
              <div className="relative max-w-md flex-1">
                <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  placeholder="Search liked songs..."
                  value={likedSearchQuery}
                  onChange={(e) => setLikedSearchQuery(e.target.value)}
                  className="border-0 bg-secondary pl-9"
                />
              </div>
              <span className="shrink-0 text-sm text-muted-foreground">{likedTotal} songs</span>
            </div>

            {likedError ? (
              <div className="flex flex-col items-center justify-center py-12 text-center">
                <AlertCircle className="mb-3 size-10 text-destructive" />
                <p className="text-muted-foreground">{likedError}</p>
                <Button variant="outline" size="sm" className="mt-4" onClick={() => loadLikedInitial()}>
                  Retry
                </Button>
              </div>
            ) : isLoadingLikedInitial ? (
              <TrackListSkeleton />
            ) : (
              <ScrollArea className="min-h-0 flex-1" viewportRef={likedViewportRef}>
                <div className={trackListHeaderClass()}>
                  <span className="w-8 text-right">#</span>
                  <span className="size-10 shrink-0" />
                  <span className="min-w-0 flex-1">Title</span>
                  <span className="hidden max-w-[200px] md:block">Album</span>
                  <span className="hidden w-24 text-right lg:block">Date Added</span>
                  <span className="w-12 shrink-0 text-right">
                    <Clock className="inline size-3.5" />
                  </span>
                </div>
                <div className="px-2 pb-2 md:px-4">
                  {filteredLikedSongs.map((track, i) => (
                    <TrackRow key={`${track.spotifyId}-${i}`} track={track} index={i} />
                  ))}
                  {filteredLikedSongs.length === 0 && !isLoadingLikedInitial && (
                    <div className="flex flex-col items-center justify-center py-12 text-center">
                      <Heart className="mb-3 size-10 text-muted-foreground" />
                      <p className="text-muted-foreground">
                        {likedSearchQuery ? "No matching songs found" : "No liked songs yet"}
                      </p>
                    </div>
                  )}
                </div>
                <LoadMoreSentinel
                  scrollRootRef={likedViewportRef}
                  hasMore={likedHasMore}
                  isLoading={isLoadingLikedMore}
                  onLoadMore={loadMoreLiked}
                  error={likedLoadMoreError}
                  onRetry={loadMoreLiked}
                />
              </ScrollArea>
            )}
          </TabsContent>

          <TabsContent value="playlists" className="m-0 flex min-h-0 flex-1 flex-col overflow-hidden">
            <div className="flex shrink-0 items-center gap-3 border-b border-border px-4 py-3 md:px-6">
              <div className="relative max-w-md flex-1">
                <Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  placeholder="Search playlists..."
                  value={playlistSearchQuery}
                  onChange={(e) => setPlaylistSearchQuery(e.target.value)}
                  className="border-0 bg-secondary pl-9"
                />
              </div>
              <span className="shrink-0 text-sm text-muted-foreground">{playlists.length} playlists</span>
            </div>

            {playlistsError ? (
              <div className="flex flex-col items-center justify-center py-12 text-center">
                <AlertCircle className="mb-3 size-10 text-destructive" />
                <p className="text-muted-foreground">{playlistsError}</p>
                <Button variant="outline" size="sm" className="mt-4" onClick={loadPlaylists}>
                  Retry
                </Button>
              </div>
            ) : isLoadingPlaylists ? (
              <PlaylistGridSkeleton />
            ) : (
              <ScrollArea className="min-h-0 flex-1">
                <div className="grid grid-cols-2 gap-4 p-4 sm:grid-cols-3 md:grid-cols-4 md:p-6 lg:grid-cols-5">
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
                    <ListMusic className="mb-3 size-10 text-muted-foreground" />
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
    </div>
  )
}
