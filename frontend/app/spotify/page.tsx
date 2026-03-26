"use client"

import { useState, useEffect, useCallback, Suspense } from "react"
import { useRouter, useSearchParams } from "next/navigation"
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
  fetchSpotifyLikedSongsComparison,
  fetchSpotifyLikedSongsComparisonSummary,
} from "@/lib/api-client"
import { isDemoMode } from "@/lib/app-mode"
import type {
  SpotifyStatusResponse,
  SpotifyApiTrack,
  SpotifyApiPlaylist,
  SpotifyCredentialsResponse,
  SpotifyComparisonItem,
  SpotifyComparisonMatchStatus,
  SpotifyComparisonSummaryApiResponse,
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
  Columns2,
  Link2,
  ChevronDown,
  ChevronUp,
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

type ComparisonFilterTab = "all" | SpotifyComparisonMatchStatus

function ComparisonSummaryPills({
  summary,
  isLoading,
}: {
  summary: SpotifyComparisonSummaryApiResponse | null
  isLoading: boolean
}) {
  if (isLoading && !summary) {
    return (
      <div className="flex flex-wrap gap-2">
        <Skeleton className="h-8 w-36 rounded-full" />
        <Skeleton className="h-8 w-40 rounded-full" />
        <Skeleton className="h-8 w-36 rounded-full" />
      </div>
    )
  }
  if (!summary) return null
  return (
    <div className="flex flex-wrap gap-2">
      <div className="inline-flex items-center gap-1.5 rounded-full border border-emerald-500/30 bg-emerald-500/10 px-3 py-1 text-sm">
        <span aria-hidden>✅</span>
        <span className="font-medium tabular-nums">{summary.inLibrary}</span>
        <span className="text-muted-foreground">in library</span>
      </div>
      <div className="inline-flex items-center gap-1.5 rounded-full border border-amber-500/35 bg-amber-500/10 px-3 py-1 text-sm">
        <span aria-hidden>🟡</span>
        <span className="font-medium tabular-nums">{summary.possibleMatch}</span>
        <span className="text-muted-foreground">possible matches</span>
      </div>
      <div className="inline-flex items-center gap-1.5 rounded-full border border-rose-500/30 bg-rose-500/10 px-3 py-1 text-sm">
        <span aria-hidden>❌</span>
        <span className="font-medium tabular-nums">{summary.notInLibrary}</span>
        <span className="text-muted-foreground">not in library</span>
      </div>
    </div>
  )
}

function ComparisonTrackRow({
  item,
  index,
  expanded,
  onToggleExpand,
}: {
  item: SpotifyComparisonItem
  index: number
  expanded: boolean
  onToggleExpand: () => void
}) {
  const isPossible = item.matchStatus === "PossibleMatch"
  const isNotInLib = item.matchStatus === "NotInLibrary"
  const matched = item.matchedTrack

  return (
    <div
      className={
        isNotInLib
          ? "rounded-lg border border-rose-500/20 bg-rose-500/[0.06]"
          : "rounded-lg"
      }
    >
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
          {item.albumArt ? (
            <img
              src={item.albumArt}
              alt={item.album}
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
          <p className="truncate text-sm font-medium">{item.title}</p>
          <p className="text-xs text-muted-foreground truncate">{item.artist}</p>
        </div>

        <span className="text-sm text-muted-foreground truncate hidden md:block max-w-[200px]">
          {item.album}
        </span>

        <span className="text-xs text-muted-foreground shrink-0 hidden lg:block w-24 text-right">
          {formatDateAdded(item.addedAt)}
        </span>

        <span className="text-xs text-muted-foreground shrink-0 w-12 text-right">
          {formatDuration(item.durationMs)}
        </span>

        <div className="shrink-0 flex items-center gap-1.5 min-w-[140px] justify-end">
          {item.matchStatus === "InLibrary" && matched != null && (
            <Link
              href={`/app?song=${matched.id}`}
              className="inline-flex items-center gap-1 rounded-full border border-emerald-500/40 bg-emerald-500/15 px-2.5 py-0.5 text-xs font-medium text-emerald-800 dark:text-emerald-300 hover:bg-emerald-500/25"
              onClick={(e) => e.stopPropagation()}
            >
              In Library
              <Link2 className="size-3" />
            </Link>
          )}
          {item.matchStatus === "PossibleMatch" && matched != null && (
            <div className="flex items-center gap-1">
              <Link
                href={`/app?song=${matched.id}`}
                className="inline-flex items-center gap-1 rounded-full border border-amber-500/45 bg-amber-500/15 px-2.5 py-0.5 text-xs font-medium text-amber-900 dark:text-amber-200 hover:bg-amber-500/25 max-w-[200px]"
                onClick={(e) => e.stopPropagation()}
                title="Open matched track in library"
              >
                <span className="truncate">Possible match</span>
                <span className="tabular-nums shrink-0">{formatMatchConfidence(item.matchConfidence)}</span>
                <Link2 className="size-3 shrink-0" />
              </Link>
              {expanded ? (
                <ChevronUp className="size-4 text-muted-foreground" />
              ) : (
                <ChevronDown className="size-4 text-muted-foreground" />
              )}
            </div>
          )}
          {item.matchStatus === "NotInLibrary" && (
            <span className="inline-flex rounded-full border border-muted-foreground/30 bg-muted/50 px-2.5 py-0.5 text-xs font-medium text-muted-foreground">
              Not in library
            </span>
          )}
        </div>
      </div>

      {isPossible && expanded && matched != null && (
        <div className="border-t border-border/80 bg-secondary/20 px-3 py-4 md:px-6">
          <p className="text-xs font-medium text-muted-foreground mb-3 flex items-center gap-1.5">
            <Columns2 className="size-3.5" />
            Spotify vs local (possible match · {formatMatchConfidence(item.matchConfidence)})
          </p>
          <div className="grid gap-4 md:grid-cols-2">
            <div className="rounded-lg border border-border bg-card/50 p-3 space-y-2">
              <p className="text-xs uppercase tracking-wide text-muted-foreground">Spotify</p>
              <p className="text-sm font-medium">{item.title}</p>
              <p className="text-xs text-muted-foreground">{item.artist}</p>
              <p className="text-xs text-muted-foreground">{item.album}</p>
              <p className="text-xs text-muted-foreground">{formatDuration(item.durationMs)}</p>
            </div>
            <div className="rounded-lg border border-border bg-card/50 p-3 space-y-2">
              <div className="flex items-center justify-between gap-2">
                <p className="text-xs uppercase tracking-wide text-muted-foreground">MusicHoarder</p>
                <Link
                  href={`/app?song=${matched.id}`}
                  className="text-xs text-primary inline-flex items-center gap-1 hover:underline"
                >
                  Open track
                  <Link2 className="size-3" />
                </Link>
              </div>
              <p className="text-sm font-medium">{matched.title ?? "—"}</p>
              <p className="text-xs text-muted-foreground">{matched.artist ?? "—"}</p>
              <p className="text-xs text-muted-foreground">
                Enrichment: {matched.enrichmentStatus}
              </p>
            </div>
          </div>
        </div>
      )}
    </div>
  )
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
          <div className="p-2 md:px-4">
            {filteredTracks.map((track, i) => (
              <TrackRow key={`${track.spotifyId}-${i}`} track={track} index={offset + i} />
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

  const [spotifyMainTab, setSpotifyMainTab] = useState<"liked" | "playlists" | "comparison">("liked")
  const [comparisonSummary, setComparisonSummary] =
    useState<SpotifyComparisonSummaryApiResponse | null>(null)
  const [comparisonItems, setComparisonItems] = useState<SpotifyComparisonItem[]>([])
  const [comparisonTotal, setComparisonTotal] = useState(0)
  const [comparisonFilter, setComparisonFilter] = useState<ComparisonFilterTab>("all")
  const [comparisonLoading, setComparisonLoading] = useState(false)
  const [comparisonLoadingMore, setComparisonLoadingMore] = useState(false)
  const [comparisonError, setComparisonError] = useState<string | null>(null)
  const [expandedComparisonIds, setExpandedComparisonIds] = useState<Set<string>>(() => new Set())

  const likedLimit = 50
  const comparisonLimit = 50

  const comparisonApiFilter: SpotifyComparisonMatchStatus | null =
    comparisonFilter === "all" ? null : comparisonFilter

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

  const loadMoreComparison = useCallback(async () => {
    if (comparisonItems.length >= comparisonTotal || comparisonLoadingMore || comparisonLoading) return
    setComparisonLoadingMore(true)
    setComparisonError(null)
    try {
      const page = await fetchSpotifyLikedSongsComparison(
        comparisonItems.length,
        comparisonLimit,
        comparisonApiFilter
      )
      setComparisonItems((prev) => [...prev, ...page.items])
    } catch (err) {
      setComparisonError(err instanceof Error ? err.message : "Failed to load more")
    } finally {
      setComparisonLoadingMore(false)
    }
  }, [
    comparisonItems.length,
    comparisonTotal,
    comparisonLoadingMore,
    comparisonLoading,
    comparisonApiFilter,
    comparisonLimit,
  ])

  const loadComparisonRefresh = useCallback(async () => {
    setComparisonLoading(true)
    setComparisonError(null)
    try {
      const filter = comparisonFilter === "all" ? null : comparisonFilter
      const [sum, page] = await Promise.all([
        fetchSpotifyLikedSongsComparisonSummary(),
        fetchSpotifyLikedSongsComparison(0, comparisonLimit, filter),
      ])
      setComparisonSummary(sum)
      setComparisonItems(page.items)
      setComparisonTotal(page.total)
      setExpandedComparisonIds(new Set())
    } catch (err) {
      setComparisonError(err instanceof Error ? err.message : "Failed to load library comparison")
    } finally {
      setComparisonLoading(false)
    }
  }, [comparisonFilter, comparisonLimit])

  useEffect(() => {
    if (!status?.connected || spotifyMainTab !== "comparison") return

    let cancelled = false
    setComparisonLoading(true)
    setComparisonError(null)

    void (async () => {
      try {
        const filter = comparisonFilter === "all" ? null : comparisonFilter
        const [sum, page] = await Promise.all([
          fetchSpotifyLikedSongsComparisonSummary(),
          fetchSpotifyLikedSongsComparison(0, comparisonLimit, filter),
        ])
        if (cancelled) return
        setComparisonSummary(sum)
        setComparisonItems(page.items)
        setComparisonTotal(page.total)
        setExpandedComparisonIds(new Set())
      } catch (err) {
        if (cancelled) return
        setComparisonError(err instanceof Error ? err.message : "Failed to load library comparison")
      } finally {
        if (!cancelled) setComparisonLoading(false)
      }
    })()

    return () => {
      cancelled = true
    }
  }, [status?.connected, spotifyMainTab, comparisonFilter, comparisonLimit])

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
      setComparisonSummary(null)
      setComparisonItems([])
      setComparisonTotal(0)
      setComparisonError(null)
      setExpandedComparisonIds(new Set())
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
            <h2 className="text-2xl font-bold mb-2">Connect Spotify</h2>
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
      </div>
    )
  }

  if (selectedPlaylist) {
    return (
      <div className="flex h-screen flex-col bg-background">
        <AppHeader />
        <PlaylistDetailView
          playlist={selectedPlaylist}
          onBack={() => setSelectedPlaylist(null)}
        />
      </div>
    )
  }

  return (
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />

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

        <Tabs
          value={spotifyMainTab}
          onValueChange={(v) =>
            setSpotifyMainTab(v as "liked" | "playlists" | "comparison")
          }
          className="flex min-h-0 flex-1 flex-col overflow-hidden"
        >
          <div className="border-b border-border px-4 md:px-6">
            <TabsList className="h-12 bg-transparent p-0 flex-wrap gap-y-1">
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
              <TabsTrigger
                value="comparison"
                className="h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:bg-transparent data-[state=active]:shadow-none"
              >
                <Columns2 className="size-4 mr-2" />
                Library Comparison
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
                </div>
                <div className="p-2 md:px-4">
                  {filteredLikedSongs.map((track, i) => (
                    <TrackRow key={`${track.spotifyId}-${i}`} track={track} index={likedOffset + i} />
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

          {/* Library Comparison (BRINK-69) */}
          <TabsContent value="comparison" className="m-0 flex min-h-0 flex-1 flex-col overflow-hidden">
            <div className="space-y-4 border-b border-border px-4 py-4 md:px-6">
              <ComparisonSummaryPills summary={comparisonSummary} isLoading={comparisonLoading} />
              <div className="flex flex-wrap gap-2">
                {(
                  [
                    { id: "all" as const, label: "All" },
                    { id: "InLibrary" as const, label: "In Library" },
                    { id: "PossibleMatch" as const, label: "Possible Match" },
                    { id: "NotInLibrary" as const, label: "Not in Library" },
                  ] as const
                ).map(({ id, label }) => (
                  <Button
                    key={id}
                    type="button"
                    variant={comparisonFilter === id ? "secondary" : "ghost"}
                    size="sm"
                    className="rounded-full"
                    onClick={() => setComparisonFilter(id)}
                  >
                    {label}
                  </Button>
                ))}
              </div>
            </div>

            {comparisonError ? (
              <div className="flex flex-col items-center justify-center py-12 text-center px-4">
                <AlertCircle className="size-10 text-destructive mb-3" />
                <p className="text-muted-foreground">{comparisonError}</p>
                <Button
                  variant="outline"
                  size="sm"
                  className="mt-4"
                  onClick={() => void loadComparisonRefresh()}
                >
                  Retry
                </Button>
              </div>
            ) : comparisonLoading && comparisonItems.length === 0 ? (
              <TrackListSkeleton />
            ) : (
              <ScrollArea className="min-h-0 flex-1">
                <div className="p-2 md:px-4 pb-6">
                  {comparisonItems.map((item, i) => (
                    <ComparisonTrackRow
                      key={`${item.spotifyId}-${i}`}
                      item={item}
                      index={i}
                      expanded={expandedComparisonIds.has(item.spotifyId)}
                      onToggleExpand={() => {
                        setExpandedComparisonIds((prev) => {
                          const next = new Set(prev)
                          if (next.has(item.spotifyId)) next.delete(item.spotifyId)
                          else next.add(item.spotifyId)
                          return next
                        })
                      }}
                    />
                  ))}

                  {!comparisonLoading &&
                    comparisonItems.length === 0 &&
                    comparisonSummary &&
                    (() => {
                      const s = comparisonSummary
                      if (s.total === 0) {
                        return (
                          <div className="flex flex-col items-center justify-center py-14 text-center px-4">
                            <Heart className="size-10 text-muted-foreground mb-3" />
                            <p className="text-muted-foreground">No liked songs to compare yet.</p>
                          </div>
                        )
                      }
                      if (comparisonFilter === "all") {
                        if (s.inLibrary === s.total) {
                          return (
                            <div className="flex flex-col items-center justify-center py-14 text-center px-4">
                              <CheckCircle2 className="size-10 text-emerald-600 dark:text-emerald-400 mb-3" />
                              <p className="text-muted-foreground">
                                All your liked songs are in your library 🎉
                              </p>
                            </div>
                          )
                        }
                        return (
                          <div className="flex flex-col items-center justify-center py-14 text-center px-4">
                            <Columns2 className="size-10 text-muted-foreground mb-3" />
                            <p className="text-muted-foreground">No tracks on this page.</p>
                          </div>
                        )
                      }
                      if (comparisonFilter === "InLibrary" && s.inLibrary === 0) {
                        return (
                          <div className="flex flex-col items-center justify-center py-14 text-center px-4">
                            <Music className="size-10 text-muted-foreground mb-3" />
                            <p className="text-muted-foreground">
                              None of your liked songs are in the library yet.
                            </p>
                          </div>
                        )
                      }
                      if (comparisonFilter === "PossibleMatch" && s.possibleMatch === 0) {
                        return (
                          <div className="flex flex-col items-center justify-center py-14 text-center px-4">
                            <CheckCircle2 className="size-10 text-amber-600 dark:text-amber-400 mb-3" />
                            <p className="text-muted-foreground">
                              No fuzzy matches — each song is either clearly in your library or missing.
                            </p>
                          </div>
                        )
                      }
                      if (comparisonFilter === "NotInLibrary" && s.notInLibrary === 0) {
                        return (
                          <div className="flex flex-col items-center justify-center py-14 text-center px-4">
                            <CheckCircle2 className="size-10 text-emerald-600 dark:text-emerald-400 mb-3" />
                            <p className="text-muted-foreground">
                              All your liked songs are in your library 🎉
                            </p>
                          </div>
                        )
                      }
                      return (
                        <div className="flex flex-col items-center justify-center py-14 text-center px-4">
                          <AlertCircle className="size-10 text-muted-foreground mb-3" />
                          <p className="text-muted-foreground">Nothing to show for this filter.</p>
                        </div>
                      )
                    })()}

                  {comparisonItems.length > 0 && comparisonItems.length < comparisonTotal && (
                    <div className="flex justify-center pt-4">
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={comparisonLoadingMore}
                        onClick={() => void loadMoreComparison()}
                      >
                        {comparisonLoadingMore ? (
                          <>
                            <Loader2 className="size-4 mr-2 animate-spin" />
                            Loading…
                          </>
                        ) : (
                          `Load more (${comparisonItems.length} / ${comparisonTotal})`
                        )}
                      </Button>
                    </div>
                  )}
                </div>
              </ScrollArea>
            )}
          </TabsContent>
        </Tabs>
      </div>
    </div>
  )
}

function SpotifyPageFallback() {
  return (
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />
      <div className="flex flex-1 items-center justify-center">
        <Loader2 className="size-8 animate-spin text-muted-foreground" />
      </div>
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
