"use client"

import { useCallback, useEffect, useState } from "react"
import { Card, CardContent } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Label } from "@/components/ui/label"
import { Textarea } from "@/components/ui/textarea"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import {
  Music,
  Check,
  ChevronLeft,
  ChevronRight,
  AlertTriangle,
  Fingerprint,
  ArrowRight,
  SkipForward,
  Trash2,
  Save,
  RefreshCw,
  Loader2,
  CheckCheck,
  X,
} from "lucide-react"
import type { ApiSong } from "@/lib/api-client"
import {
  fetchReviewTracks,
  submitManualReview,
  softDeleteSong,
  bulkApprove,
} from "@/lib/api-client"
import { AppHeader } from "@/components/app-header"

interface MetadataEdits {
  artist?: string
  albumArtist?: string
  album?: string
  title?: string
  year?: number
  trackNumber?: number
}

export default function ReviewPage() {
  const [tracks, setTracks] = useState<ApiSong[]>([])
  const [selectedIndex, setSelectedIndex] = useState(0)
  const [editedMetadata, setEditedMetadata] = useState<Record<number, MetadataEdits>>({})
  const [loading, setLoading] = useState(true)
  const [actionLoading, setActionLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [rejectReason, setRejectReason] = useState("")
  const [bulkApproveMinConfidence, setBulkApproveMinConfidence] = useState(0.75)
  const [bulkApproveResult, setBulkApproveResult] = useState<{ count: number } | null>(null)

  const loadTracks = useCallback(async () => {
    try {
      setLoading(true)
      setError(null)
      const songs = await fetchReviewTracks()
      setTracks(songs)
      setSelectedIndex(0)
      setEditedMetadata({})
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load tracks")
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    loadTracks()
  }, [loadTracks])

  const selectedTrack = tracks[selectedIndex]
  const currentEdits = selectedTrack ? editedMetadata[selectedTrack.id] || {} : {}

  const handleNext = () => {
    if (selectedIndex < tracks.length - 1) {
      setSelectedIndex(selectedIndex + 1)
      setRejectReason("")
    }
  }

  const handlePrev = () => {
    if (selectedIndex > 0) {
      setSelectedIndex(selectedIndex - 1)
      setRejectReason("")
    }
  }

  const handleAcceptOriginal = () => {
    if (!selectedTrack?.originalMetadataCaptured) return

    setEditedMetadata((prev) => ({
      ...prev,
      [selectedTrack.id]: {
        ...prev[selectedTrack.id],
        artist: selectedTrack.originalArtist ?? undefined,
        albumArtist: selectedTrack.originalAlbumArtist ?? undefined,
        album: selectedTrack.originalAlbum ?? undefined,
        title: selectedTrack.originalTitle ?? undefined,
        year: selectedTrack.originalYear ?? undefined,
        trackNumber: selectedTrack.originalTrackNumber ?? undefined,
      },
    }))
  }

  const handleFieldChange = (field: string, value: string | number) => {
    if (!selectedTrack) return
    setEditedMetadata((prev) => ({
      ...prev,
      [selectedTrack.id]: {
        ...prev[selectedTrack.id],
        [field]: value,
      },
    }))
  }

  const getDisplayValue = (field: keyof MetadataEdits): string | number => {
    if (!selectedTrack) return ""
    const editValue = currentEdits[field]
    if (editValue !== undefined) return editValue
    const songValue = selectedTrack[field as keyof ApiSong]
    if (songValue !== undefined && songValue !== null) return songValue as string | number
    return ""
  }

  const buildMetadataOverrides = (): Partial<MetadataEdits> => {
    if (!selectedTrack) return {}
    const edits = editedMetadata[selectedTrack.id]
    if (!edits) return {}

    const overrides: Partial<MetadataEdits> = {}
    if (edits.artist !== undefined) overrides.artist = edits.artist
    if (edits.albumArtist !== undefined) overrides.albumArtist = edits.albumArtist
    if (edits.album !== undefined) overrides.album = edits.album
    if (edits.title !== undefined) overrides.title = edits.title
    if (edits.year !== undefined) overrides.year = edits.year
    if (edits.trackNumber !== undefined) overrides.trackNumber = edits.trackNumber
    return overrides
  }

  const handleApprove = async () => {
    if (!selectedTrack || actionLoading) return
    try {
      setActionLoading(true)
      setError(null)
      const overrides = buildMetadataOverrides()
      await submitManualReview(selectedTrack.id, {
        decision: "approve",
        ...overrides,
      })
      setTracks((prev) => {
        const next = prev.filter((t) => t.id !== selectedTrack.id)
        if (selectedIndex >= next.length && selectedIndex > 0) {
          setSelectedIndex(next.length - 1)
        }
        return next
      })
      setRejectReason("")
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to approve track")
    } finally {
      setActionLoading(false)
    }
  }

  const handleReject = async () => {
    if (!selectedTrack || actionLoading) return
    try {
      setActionLoading(true)
      setError(null)
      await submitManualReview(selectedTrack.id, {
        decision: "reject",
        rejectReason: rejectReason || undefined,
      })
      setTracks((prev) =>
        prev.map((t) =>
          t.id === selectedTrack.id
            ? { ...t, matchedBy: null, matchConfidence: null, matchWarnings: null, enrichmentError: rejectReason || "Manually rejected" }
            : t
        )
      )
      if (selectedIndex < tracks.length - 1) {
        setSelectedIndex(selectedIndex + 1)
      }
      setRejectReason("")
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to reject track")
    } finally {
      setActionLoading(false)
    }
  }

  const handleSkip = () => {
    handleNext()
  }

  const handleDelete = async () => {
    if (!selectedTrack || actionLoading) return
    try {
      setActionLoading(true)
      setError(null)
      await softDeleteSong(selectedTrack.id)
      setTracks((prev) => {
        const next = prev.filter((t) => t.id !== selectedTrack.id)
        if (selectedIndex >= next.length && selectedIndex > 0) {
          setSelectedIndex(next.length - 1)
        }
        return next
      })
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to delete track")
    } finally {
      setActionLoading(false)
    }
  }

  const handleBulkApprove = async () => {
    if (actionLoading) return
    try {
      setActionLoading(true)
      setError(null)
      setBulkApproveResult(null)
      const result = await bulkApprove(bulkApproveMinConfidence)
      setBulkApproveResult({ count: result.approvedCount })
      if (result.approvedCount > 0) {
        await loadTracks()
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to bulk approve")
    } finally {
      setActionLoading(false)
    }
  }

  if (loading) {
    return (
      <div className="flex min-h-screen flex-col bg-background">
        <AppHeader />
        <main className="flex flex-1 items-center justify-center p-4">
          <div className="flex flex-col items-center gap-4">
            <Loader2 className="size-8 animate-spin text-primary" />
            <p className="text-muted-foreground">Loading tracks for review...</p>
          </div>
        </main>
      </div>
    )
  }

  if (error && tracks.length === 0) {
    return (
      <div className="flex min-h-screen flex-col bg-background">
        <AppHeader />
        <main className="flex flex-1 items-center justify-center p-4">
          <Card className="max-w-md text-center">
            <CardContent className="p-8">
              <div className="mx-auto mb-4 flex size-16 items-center justify-center rounded-full bg-destructive/10">
                <X className="size-8 text-destructive" />
              </div>
              <h2 className="mb-2 text-xl font-semibold">Error</h2>
              <p className="mb-4 text-muted-foreground">{error}</p>
              <Button onClick={loadTracks}>Retry</Button>
            </CardContent>
          </Card>
        </main>
      </div>
    )
  }

  if (tracks.length === 0) {
    return (
      <div className="flex min-h-screen flex-col bg-background">
        <AppHeader />
        <main className="flex flex-1 items-center justify-center p-4">
          <Card className="max-w-md text-center">
            <CardContent className="p-8">
              <div className="mx-auto mb-4 flex size-16 items-center justify-center rounded-full bg-primary/10">
                <Check className="size-8 text-primary" />
              </div>
              <h2 className="mb-2 text-xl font-semibold">All Done!</h2>
              <p className="mb-4 text-muted-foreground">
                No more tracks need review. Great job!
              </p>
              <div className="flex gap-2 justify-center">
                <Button variant="outline" onClick={loadTracks}>
                  <RefreshCw className="mr-2 size-4" />
                  Refresh
                </Button>
                <Button asChild>
                  <a href="/overview">Back to Overview</a>
                </Button>
              </div>
            </CardContent>
          </Card>
        </main>
      </div>
    )
  }

  const eligibleForBulk = tracks.filter(
    (t) => t.matchConfidence != null && t.matchConfidence >= bulkApproveMinConfidence
  ).length

  return (
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />

      <main className="flex flex-1 flex-col overflow-hidden">
        <div className="mx-auto flex w-full max-w-7xl flex-1 flex-col overflow-hidden px-4 md:px-6">
          {/* Header */}
          <div className="flex flex-col gap-4 py-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h1 className="text-2xl font-bold md:text-3xl">Manual Review</h1>
              <p className="text-muted-foreground">
                Review and correct track metadata
              </p>
            </div>
            <div className="flex items-center gap-2">
              {/* Bulk Approve */}
              <AlertDialog>
                <AlertDialogTrigger asChild>
                  <Button variant="outline" size="sm" className="gap-2">
                    <CheckCheck className="size-4" />
                    Bulk Approve
                  </Button>
                </AlertDialogTrigger>
                <AlertDialogContent>
                  <AlertDialogHeader>
                    <AlertDialogTitle>Bulk Approve Tracks</AlertDialogTitle>
                    <AlertDialogDescription>
                      Approve all tracks with match confidence at or above the threshold.
                      {eligibleForBulk > 0
                        ? ` ${eligibleForBulk} track${eligibleForBulk !== 1 ? "s" : ""} eligible.`
                        : " No tracks are currently eligible at this threshold."}
                    </AlertDialogDescription>
                  </AlertDialogHeader>
                  <div className="py-2">
                    <Label htmlFor="minConfidence" className="text-sm">
                      Minimum Confidence
                    </Label>
                    <Input
                      id="minConfidence"
                      type="number"
                      min={0}
                      max={1}
                      step={0.05}
                      value={bulkApproveMinConfidence}
                      onChange={(e) => setBulkApproveMinConfidence(parseFloat(e.target.value) || 0.75)}
                      className="mt-1"
                    />
                  </div>
                  {bulkApproveResult && (
                    <p className="text-sm text-muted-foreground">
                      Approved {bulkApproveResult.count} track{bulkApproveResult.count !== 1 ? "s" : ""}.
                    </p>
                  )}
                  <AlertDialogFooter>
                    <AlertDialogCancel>Cancel</AlertDialogCancel>
                    <AlertDialogAction onClick={handleBulkApprove} disabled={actionLoading}>
                      {actionLoading ? <Loader2 className="mr-2 size-4 animate-spin" /> : null}
                      Approve {eligibleForBulk} Track{eligibleForBulk !== 1 ? "s" : ""}
                    </AlertDialogAction>
                  </AlertDialogFooter>
                </AlertDialogContent>
              </AlertDialog>
              <Button variant="outline" size="icon" onClick={loadTracks} title="Refresh">
                <RefreshCw className="size-4" />
              </Button>
              <span className="text-sm text-muted-foreground">
                {selectedIndex + 1} of {tracks.length}
              </span>
              <div className="flex gap-1">
                <Button
                  variant="outline"
                  size="icon"
                  onClick={handlePrev}
                  disabled={selectedIndex === 0}
                >
                  <ChevronLeft className="size-4" />
                </Button>
                <Button
                  variant="outline"
                  size="icon"
                  onClick={handleNext}
                  disabled={selectedIndex === tracks.length - 1}
                >
                  <ChevronRight className="size-4" />
                </Button>
              </div>
            </div>
          </div>

          {error && (
            <div className="mb-2 rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
              {error}
              <Button variant="ghost" size="sm" className="ml-2" onClick={() => setError(null)}>
                Dismiss
              </Button>
            </div>
          )}

          {/* Main Content — fills remaining height */}
          <div className="grid min-h-0 flex-1 gap-4 pb-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
            {/* Left Panel - Track List */}
            <Card className="flex min-h-0 flex-col overflow-hidden">
              <div className="shrink-0 border-b border-border p-3">
                <h2 className="font-medium">Pending Review ({tracks.length})</h2>
              </div>
              <ScrollArea className="min-h-0 flex-1">
                <div className="space-y-1 p-2">
                  {tracks.map((track, index) => (
                    <button
                      key={track.id}
                      onClick={() => {
                        setSelectedIndex(index)
                        setRejectReason("")
                      }}
                      className={`flex w-full items-center gap-3 rounded-lg p-3 text-left transition-colors ${
                        index === selectedIndex
                          ? "bg-primary/10 border border-primary/20"
                          : "hover:bg-secondary"
                      }`}
                    >
                      <div className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-secondary">
                        <Music className="size-5 text-muted-foreground" />
                      </div>
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-sm font-medium">
                          {track.title || track.fileName || "Unknown"}
                        </p>
                        <p className="truncate text-xs text-muted-foreground">
                          {track.artist || "Unknown Artist"}
                          {track.matchConfidence != null && (
                            <span className="ml-1">
                              ({Math.round(track.matchConfidence * 100)}%)
                            </span>
                          )}
                        </p>
                      </div>
                      {track.matchWarnings && track.matchWarnings.length > 0 && (
                        <Badge variant="outline" className="shrink-0 gap-1 text-amber-400 border-amber-400/30">
                          <AlertTriangle className="size-3" />
                          {track.matchWarnings.length}
                        </Badge>
                      )}
                    </button>
                  ))}
                </div>
              </ScrollArea>
            </Card>

            {/* Right Panel - Edit Form + Action Bar */}
            <div className="flex min-h-0 flex-col gap-4">
              <Card className="flex min-h-0 flex-1 flex-col overflow-hidden">
                <Tabs defaultValue="edit" className="flex flex-1 flex-col overflow-hidden">
                  <div className="shrink-0 border-b border-border px-3">
                    <TabsList className="h-12 w-full justify-start rounded-none border-0 bg-transparent p-0">
                      <TabsTrigger
                        value="edit"
                        className="rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:border-l-transparent data-[state=active]:border-r-transparent data-[state=active]:border-t-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
                      >
                        Edit Metadata
                      </TabsTrigger>
                      <TabsTrigger
                        value="compare"
                        className="rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:border-l-transparent data-[state=active]:border-r-transparent data-[state=active]:border-t-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
                      >
                        Compare
                      </TabsTrigger>
                      <TabsTrigger
                        value="issues"
                        className="rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:border-b-primary/50 data-[state=active]:border-l-transparent data-[state=active]:border-r-transparent data-[state=active]:border-t-transparent data-[state=active]:bg-transparent data-[state=active]:shadow-none"
                      >
                        Issues
                      </TabsTrigger>
                    </TabsList>
                  </div>

                  <TabsContent value="edit" className="min-h-0 flex-1 overflow-hidden m-0">
                    <ScrollArea className="h-full">
                      <div className="space-y-4 p-4">
                        {/* Basic Info */}
                        <div className="space-y-2">
                          <div>
                            <Label htmlFor="title" className="text-xs text-muted-foreground">
                              Title
                            </Label>
                            <Input
                              id="title"
                              value={getDisplayValue("title")}
                              onChange={(e) => handleFieldChange("title", e.target.value)}
                              className="h-8"
                            />
                          </div>
                          <div>
                            <Label htmlFor="artist" className="text-xs text-muted-foreground">
                              Artist
                            </Label>
                            <Input
                              id="artist"
                              value={getDisplayValue("artist")}
                              onChange={(e) => handleFieldChange("artist", e.target.value)}
                              className="h-8"
                            />
                          </div>
                        </div>

                        <div className="grid gap-4 sm:grid-cols-2">
                          <div>
                            <Label htmlFor="album" className="text-xs text-muted-foreground">
                              Album
                            </Label>
                            <Input
                              id="album"
                              value={getDisplayValue("album")}
                              onChange={(e) => handleFieldChange("album", e.target.value)}
                              className="h-8"
                            />
                          </div>
                          <div>
                            <Label htmlFor="albumArtist" className="text-xs text-muted-foreground">
                              Album Artist
                            </Label>
                            <Input
                              id="albumArtist"
                              value={getDisplayValue("albumArtist")}
                              onChange={(e) => handleFieldChange("albumArtist", e.target.value)}
                              className="h-8"
                            />
                          </div>
                          <div>
                            <Label htmlFor="year" className="text-xs text-muted-foreground">
                              Year
                            </Label>
                            <Input
                              id="year"
                              type="number"
                              value={getDisplayValue("year")}
                              onChange={(e) => handleFieldChange("year", parseInt(e.target.value) || 0)}
                              className="h-8"
                            />
                          </div>
                          <div>
                            <Label htmlFor="trackNumber" className="text-xs text-muted-foreground">
                              Track Number
                            </Label>
                            <Input
                              id="trackNumber"
                              type="number"
                              value={getDisplayValue("trackNumber")}
                              onChange={(e) => handleFieldChange("trackNumber", parseInt(e.target.value) || 0)}
                              className="h-8"
                            />
                          </div>
                          <div>
                            <Label className="text-xs text-muted-foreground">Format</Label>
                            <Input
                              value={selectedTrack?.extension?.replace(/^\./, "").toUpperCase() || ""}
                              disabled
                              className="h-8 bg-secondary"
                            />
                          </div>
                          <div>
                            <Label className="text-xs text-muted-foreground">Matched By</Label>
                            <Input
                              value={selectedTrack?.matchedBy || "—"}
                              disabled
                              className="h-8 bg-secondary"
                            />
                          </div>
                        </div>

                        {/* Match confidence */}
                        {selectedTrack?.matchConfidence != null && (
                          <div className="rounded-lg bg-secondary/50 p-3">
                            <p className="mb-1 text-xs font-medium text-muted-foreground">
                              Match Confidence
                            </p>
                            <div className="flex items-center gap-2">
                              <div className="h-2 flex-1 rounded-full bg-secondary">
                                <div
                                  className={`h-2 rounded-full ${
                                    selectedTrack.matchConfidence >= 0.8
                                      ? "bg-green-500"
                                      : selectedTrack.matchConfidence >= 0.6
                                      ? "bg-amber-500"
                                      : "bg-red-500"
                                  }`}
                                  style={{ width: `${Math.round(selectedTrack.matchConfidence * 100)}%` }}
                                />
                              </div>
                              <span className="text-sm font-medium">
                                {Math.round(selectedTrack.matchConfidence * 100)}%
                              </span>
                            </div>
                          </div>
                        )}

                        {/* File Info */}
                        <div className="rounded-lg bg-secondary/50 p-3">
                          <p className="mb-2 text-xs font-medium text-muted-foreground">
                            Original File
                          </p>
                          <p className="break-all text-sm">{selectedTrack?.sourcePath}</p>
                        </div>

                        {/* Fingerprint */}
                        {selectedTrack?.fingerprint && (
                          <div className="flex items-center gap-2 rounded-lg bg-secondary/50 p-3">
                            <Fingerprint className="size-4 text-muted-foreground" />
                            <code className="text-xs text-muted-foreground break-all">
                              {selectedTrack.fingerprint.length > 60
                                ? `${selectedTrack.fingerprint.slice(0, 60)}...`
                                : selectedTrack.fingerprint}
                            </code>
                          </div>
                        )}

                        {/* Enrichment error */}
                        {selectedTrack?.enrichmentError && (
                          <div className="rounded-lg bg-amber-400/10 p-3">
                            <p className="mb-1 text-xs font-medium text-amber-400">
                              Enrichment Note
                            </p>
                            <p className="text-sm">{selectedTrack.enrichmentError}</p>
                          </div>
                        )}
                      </div>
                    </ScrollArea>
                  </TabsContent>

                  <TabsContent value="compare" className="min-h-0 flex-1 overflow-hidden m-0">
                    <ScrollArea className="h-full">
                      <div className="p-4">
                        {selectedTrack?.originalMetadataCaptured ? (
                          <div className="space-y-4">
                            <div className="flex items-center justify-between">
                              <h3 className="font-medium">Current vs Original Metadata</h3>
                              <Button size="sm" onClick={handleAcceptOriginal} className="gap-2">
                                <RefreshCw className="size-4" />
                                Restore Original
                              </Button>
                            </div>

                            <div className="space-y-3">
                              <CompareRow
                                label="Title"
                                current={selectedTrack.title}
                                original={selectedTrack.originalTitle}
                              />
                              <CompareRow
                                label="Artist"
                                current={selectedTrack.artist}
                                original={selectedTrack.originalArtist}
                              />
                              <CompareRow
                                label="Album"
                                current={selectedTrack.album}
                                original={selectedTrack.originalAlbum}
                              />
                              <CompareRow
                                label="Album Artist"
                                current={selectedTrack.albumArtist}
                                original={selectedTrack.originalAlbumArtist}
                              />
                              <CompareRow
                                label="Year"
                                current={selectedTrack.year?.toString()}
                                original={selectedTrack.originalYear?.toString()}
                              />
                              <CompareRow
                                label="Track Number"
                                current={selectedTrack.trackNumber?.toString()}
                                original={selectedTrack.originalTrackNumber?.toString()}
                              />
                            </div>
                          </div>
                        ) : (
                          <div className="flex flex-col items-center justify-center py-12 text-center">
                            <RefreshCw className="mb-4 size-12 text-muted-foreground" />
                            <h3 className="font-medium">No Original Metadata</h3>
                            <p className="text-sm text-muted-foreground">
                              Original metadata was not captured for this track
                            </p>
                          </div>
                        )}
                      </div>
                    </ScrollArea>
                  </TabsContent>

                  <TabsContent value="issues" className="min-h-0 flex-1 overflow-hidden m-0">
                    <ScrollArea className="h-full">
                      <div className="p-4">
                        {selectedTrack?.matchWarnings && selectedTrack.matchWarnings.length > 0 ? (
                          <div className="space-y-2">
                            {selectedTrack.matchWarnings.map((warning, index) => (
                              <div
                                key={index}
                                className="flex items-start gap-3 rounded-lg bg-amber-400/10 p-3"
                              >
                                <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" />
                                <p className="text-sm">{warning}</p>
                              </div>
                            ))}
                          </div>
                        ) : (
                          <div className="flex flex-col items-center justify-center py-12 text-center">
                            <Check className="mb-4 size-12 text-primary" />
                            <h3 className="font-medium">No Warnings</h3>
                            <p className="text-sm text-muted-foreground">
                              This track has no match warnings
                            </p>
                          </div>
                        )}
                      </div>
                    </ScrollArea>
                  </TabsContent>
                </Tabs>
              </Card>

              {/* Action Bar — always visible at the bottom of right panel */}
              <Card className="shrink-0">
                <div className="flex flex-wrap items-center gap-2 p-3">
                  <Button onClick={handleApprove} disabled={actionLoading} className="gap-2">
                    {actionLoading ? <Loader2 className="size-4 animate-spin" /> : <Save className="size-4" />}
                    Approve
                  </Button>
                  <Button variant="outline" onClick={handleReject} disabled={actionLoading} className="gap-2">
                    {actionLoading ? <Loader2 className="size-4 animate-spin" /> : <X className="size-4" />}
                    Reject
                  </Button>
                  <Button variant="outline" onClick={handleSkip} className="gap-2">
                    <SkipForward className="size-4" />
                    Skip
                  </Button>
                  <AlertDialog>
                    <AlertDialogTrigger asChild>
                      <Button variant="outline" className="gap-2 text-destructive hover:text-destructive">
                        <Trash2 className="size-4" />
                        Delete
                      </Button>
                    </AlertDialogTrigger>
                    <AlertDialogContent>
                      <AlertDialogHeader>
                        <AlertDialogTitle>Delete this track?</AlertDialogTitle>
                        <AlertDialogDescription>
                          This will soft-delete the track so it is excluded from review and library build. The original file will not be deleted.
                        </AlertDialogDescription>
                      </AlertDialogHeader>
                      <AlertDialogFooter>
                        <AlertDialogCancel>Cancel</AlertDialogCancel>
                        <AlertDialogAction onClick={handleDelete}>Delete</AlertDialogAction>
                      </AlertDialogFooter>
                    </AlertDialogContent>
                  </AlertDialog>
                  <div className="ml-auto flex items-center gap-2">
                    <Input
                      value={rejectReason}
                      onChange={(e) => setRejectReason(e.target.value)}
                      placeholder="Reject reason (optional)"
                      className="h-8 w-48"
                    />
                  </div>
                </div>
              </Card>
            </div>
          </div>
        </div>
      </main>
    </div>
  )
}

function CompareRow({
  label,
  current,
  original,
}: {
  label: string
  current?: string | null
  original?: string | null
}) {
  const isDifferent = current !== original && original

  return (
    <div className="rounded-lg border border-border p-3">
      <p className="mb-2 text-xs font-medium text-muted-foreground">{label}</p>
      <div className="flex items-center gap-3">
        <div className="min-w-0 flex-1">
          <p className="text-xs text-muted-foreground mb-0.5">Enriched</p>
          <p className={`truncate text-sm ${isDifferent ? "font-medium" : ""}`}>
            {current || "—"}
          </p>
        </div>
        {isDifferent && (
          <>
            <ArrowRight className="size-4 shrink-0 text-muted-foreground" />
            <div className="min-w-0 flex-1">
              <p className="text-xs text-muted-foreground mb-0.5">Original</p>
              <p className="truncate text-sm text-muted-foreground">{original}</p>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
