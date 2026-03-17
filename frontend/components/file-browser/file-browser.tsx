"use client"

import { useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from "react"
import {
  ResizablePanelGroup,
  ResizablePanel,
  ResizableHandle,
} from "@/components/ui/resizable"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Sheet, SheetContent, SheetTitle, SheetDescription } from "@/components/ui/sheet"
import { Button } from "@/components/ui/button"
import { FolderTree } from "./folder-tree"
import { FileGrid } from "./file-grid"
import { BreadcrumbNav } from "./breadcrumb-nav"
import { TrackDetails } from "./track-details"
import { Toolbar } from "./toolbar"
import { findFileById, getPathToFile } from "@/lib/mock-data"
import type { FileItem } from "@/lib/types"
import { FolderOpen, Menu } from "lucide-react"
import { cn } from "@/lib/utils"
import { useIsMobile } from "@/hooks/use-mobile"
import { AppHeader } from "../app-header"
import {
  buildFileSystemFromSongs,
  fetchSongs,
  type ApiSong,
  type LibraryPathMode,
} from "@/lib/api-client"
import { isDemoMode } from "@/lib/app-mode"
import { usePlayer } from "@/lib/player-context"

type LibrarySortBy = "name" | "dateModified" | "size" | "type"
type LibrarySortDirection = "asc" | "desc"
type LibraryFilterBy = "all" | "audio" | "folders" | "pendingEnrichment"

const EMPTY_LIBRARY: FileItem[] = [
  {
    id: "root",
    name: "music",
    type: "folder",
    path: "/Volumes/music",
    parentId: null,
    children: [],
  },
]

function countAudioFiles(items: FileItem[]): number {
  let count = 0
  for (const item of items) {
    if (item.type === "audio") {
      count++
    }
    if (item.children && item.children.length > 0) {
      count += countAudioFiles(item.children)
    }
  }
  return count
}

interface SearchableItem {
  item: FileItem
  searchText: string
}

function buildSearchIndex(items: FileItem[]): SearchableItem[] {
  const result: SearchableItem[] = []
  const stack = [...items]

  while (stack.length > 0) {
    const item = stack.pop()
    if (!item) continue

    const parts = [item.name, item.path]
    if (item.type === "audio" && item.metadata) {
      parts.push(
        item.metadata.title,
        item.metadata.artist,
        item.metadata.album,
        item.metadata.format
      )
    }
    result.push({ item, searchText: parts.join("\0").toLowerCase() })

    if (item.type === "folder" && item.children?.length) {
      stack.push(...item.children)
    }
  }

  return result
}

function getAggregateSize(item: FileItem): number {
  if (item.type === "audio") {
    return item.metadata?.fileSize ?? 0
  }

  let totalSize = 0
  const stack = [...(item.children ?? [])]
  while (stack.length > 0) {
    const child = stack.pop()
    if (!child) continue
    if (child.type === "audio") {
      totalSize += child.metadata?.fileSize ?? 0
      continue
    }

    if (child.children?.length) {
      stack.push(...child.children)
    }
  }

  return totalSize
}

function getRecencyValue(item: FileItem): number {
  if (item.type === "audio") {
    const songId = Number(item.id.replace("song:", ""))
    return Number.isFinite(songId) ? songId : 0
  }

  let newestSongId = 0
  const stack = [...(item.children ?? [])]
  while (stack.length > 0) {
    const child = stack.pop()
    if (!child) continue
    if (child.type === "audio") {
      const parsedSongId = Number(child.id.replace("song:", ""))
      if (Number.isFinite(parsedSongId)) {
        newestSongId = Math.max(newestSongId, parsedSongId)
      }
      continue
    }
    if (child.children?.length) {
      stack.push(...child.children)
    }
  }

  return newestSongId
}

export function FileBrowser() {
  const isMountedRef = useRef(true)
  const [songs, setSongs] = useState<ApiSong[]>([])
  const [libraryMode, setLibraryMode] = useState<LibraryPathMode>("destination")
  const [currentFolderId, setCurrentFolderId] = useState<string>("root")
  const [selectedFileId, setSelectedFileId] = useState<string | null>(null)
  const [viewMode, setViewMode] = useState<"grid" | "list">("grid")
  const [searchQuery, setSearchQuery] = useState("")
  const [sortBy, setSortBy] = useState<LibrarySortBy>("name")
  const [sortDirection, setSortDirection] = useState<LibrarySortDirection>("asc")
  const [filterBy, setFilterBy] = useState<LibraryFilterBy>("all")
  const [showDetails, setShowDetails] = useState(false)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [apiError, setApiError] = useState<string | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [isRefreshing, setIsRefreshing] = useState(false)
  const [isHydrated, setIsHydrated] = useState(false)
  // Track expanded folder IDs for the sidebar tree - managed here to persist across re-renders
  const [expandedFolderIds, setExpandedFolderIds] = useState<Set<string>>(() => new Set(["root"]))
  const isMobile = useIsMobile()
  const { currentSong } = usePlayer()
  const dataModeMessage = isDemoMode
    ? "Demo mode is enabled. Library data is served from fake data."
    : "Production mode is enabled. Library data is served from the API."
  const fileSystem = useMemo(
    () => (songs.length > 0 ? buildFileSystemFromSongs(songs, libraryMode) : EMPTY_LIBRARY),
    [songs, libraryMode]
  )

  useEffect(() => {
    if (typeof window !== "undefined") {
      const stored = localStorage.getItem("musichoarder-library-view") as "grid" | "list" | null
      if (stored === "grid" || stored === "list") setViewMode(stored)
    }
    setIsHydrated(true)
  }, [])

  useEffect(() => {
    if (!isHydrated || typeof window === "undefined") return
    localStorage.setItem("musichoarder-library-view", viewMode)
  }, [viewMode, isHydrated])

  const loadSongs = useCallback(async (mode: "initial" | "refresh") => {
    try {
      if (mode === "initial") {
        setIsLoading(true)
      } else {
        setIsRefreshing(true)
      }

      const loadedSongs = await fetchSongs()
      if (!isMountedRef.current) return
      setSongs(loadedSongs)
      setApiError(null)
    } catch (error) {
      if (!isMountedRef.current) return
      setSongs([])
      const message = error instanceof Error ? error.message : "Unknown API error"
      setApiError(`Unable to load library data from API. ${message}`)
    } finally {
      if (!isMountedRef.current) return
      if (mode === "initial") {
        setIsLoading(false)
      } else {
        setIsRefreshing(false)
      }
    }
  }, [])

  useEffect(() => {
    isMountedRef.current = true
    void loadSongs("initial")

    return () => {
      isMountedRef.current = false
    }
  }, [loadSongs])

  useEffect(() => {
    setCurrentFolderId("root")
    setSelectedFileId(null)
    setShowDetails(false)
    // Reset expanded folders when switching library mode
    setExpandedFolderIds(new Set(["root"]))
  }, [libraryMode])

  const currentFolder = useMemo(
    () => findFileById(fileSystem, currentFolderId),
    [fileSystem, currentFolderId]
  )

  useEffect(() => {
    if (currentFolder || currentFolderId === "root") return

    setCurrentFolderId("root")
    setSelectedFileId(null)
    setShowDetails(false)
  }, [currentFolder, currentFolderId])

  const breadcrumbPath = useMemo(
    () => getPathToFile(fileSystem, currentFolderId),
    [fileSystem, currentFolderId]
  )

  const selectedFile = useMemo(
    () => (selectedFileId ? findFileById(fileSystem, selectedFileId) : null),
    [fileSystem, selectedFileId]
  )

  const expectedSongCount = useMemo(
    () =>
      libraryMode === "destination"
        ? songs.filter((song) => Boolean(song.destinationPath?.trim())).length
        : songs.length,
    [songs, libraryMode]
  )

  const mappedSongCount = useMemo(() => countAudioFiles(fileSystem), [fileSystem])

  const coverageWarning = useMemo(() => {
    if (apiError || isLoading || expectedSongCount === 0) return null
    if (mappedSongCount === expectedSongCount) return null
    return `Loaded ${mappedSongCount} of ${expectedSongCount} ${libraryMode} songs. Some songs could not be mapped to folders.`
  }, [apiError, isLoading, expectedSongCount, mappedSongCount, libraryMode])

  const deferredSearchQuery = useDeferredValue(searchQuery)

  const searchIndex = useMemo(
    () => buildSearchIndex(currentFolder?.children ?? []),
    [currentFolder]
  )

  const visibleItems = useMemo(() => {
    const folderItems = currentFolder?.children ?? []
    const query = deferredSearchQuery.trim().toLowerCase()

    const searchedItems =
      query.length === 0
        ? folderItems
        : searchIndex
            .filter((entry) => entry.searchText.includes(query))
            .map((entry) => entry.item)

    const filteredItems = searchedItems.filter((item) => {
      switch (filterBy) {
        case "audio":
          return item.type === "audio"
        case "folders":
          return item.type === "folder"
        case "pendingEnrichment":
          return item.type === "audio" && item.metadata?.enrichmentStatus === "pending"
        case "all":
        default:
          return true
      }
    })

    const sortedItems = filteredItems.toSorted((left, right) => {
      if (left.type !== right.type) {
        return left.type === "folder" ? -1 : 1
      }

      let result = 0
      switch (sortBy) {
        case "size":
          result = getAggregateSize(left) - getAggregateSize(right)
          break
        case "type":
          result = left.type.localeCompare(right.type)
          break
        case "dateModified":
          result = getRecencyValue(left) - getRecencyValue(right)
          break
        case "name":
        default:
          result = left.name.localeCompare(right.name, undefined, { sensitivity: "base" })
          break
      }

      if (result === 0) {
        result = left.name.localeCompare(right.name, undefined, { sensitivity: "base" })
      }
      if (result === 0) {
        result = left.id.localeCompare(right.id)
      }

      return sortDirection === "asc" ? result : -result
    })

    return sortedItems
  }, [currentFolder, filterBy, deferredSearchQuery, searchIndex, sortBy, sortDirection])

  const isSearchPending = deferredSearchQuery !== searchQuery

  const emptyStateMessage = useMemo(() => {
    if (deferredSearchQuery.trim().length > 0) {
      return "No files match your search in this folder tree"
    }
    if (filterBy !== "all") {
      return "No files match the selected filter"
    }
    return "This folder is empty"
  }, [filterBy, deferredSearchQuery])

  const handleFolderSelect = (item: FileItem) => {
    if (item.type === "folder") {
      setCurrentFolderId(item.id)
      setSelectedFileId(null)
      setShowDetails(false)
      setSidebarOpen(false)
    }
  }

  const handleFileSelect = (item: FileItem) => {
    setSelectedFileId(item.id)
    if (item.type === "audio") {
      setShowDetails(true)
    }
  }

  const handleFileOpen = (item: FileItem) => {
    if (item.type === "folder") {
      setCurrentFolderId(item.id)
      setSelectedFileId(null)
      setShowDetails(false)
    } else {
      setSelectedFileId(item.id)
      setShowDetails(true)
    }
  }

  const handleNavigate = (item: FileItem) => {
    setCurrentFolderId(item.id)
    setSelectedFileId(null)
    setShowDetails(false)
  }

  // Calculate library size for sidebar
  const libraryStats = useMemo(() => {
    let totalTracks = 0
    let totalSize = 0

    function countFiles(items: FileItem[]) {
      for (const item of items) {
        if (item.type === "audio" && item.metadata) {
          totalTracks++
          totalSize += item.metadata.fileSize
        }
        if (item.children) countFiles(item.children)
      }
    }

    countFiles(fileSystem)
    return { totalTracks, totalSize }
  }, [fileSystem])

  // Sidebar content - uses external expanded state to persist across selections
  const sidebarContent = (
    <div className="flex h-full min-h-0 flex-col bg-sidebar">
      <div className="border-b border-sidebar-border px-4 py-3">
        <h2 className="text-sm font-medium text-muted-foreground">
          {libraryMode === "destination" ? "Destination Library" : "Source Library"}
        </h2>
      </div>
      <ScrollArea className="min-h-0 flex-1 p-2">
        <FolderTree
          items={fileSystem}
          selectedId={currentFolderId}
          onSelect={handleFolderSelect}
          expandedIds={expandedFolderIds}
          onExpandedChange={setExpandedFolderIds}
        />
      </ScrollArea>

      {/* Sidebar Footer */}
      <div className="border-t border-sidebar-border p-4">
        <div className="rounded-lg bg-sidebar-accent p-3">
          <div className="flex items-center gap-2 text-sm">
            <FolderOpen className="size-4 text-primary" />
            <span className="font-medium">Library Size</span>
          </div>
          <p className="mt-1 text-2xl font-bold">
            {(libraryStats.totalSize / (1024 * 1024 * 1024)).toFixed(1)} GB
          </p>
          <p className="text-xs text-muted-foreground">{libraryStats.totalTracks} tracks total</p>
        </div>
      </div>
    </div>
  )

  return (
    <div className={cn("flex h-screen flex-col bg-background", currentSong && "pb-[72px]")}>
      <AppHeader />

      {/* Mobile sidebar sheet */}
      <Sheet open={sidebarOpen} onOpenChange={setSidebarOpen}>
        <SheetContent side="left" className="w-72 p-0">
          <SheetTitle className="sr-only">Library Navigation</SheetTitle>
          <SheetDescription className="sr-only">Browse your music library folders</SheetDescription>
          {sidebarContent}
        </SheetContent>
      </Sheet>

      {/* Mobile Details Sheet */}
      {isMobile && (
        <Sheet open={showDetails && !!selectedFile} onOpenChange={(open) => !open && setShowDetails(false)}>
          <SheetContent side="bottom" className="h-[85vh] p-0 [&>button]:hidden">
            <SheetTitle className="sr-only">Track Details</SheetTitle>
            <SheetDescription className="sr-only">View track metadata, lyrics, and sources</SheetDescription>
            {selectedFile?.type === "audio" && (
              <TrackDetails
                file={selectedFile}
                onClose={() => setShowDetails(false)}
                onResetEnrichment={() => void loadSongs("refresh")}
              />
            )}
          </SheetContent>
        </Sheet>
      )}

      {/* Main Content - Desktop with resizable panels */}
      {!isHydrated || !isMobile ? (
        <ResizablePanelGroup id="library-browser-panels" direction="horizontal" className="flex-1">
          {/* Sidebar */}
          <ResizablePanel id="library-sidebar-panel" order={1} defaultSize={20} minSize={15} maxSize={30}>
            {sidebarContent}
          </ResizablePanel>

          <ResizableHandle />

          {/* Main Panel */}
          <ResizablePanel id="library-main-panel" order={2} defaultSize={showDetails ? 50 : 80}>
            <div className="flex h-full min-h-0 flex-col">
              {/* Breadcrumb */}
              <div className="border-b border-border bg-card/30 px-4 py-2">
                <BreadcrumbNav path={breadcrumbPath} onNavigate={handleNavigate} />
              </div>

              {/* Toolbar */}
              <Toolbar
                viewMode={viewMode}
                onViewModeChange={setViewMode}
                searchQuery={searchQuery}
                onSearchChange={setSearchQuery}
                sortBy={sortBy}
                sortDirection={sortDirection}
                filterBy={filterBy}
                onSortByChange={setSortBy}
                onSortDirectionChange={setSortDirection}
                onFilterByChange={setFilterBy}
                libraryMode={libraryMode}
                onLibraryModeChange={setLibraryMode}
                onRefresh={() => void loadSongs("refresh")}
                isRefreshing={isRefreshing}
              />
              <div className="border-b border-border bg-card/30 px-4 py-2 text-xs text-muted-foreground">
                {dataModeMessage}
              </div>
              {isLoading && (
                <div className="border-b border-border bg-card/30 px-4 py-2 text-xs text-muted-foreground">
                  {isDemoMode ? "Loading demo library data..." : "Loading library from API..."}
                </div>
              )}
              {coverageWarning && (
                <div className="border-b border-border bg-card/30 px-4 py-2 text-xs text-muted-foreground">
                  {coverageWarning}
                </div>
              )}
              {apiError && (
                <div className="border-b border-border bg-card/30 px-4 py-2 text-xs text-muted-foreground">
                  {apiError}
                </div>
              )}

              {/* File Grid */}
              <div className={cn("min-h-0 flex-1", isSearchPending && "opacity-60 transition-opacity")}>
                <FileGrid
                  items={visibleItems}
                  selectedId={selectedFileId}
                  onSelect={handleFileSelect}
                  onOpen={handleFileOpen}
                  viewMode={viewMode}
                  emptyMessage={emptyStateMessage}
                />
              </div>

              {/* Status Bar */}
              <div className="flex items-center justify-between border-t border-border bg-card/30 px-4 py-1.5 text-xs text-muted-foreground">
                <span>
                  {visibleItems.length} items ({libraryMode})
                </span>
                {selectedFile && (
                  <span className="truncate max-w-[200px]">
                    Selected: {selectedFile.name}
                  </span>
                )}
              </div>
            </div>
          </ResizablePanel>

          {/* Details Panel - Desktop only */}
          {showDetails && selectedFile?.type === "audio" && (
            <>
              <ResizableHandle />
              <ResizablePanel id="library-details-panel" order={3} defaultSize={30} minSize={25} maxSize={40}>
                <TrackDetails
                  file={selectedFile}
                  onClose={() => setShowDetails(false)}
                  onResetEnrichment={() => void loadSongs("refresh")}
                />
              </ResizablePanel>
            </>
          )}
        </ResizablePanelGroup>
      ) : (
        /* Mobile Layout - No resizable panels */
        <div className="flex flex-1 min-h-0 flex-col overflow-hidden">
          {/* Breadcrumb with menu button */}
          <div className="flex items-center gap-2 border-b border-border bg-card/30 px-3 py-2">
            <Button variant="ghost" size="icon" className="size-8 shrink-0" onClick={() => setSidebarOpen(true)}>
              <Menu className="size-4" />
            </Button>
            <BreadcrumbNav path={breadcrumbPath} onNavigate={handleNavigate} />
          </div>

          {/* Toolbar */}
          <Toolbar
            viewMode={viewMode}
            onViewModeChange={setViewMode}
            searchQuery={searchQuery}
            onSearchChange={setSearchQuery}
            sortBy={sortBy}
            sortDirection={sortDirection}
            filterBy={filterBy}
            onSortByChange={setSortBy}
            onSortDirectionChange={setSortDirection}
            onFilterByChange={setFilterBy}
            libraryMode={libraryMode}
            onLibraryModeChange={setLibraryMode}
            onRefresh={() => void loadSongs("refresh")}
            isRefreshing={isRefreshing}
          />
          <div className="border-b border-border bg-card/30 px-3 py-2 text-xs text-muted-foreground">
            {dataModeMessage}
          </div>
          {isLoading && (
            <div className="border-b border-border bg-card/30 px-3 py-2 text-xs text-muted-foreground">
              {isDemoMode ? "Loading demo library data..." : "Loading library from API..."}
            </div>
          )}
          {coverageWarning && (
            <div className="border-b border-border bg-card/30 px-3 py-2 text-xs text-muted-foreground">
              {coverageWarning}
            </div>
          )}
          {apiError && (
            <div className="border-b border-border bg-card/30 px-3 py-2 text-xs text-muted-foreground">
              {apiError}
            </div>
          )}

          {/* File Grid */}
          <div className={cn("min-h-0 flex-1", isSearchPending && "opacity-60 transition-opacity")}>
            <FileGrid
              items={visibleItems}
              selectedId={selectedFileId}
              onSelect={handleFileSelect}
              onOpen={handleFileOpen}
              viewMode={viewMode}
              emptyMessage={emptyStateMessage}
            />
          </div>

          {/* Status Bar */}
          <div className="flex items-center justify-between border-t border-border bg-card/30 px-3 py-1.5 text-xs text-muted-foreground">
            <span>
              {visibleItems.length} items ({libraryMode})
            </span>
            {selectedFile && (
              <span className="truncate max-w-[150px]">
                {selectedFile.name}
              </span>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
