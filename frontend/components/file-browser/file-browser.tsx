"use client"

import { useEffect, useMemo, useState } from "react"
import {
  ResizablePanelGroup,
  ResizablePanel,
  ResizableHandle,
} from "@/components/ui/resizable"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Sheet, SheetContent, SheetTrigger, SheetTitle, SheetDescription } from "@/components/ui/sheet"
import { Button } from "@/components/ui/button"
import { FolderTree } from "./folder-tree"
import { FileGrid } from "./file-grid"
import { BreadcrumbNav } from "./breadcrumb-nav"
import { TrackDetails } from "./track-details"
import { Toolbar } from "./toolbar"
import { mockFileSystem, findFileById, getPathToFile } from "@/lib/mock-data"
import type { FileItem } from "@/lib/types"
import { FolderOpen, Menu } from "lucide-react"
import { useIsMobile } from "@/hooks/use-mobile"
import { AppHeader } from "@/components/app-header"
import { buildFileSystemFromSongs, fetchSongs } from "@/lib/api-client"

export function FileBrowser() {
  const [fileSystem, setFileSystem] = useState<FileItem[]>(mockFileSystem)
  const [currentFolderId, setCurrentFolderId] = useState<string>("root")
  const [selectedFileId, setSelectedFileId] = useState<string | null>(null)
  const [viewMode, setViewMode] = useState<"grid" | "list">("grid")
  const [searchQuery, setSearchQuery] = useState("")
  const [showDetails, setShowDetails] = useState(false)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [apiError, setApiError] = useState<string | null>(null)
  const isMobile = useIsMobile()

  useEffect(() => {
    let active = true

    const loadSongs = async () => {
      try {
        const songs = await fetchSongs()
        if (!active) return
        setFileSystem(buildFileSystemFromSongs(songs))
        setApiError(null)
      } catch {
        if (!active) return
        setApiError("Using mock library data because API is currently unavailable.")
      }
    }

    loadSongs()
  }, [])

  const currentFolder = useMemo(
    () => findFileById(fileSystem, currentFolderId),
    [fileSystem, currentFolderId]
  )

  const breadcrumbPath = useMemo(
    () => getPathToFile(fileSystem, currentFolderId),
    [fileSystem, currentFolderId]
  )

  const selectedFile = useMemo(
    () => (selectedFileId ? findFileById(fileSystem, selectedFileId) : null),
    [fileSystem, selectedFileId]
  )

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
    <div className="flex h-full flex-col bg-sidebar">
      <div className="border-b border-sidebar-border px-4 py-3">
        <h2 className="text-sm font-medium text-muted-foreground">Library</h2>
      </div>
      <ScrollArea className="flex-1 p-2">
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
          <p className="text-xs text-muted-foreground">
            {libraryStats.totalTracks} tracks total
          </p>
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
      {!isMobile ? (
        <ResizablePanelGroup direction="horizontal" className="flex-1">
          {/* Sidebar */}
          <ResizablePanel defaultSize={20} minSize={15} maxSize={30}>
            <SidebarContent />
          </ResizablePanel>

          <ResizableHandle />

          {/* Main Panel */}
          <ResizablePanel defaultSize={showDetails ? 50 : 80}>
            <div className="flex h-full flex-col">
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
              />
              {apiError && (
                <div className="border-b border-border bg-card/30 px-4 py-2 text-xs text-muted-foreground">
                  {apiError}
                </div>
              )}

              {/* File Grid */}
              <ScrollArea className="flex-1">
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
                <span>{filteredItems.length} items</span>
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
              <ResizablePanel defaultSize={30} minSize={25} maxSize={40}>
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
        <div className="flex flex-1 flex-col overflow-hidden">
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
          />
          {apiError && (
            <div className="border-b border-border bg-card/30 px-3 py-2 text-xs text-muted-foreground">
              {apiError}
            </div>
          )}

          {/* File Grid */}
          <ScrollArea className="flex-1">
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
            <span>{filteredItems.length} items</span>
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
