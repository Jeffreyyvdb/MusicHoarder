"use client"

import { useEffect, useMemo, useState } from "react"
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
import { useIsMobile } from "@/hooks/use-mobile"
import { AppHeader } from "../app-header"
import {
  buildFileSystemFromSongs,
  fetchSongs,
  type ApiSong,
  type LibraryPathMode,
} from "@/lib/api-client"

const EMPTY_LIBRARY: FileItem[] = [
  {
    id: "root",
    name: "Music Library",
    type: "folder",
    path: "/Music Library",
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

export function FileBrowser() {
  const [songs, setSongs] = useState<ApiSong[]>([])
  const [libraryMode, setLibraryMode] = useState<LibraryPathMode>("destination")
  const [currentFolderId, setCurrentFolderId] = useState<string>("root")
  const [selectedFileId, setSelectedFileId] = useState<string | null>(null)
  const [viewMode, setViewMode] = useState<"grid" | "list">("grid")
  const [searchQuery, setSearchQuery] = useState("")
  const [showDetails, setShowDetails] = useState(false)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [apiError, setApiError] = useState<string | null>(null)
  const [isLoading, setIsLoading] = useState(true)
  const [isHydrated, setIsHydrated] = useState(false)
  const isMobile = useIsMobile()
  const fileSystem = useMemo(
    () => (songs.length > 0 ? buildFileSystemFromSongs(songs, libraryMode) : EMPTY_LIBRARY),
    [songs, libraryMode]
  )

  useEffect(() => {
    setIsHydrated(true)
  }, [])

  useEffect(() => {
    let active = true

    const loadSongs = async () => {
      try {
        setIsLoading(true)
        const loadedSongs = await fetchSongs()
        if (!active) return
        setSongs(loadedSongs)
        setApiError(null)
      } catch (error) {
        if (!active) return
        setSongs([])
        const message = error instanceof Error ? error.message : "Unknown API error"
        setApiError(`Unable to load library from API. ${message}`)
      } finally {
        if (!active) return
        setIsLoading(false)
      }
    }

    loadSongs()

    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    setCurrentFolderId("root")
    setSelectedFileId(null)
    setShowDetails(false)
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

  const filteredItems = useMemo(() => {
    const items = currentFolder?.children || []
    if (!searchQuery) return items
    return items.filter((item) =>
      item.name.toLowerCase().includes(searchQuery.toLowerCase())
    )
  }, [currentFolder, searchQuery])

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

  // Sidebar content component for reuse
  const SidebarContent = () => (
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
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />

      {/* Mobile sidebar sheet */}
      <Sheet open={sidebarOpen} onOpenChange={setSidebarOpen}>
        <SheetContent side="left" className="w-72 p-0">
          <SheetTitle className="sr-only">Library Navigation</SheetTitle>
          <SheetDescription className="sr-only">Browse your music library folders</SheetDescription>
          <SidebarContent />
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
            <SidebarContent />
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
                libraryMode={libraryMode}
                onLibraryModeChange={setLibraryMode}
              />
              {isLoading && (
                <div className="border-b border-border bg-card/30 px-4 py-2 text-xs text-muted-foreground">
                  Loading library from API...
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
              <ScrollArea className="min-h-0 flex-1">
                <FileGrid
                  items={filteredItems}
                  selectedId={selectedFileId}
                  onSelect={handleFileSelect}
                  onOpen={handleFileOpen}
                  viewMode={viewMode}
                />
              </ScrollArea>

              {/* Status Bar */}
              <div className="flex items-center justify-between border-t border-border bg-card/30 px-4 py-1.5 text-xs text-muted-foreground">
                <span>
                  {filteredItems.length} items ({libraryMode})
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
            libraryMode={libraryMode}
            onLibraryModeChange={setLibraryMode}
          />
          {isLoading && (
            <div className="border-b border-border bg-card/30 px-3 py-2 text-xs text-muted-foreground">
              Loading library from API...
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
          <ScrollArea className="min-h-0 flex-1">
            <FileGrid
              items={filteredItems}
              selectedId={selectedFileId}
              onSelect={handleFileSelect}
              onOpen={handleFileOpen}
              viewMode={viewMode}
            />
          </ScrollArea>

          {/* Status Bar */}
          <div className="flex items-center justify-between border-t border-border bg-card/30 px-3 py-1.5 text-xs text-muted-foreground">
            <span>
              {filteredItems.length} items ({libraryMode})
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
