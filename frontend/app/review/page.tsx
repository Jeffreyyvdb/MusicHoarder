"use client"

import { useState } from "react"
import { Card, CardContent } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Label } from "@/components/ui/label"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
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
  X,
  ChevronLeft,
  ChevronRight,
  AlertTriangle,
  Fingerprint,
  ArrowRight,
  SkipForward,
  Trash2,
  Save,
  RefreshCw,
} from "lucide-react"
import { mockTracksForReview } from "@/lib/mock-data"
import type { TrackImport } from "@/lib/types"
import { AppHeader } from "@/components/app-header"

export default function ReviewPage() {
  const [tracks, setTracks] = useState(mockTracksForReview)
  const [selectedIndex, setSelectedIndex] = useState(0)
  const [editedMetadata, setEditedMetadata] = useState<Record<string, Partial<TrackImport["metadata"]>>>({})

  const selectedTrack = tracks[selectedIndex]
  const currentEdits = editedMetadata[selectedTrack?.id] || {}

  const handleNext = () => {
    if (selectedIndex < tracks.length - 1) {
      setSelectedIndex(selectedIndex + 1)
    }
  }

  const handlePrev = () => {
    if (selectedIndex > 0) {
      setSelectedIndex(selectedIndex - 1)
    }
  }

  const handleAcceptSuggestion = () => {
    if (!selectedTrack?.suggestedMetadata) return

    setEditedMetadata((prev) => ({
      ...prev,
      [selectedTrack.id]: {
        ...prev[selectedTrack.id],
        ...selectedTrack.suggestedMetadata,
      },
    }))
  }

  const handleFieldChange = (field: string, value: string | number) => {
    setEditedMetadata((prev) => ({
      ...prev,
      [selectedTrack.id]: {
        ...prev[selectedTrack.id],
        [field]: value,
      },
    }))
  }

  const handleApprove = () => {
    // In a real app, this would save the changes and move to the next track
    setTracks((prev) => prev.filter((_, i) => i !== selectedIndex))
    if (selectedIndex >= tracks.length - 1 && selectedIndex > 0) {
      setSelectedIndex(selectedIndex - 1)
    }
  }

  const handleSkip = () => {
    handleNext()
  }

  const handleDelete = () => {
    setTracks((prev) => prev.filter((_, i) => i !== selectedIndex))
    if (selectedIndex >= tracks.length - 1 && selectedIndex > 0) {
      setSelectedIndex(selectedIndex - 1)
    }
  }

  const getValue = (field: keyof NonNullable<TrackImport["metadata"]>) => {
    if (currentEdits[field] !== undefined) return currentEdits[field]
    if (selectedTrack?.metadata?.[field] !== undefined) return selectedTrack.metadata[field]
    return ""
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
              <Button asChild>
                <a href="/overview">Back to Overview</a>
              </Button>
            </CardContent>
          </Card>
        </main>
      </div>
    )
  }

  return (
    <div className="flex min-h-screen flex-col bg-background">
      <AppHeader />

      <main className="flex flex-1 flex-col overflow-hidden p-4 md:p-6">
        <div className="mx-auto flex w-full max-w-7xl flex-1 flex-col gap-4 overflow-hidden">
          {/* Header */}
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h1 className="text-2xl font-bold md:text-3xl">Manual Review</h1>
              <p className="text-muted-foreground">
                Review and correct track metadata
              </p>
            </div>
            <div className="flex items-center gap-2">
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

          {/* Main Content */}
          <div className="grid flex-1 gap-4 overflow-hidden lg:grid-cols-2">
            {/* Left Panel - Track List */}
            <Card className="flex flex-col overflow-hidden lg:row-span-1">
              <div className="border-b border-border p-3">
                <h2 className="font-medium">Pending Review</h2>
              </div>
              <ScrollArea className="flex-1">
                <div className="space-y-1 p-2">
                  {tracks.map((track, index) => (
                    <button
                      key={track.id}
                      onClick={() => setSelectedIndex(index)}
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
                          {track.metadata?.title || "Unknown"}
                        </p>
                        <p className="truncate text-xs text-muted-foreground">
                          {track.originalPath.split("/").pop()}
                        </p>
                      </div>
                      {track.issues && track.issues.length > 0 && (
                        <Badge variant="outline" className="shrink-0 gap-1 text-amber-400 border-amber-400/30">
                          <AlertTriangle className="size-3" />
                          {track.issues.length}
                        </Badge>
                      )}
                    </button>
                  ))}
                </div>
              </ScrollArea>
            </Card>

            {/* Right Panel - Edit Form */}
            <Card className="flex flex-col overflow-hidden">
              <Tabs defaultValue="edit" className="flex flex-1 flex-col overflow-hidden">
                <div className="border-b border-border px-3">
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

                <TabsContent value="edit" className="flex-1 overflow-hidden m-0">
                  <ScrollArea className="h-full">
                    <div className="space-y-4 p-4">
                      {/* Album Art & Basic Info */}
                      <div className="flex gap-4">
                        <div className="size-24 shrink-0 overflow-hidden rounded-lg bg-secondary">
                          {selectedTrack?.suggestedMetadata?.albumArt || currentEdits.albumArt ? (
                            <img
                              src={currentEdits.albumArt as string || selectedTrack?.suggestedMetadata?.albumArt}
                              alt="Album art"
                              className="size-full object-cover"
                              crossOrigin="anonymous"
                            />
                          ) : (
                            <div className="flex size-full items-center justify-center">
                              <Music className="size-8 text-muted-foreground" />
                            </div>
                          )}
                        </div>
                        <div className="min-w-0 flex-1 space-y-2">
                          <div>
                            <Label htmlFor="title" className="text-xs text-muted-foreground">
                              Title
                            </Label>
                            <Input
                              id="title"
                              value={getValue("title")}
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
                              value={getValue("artist")}
                              onChange={(e) => handleFieldChange("artist", e.target.value)}
                              className="h-8"
                            />
                          </div>
                        </div>
                      </div>

                      {/* More fields */}
                      <div className="grid gap-4 sm:grid-cols-2">
                        <div>
                          <Label htmlFor="album" className="text-xs text-muted-foreground">
                            Album
                          </Label>
                          <Input
                            id="album"
                            value={getValue("album")}
                            onChange={(e) => handleFieldChange("album", e.target.value)}
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
                            value={getValue("year")}
                            onChange={(e) => handleFieldChange("year", parseInt(e.target.value) || 0)}
                            className="h-8"
                          />
                        </div>
                        <div>
                          <Label htmlFor="genre" className="text-xs text-muted-foreground">
                            Genre
                          </Label>
                          <Select
                            value={getValue("genre") as string}
                            onValueChange={(value) => handleFieldChange("genre", value)}
                          >
                            <SelectTrigger className="h-8">
                              <SelectValue placeholder="Select genre" />
                            </SelectTrigger>
                            <SelectContent>
                              <SelectItem value="Rock">Rock</SelectItem>
                              <SelectItem value="Pop">Pop</SelectItem>
                              <SelectItem value="Electronic">Electronic</SelectItem>
                              <SelectItem value="Jazz">Jazz</SelectItem>
                              <SelectItem value="Classical">Classical</SelectItem>
                              <SelectItem value="Hip Hop">Hip Hop</SelectItem>
                              <SelectItem value="R&B">R&B</SelectItem>
                              <SelectItem value="Country">Country</SelectItem>
                              <SelectItem value="Metal">Metal</SelectItem>
                              <SelectItem value="Alternative Rock">Alternative Rock</SelectItem>
                              <SelectItem value="Progressive Rock">Progressive Rock</SelectItem>
                              <SelectItem value="Unknown">Unknown</SelectItem>
                            </SelectContent>
                          </Select>
                        </div>
                        <div>
                          <Label className="text-xs text-muted-foreground">Format</Label>
                          <Input
                            value={selectedTrack?.metadata?.format || ""}
                            disabled
                            className="h-8 bg-secondary"
                          />
                        </div>
                      </div>

                      {/* File Info */}
                      <div className="rounded-lg bg-secondary/50 p-3">
                        <p className="mb-2 text-xs font-medium text-muted-foreground">
                          Original File
                        </p>
                        <p className="break-all text-sm">{selectedTrack?.originalPath}</p>
                      </div>

                      {/* Fingerprint */}
                      {selectedTrack?.fingerprint && (
                        <div className="flex items-center gap-2 rounded-lg bg-secondary/50 p-3">
                          <Fingerprint className="size-4 text-muted-foreground" />
                          <code className="text-xs text-muted-foreground">
                            {selectedTrack.fingerprint}
                          </code>
                        </div>
                      )}
                    </div>
                  </ScrollArea>
                </TabsContent>

                <TabsContent value="compare" className="flex-1 overflow-hidden m-0">
                  <ScrollArea className="h-full">
                    <div className="p-4">
                      {selectedTrack?.suggestedMetadata ? (
                        <div className="space-y-4">
                          <div className="flex items-center justify-between">
                            <h3 className="font-medium">Suggested Match</h3>
                            <Button size="sm" onClick={handleAcceptSuggestion} className="gap-2">
                              <Check className="size-4" />
                              Accept All
                            </Button>
                          </div>

                          <div className="space-y-3">
                            <CompareRow
                              label="Title"
                              current={selectedTrack.metadata?.title}
                              suggested={selectedTrack.suggestedMetadata.title}
                            />
                            <CompareRow
                              label="Artist"
                              current={selectedTrack.metadata?.artist}
                              suggested={selectedTrack.suggestedMetadata.artist}
                            />
                            <CompareRow
                              label="Album"
                              current={selectedTrack.metadata?.album}
                              suggested={selectedTrack.suggestedMetadata.album}
                            />
                            <CompareRow
                              label="Year"
                              current={selectedTrack.metadata?.year?.toString()}
                              suggested={selectedTrack.suggestedMetadata.year?.toString()}
                            />
                            <CompareRow
                              label="Genre"
                              current={selectedTrack.metadata?.genre}
                              suggested={selectedTrack.suggestedMetadata.genre}
                            />
                          </div>
                        </div>
                      ) : (
                        <div className="flex flex-col items-center justify-center py-12 text-center">
                          <RefreshCw className="mb-4 size-12 text-muted-foreground" />
                          <h3 className="font-medium">No Suggestions</h3>
                          <p className="text-sm text-muted-foreground">
                            No automatic match was found for this track
                          </p>
                        </div>
                      )}
                    </div>
                  </ScrollArea>
                </TabsContent>

                <TabsContent value="issues" className="flex-1 overflow-hidden m-0">
                  <ScrollArea className="h-full">
                    <div className="p-4">
                      {selectedTrack?.issues && selectedTrack.issues.length > 0 ? (
                        <div className="space-y-2">
                          {selectedTrack.issues.map((issue, index) => (
                            <div
                              key={index}
                              className="flex items-start gap-3 rounded-lg bg-amber-400/10 p-3"
                            >
                              <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" />
                              <p className="text-sm">{issue}</p>
                            </div>
                          ))}
                        </div>
                      ) : (
                        <div className="flex flex-col items-center justify-center py-12 text-center">
                          <Check className="mb-4 size-12 text-primary" />
                          <h3 className="font-medium">No Issues</h3>
                          <p className="text-sm text-muted-foreground">
                            This track has no detected issues
                          </p>
                        </div>
                      )}
                    </div>
                  </ScrollArea>
                </TabsContent>
              </Tabs>

              {/* Action Buttons */}
              <div className="flex flex-wrap gap-2 border-t border-border p-4">
                <Button onClick={handleApprove} className="flex-1 gap-2 sm:flex-none">
                  <Save className="size-4" />
                  Approve & Copy
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
                        This will remove the track from the import queue. The original file will not be deleted.
                      </AlertDialogDescription>
                    </AlertDialogHeader>
                    <AlertDialogFooter>
                      <AlertDialogCancel>Cancel</AlertDialogCancel>
                      <AlertDialogAction onClick={handleDelete}>Delete</AlertDialogAction>
                    </AlertDialogFooter>
                  </AlertDialogContent>
                </AlertDialog>
              </div>
            </Card>
          </div>
        </div>
      </main>
    </div>
  )
}

function CompareRow({
  label,
  current,
  suggested,
}: {
  label: string
  current?: string
  suggested?: string
}) {
  const isDifferent = current !== suggested && suggested

  return (
    <div className="rounded-lg border border-border p-3">
      <p className="mb-2 text-xs font-medium text-muted-foreground">{label}</p>
      <div className="flex items-center gap-3">
        <div className="min-w-0 flex-1">
          <p className={`truncate text-sm ${isDifferent ? "text-muted-foreground line-through" : ""}`}>
            {current || "—"}
          </p>
        </div>
        {isDifferent && (
          <>
            <ArrowRight className="size-4 shrink-0 text-primary" />
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium text-primary">{suggested}</p>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
