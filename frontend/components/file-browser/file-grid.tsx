"use client"

import { useRef, useState, useEffect, useLayoutEffect, useCallback } from "react"
import { useVirtualizer } from "@tanstack/react-virtual"
import { Folder, Music, CheckCircle2, Clock, AlertCircle, Loader2, Eye, Pause, Play } from "lucide-react"
import { cn } from "@/lib/utils"
import type { FileItem } from "@/lib/types"
import { usePlayer } from "@/lib/player-context"
import { getSongStreamUrl, parseSongId } from "@/lib/api-client"

interface FileGridProps {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  viewMode: "grid" | "list"
  emptyMessage?: string
}

const LIST_ROW_HEIGHT = 52
const GRID_TILE_SIZE = 128
const GRID_GAP = 4
const GRID_PADDING = 16

function getColumnCount(containerWidth: number): number {
  const available = containerWidth - GRID_PADDING * 2
  return Math.max(2, Math.floor((available + GRID_GAP) / (GRID_TILE_SIZE + GRID_GAP)))
}

export function FileGrid({
  items,
  selectedId,
  onSelect,
  onOpen,
  viewMode,
  emptyMessage = "This folder is empty",
}: FileGridProps) {
  const { currentSong, isPlaying, playSong } = usePlayer()
  const parentRef = useRef<HTMLDivElement>(null)
  const observerRef = useRef<ResizeObserver | null>(null)
  const [containerWidth, setContainerWidth] = useState(0)

  const setRefAndMeasure = useCallback((el: HTMLDivElement | null) => {
    ;(parentRef as React.MutableRefObject<HTMLDivElement | null>).current = el
    observerRef.current?.disconnect()
    observerRef.current = null
    if (el) {
      setContainerWidth(el.clientWidth)
      const observer = new ResizeObserver((entries) => {
        const entry = entries[0]
        if (entry) setContainerWidth(entry.contentRect.width)
      })
      observer.observe(el)
      observerRef.current = observer
    }
  }, [])

  useLayoutEffect(() => {
    const el = parentRef.current
    if (el && !observerRef.current) {
      setContainerWidth(el.clientWidth)
      const observer = new ResizeObserver((entries) => {
        const entry = entries[0]
        if (entry) setContainerWidth(entry.contentRect.width)
      })
      observer.observe(el)
      observerRef.current = observer
    }
    return () => {
      observerRef.current?.disconnect()
      observerRef.current = null
    }
  }, [])

  useEffect(() => {
    let timeoutId: ReturnType<typeof setTimeout>
    const onResize = () => {
      clearTimeout(timeoutId)
      timeoutId = setTimeout(() => {
        const w = parentRef.current?.clientWidth
        if (w != null) setContainerWidth(w)
      }, 100)
    }
    window.addEventListener("resize", onResize)
    return () => {
      window.removeEventListener("resize", onResize)
      clearTimeout(timeoutId)
    }
  }, [])

  const columnCount = getColumnCount(containerWidth || 800)

  if (items.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center text-muted-foreground h-full">
        <div className="text-center">
          <Folder className="mx-auto size-12 opacity-50" />
          <p className="mt-2">{emptyMessage}</p>
        </div>
      </div>
    )
  }

  if (viewMode === "list") {
    return (
      <VirtualizedList
        items={items}
        selectedId={selectedId}
        onSelect={onSelect}
        onOpen={onOpen}
        parentRef={parentRef}
        containerRef={setRefAndMeasure}
        currentSong={currentSong}
        isPlaying={isPlaying}
        playSong={playSong}
      />
    )
  }

  return (
    <VirtualizedGrid
      items={items}
      selectedId={selectedId}
      onSelect={onSelect}
      onOpen={onOpen}
      parentRef={parentRef}
      containerRef={setRefAndMeasure}
      columnCount={columnCount}
      currentSong={currentSong}
      isPlaying={isPlaying}
      playSong={playSong}
    />
  )
}

function VirtualizedList({
  items,
  selectedId,
  onSelect,
  onOpen,
  parentRef,
  containerRef,
  currentSong,
  isPlaying,
  playSong,
}: {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  parentRef: React.RefObject<HTMLDivElement | null>
  containerRef: (el: HTMLDivElement | null) => void
  currentSong: { id: number } | null
  isPlaying: boolean
  playSong: (args: { id: number; title: string; artist: string; streamUrl: string }) => void
}) {
  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => LIST_ROW_HEIGHT,
    overscan: 15,
  })

  return (
    <div ref={containerRef} className="h-full overflow-y-auto">
      <div
        className="relative w-full"
        style={{ height: `${virtualizer.getTotalSize()}px` }}
      >
        {virtualizer.getVirtualItems().map((virtualRow) => {
          const item = items[virtualRow.index]
          const songId = parseSongId(item.id)
          const isCurrentlyPlaying = currentSong?.id === songId && isPlaying
          const isCurrentlyLoaded = currentSong?.id === songId
          return (
            <div
              key={virtualRow.key}
              className="absolute left-0 top-0 w-full"
              style={{
                height: `${virtualRow.size}px`,
                transform: `translateY(${virtualRow.start}px)`,
              }}
            >
              <FileListItem
                item={item}
                isSelected={selectedId === item.id}
                isPlaying={isCurrentlyPlaying}
                isLoaded={isCurrentlyLoaded}
                onSelect={() => onSelect(item)}
                onOpen={() => onOpen(item)}
                onPlay={
                  songId !== null && item.type === "audio"
                    ? () =>
                        playSong({
                          id: songId,
                          title: item.metadata?.title ?? item.name,
                          artist: item.metadata?.artist ?? "Unknown Artist",
                          streamUrl: getSongStreamUrl(songId),
                        })
                    : undefined
                }
              />
            </div>
          )
        })}
      </div>
    </div>
  )
}

function VirtualizedGrid({
  items,
  selectedId,
  onSelect,
  onOpen,
  parentRef,
  containerRef,
  columnCount,
  currentSong,
  isPlaying,
  playSong,
}: {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  parentRef: React.RefObject<HTMLDivElement | null>
  containerRef: (el: HTMLDivElement | null) => void
  columnCount: number
  currentSong: { id: number } | null
  isPlaying: boolean
  playSong: (args: { id: number; title: string; artist: string; streamUrl: string }) => void
}) {
  const rowCount = Math.ceil(items.length / columnCount)

  const virtualizer = useVirtualizer({
    count: rowCount,
    getScrollElement: () => parentRef.current,
    estimateSize: () => GRID_TILE_SIZE + GRID_GAP,
    overscan: 5,
  })

  useEffect(() => {
    virtualizer.measure()
  }, [columnCount])

  return (
    <div ref={containerRef} className="h-full overflow-y-auto">
      <div
        className="relative w-full"
        style={{
          height: `${virtualizer.getTotalSize() + GRID_PADDING * 2}px`,
        }}
      >
        {virtualizer.getVirtualItems().map((virtualRow) => {
          const startIdx = virtualRow.index * columnCount
          const rowItems = items.slice(startIdx, startIdx + columnCount)

          return (
            <div
              key={virtualRow.key}
              className="absolute left-0 right-0 flex justify-start"
              style={{
                height: `${virtualRow.size}px`,
                transform: `translateY(${virtualRow.start + GRID_PADDING}px)`,
                padding: `0 ${GRID_PADDING}px`,
                gap: `${GRID_GAP}px`,
              }}
            >
              {rowItems.map((item) => {
                const songId = parseSongId(item.id)
                const isCurrentlyPlaying = currentSong?.id === songId && isPlaying
                const isCurrentlyLoaded = currentSong?.id === songId
                return (
                  <div
                    key={item.id}
                    className="flex justify-start"
                    style={{ width: `${GRID_TILE_SIZE}px`, height: `${GRID_TILE_SIZE}px` }}
                  >
                    <FileGridItem
                      item={item}
                      isSelected={selectedId === item.id}
                      isPlaying={isCurrentlyPlaying}
                      isLoaded={isCurrentlyLoaded}
                      onSelect={() => onSelect(item)}
                      onOpen={() => onOpen(item)}
                      onPlay={
                        songId !== null && item.type === "audio"
                          ? () =>
                              playSong({
                                id: songId,
                                title: item.metadata?.title ?? item.name,
                                artist: item.metadata?.artist ?? "Unknown Artist",
                                streamUrl: getSongStreamUrl(songId),
                              })
                          : undefined
                      }
                    />
                  </div>
                )
              })}
            </div>
          )
        })}
      </div>
    </div>
  )
}

interface FileItemProps {
  item: FileItem
  isSelected: boolean
  isPlaying: boolean
  isLoaded: boolean
  onSelect: () => void
  onOpen: () => void
  onPlay?: () => void
}

function FileGridItem({ item, isSelected, isPlaying, isLoaded, onSelect, onOpen, onPlay }: FileItemProps) {
  const isFolder = item.type === "folder"
  const status = item.metadata?.enrichmentStatus

  return (
    // Must be a div, not a button, because it contains <button> play overlays.
    // role="button" + tabIndex preserve full keyboard accessibility.
    <div
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onDoubleClick={onOpen}
      onKeyDown={(e) => {
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect() }
      }}
      className={cn(
        "group relative flex h-full w-full cursor-pointer select-none flex-col items-center justify-center gap-2 rounded-xl p-3 text-center transition-all",
        "hover:bg-secondary/50",
        isSelected && "bg-primary/10 ring-1 ring-primary",
        isLoaded && !isSelected && "bg-primary/5"
      )}
    >
      <div className="relative">
        {isFolder ? (
          <Folder className="size-12 text-primary" />
        ) : (
          <>
            {item.metadata?.albumArt ? (
              <div className="relative size-12 overflow-hidden rounded-md">
                <img
                  src={item.metadata.albumArt}
                  alt={item.metadata.album}
                  className="size-full object-cover"
                  crossOrigin="anonymous"
                />
                {/* Play overlay */}
                {onPlay && (
                  <div
                    className={cn(
                      "absolute inset-0 flex items-center justify-center bg-black/50 transition-opacity",
                      isPlaying ? "opacity-100" : "opacity-0 group-hover:opacity-100"
                    )}
                  >
                    <button
                      onClick={(e) => { e.stopPropagation(); onPlay() }}
                      className="flex size-8 items-center justify-center rounded-full bg-primary text-primary-foreground shadow-lg transition-transform hover:scale-110 active:scale-95"
                      aria-label={isPlaying ? "Pause" : "Play"}
                    >
                      {isPlaying ? (
                        <Pause className="size-3.5" />
                      ) : (
                        <Play className="size-3.5 translate-x-px" />
                      )}
                    </button>
                  </div>
                )}
              </div>
            ) : (
              <div
                className={cn(
                  "relative flex size-12 items-center justify-center rounded-md bg-secondary transition-colors",
                  isLoaded && "bg-primary/20"
                )}
              >
                <Music
                  className={cn(
                    "size-8 transition-colors",
                    isLoaded ? "text-primary" : "text-muted-foreground"
                  )}
                />
                {/* Play overlay on non-art tracks */}
                {onPlay && (
                  <div
                    className={cn(
                      "absolute inset-0 flex items-center justify-center rounded-md bg-black/50 transition-opacity",
                      isPlaying ? "opacity-100" : "opacity-0 group-hover:opacity-100"
                    )}
                  >
                    <button
                      onClick={(e) => { e.stopPropagation(); onPlay() }}
                      className="flex size-8 items-center justify-center rounded-full bg-primary text-primary-foreground shadow-lg transition-transform hover:scale-110 active:scale-95"
                      aria-label={isPlaying ? "Pause" : "Play"}
                    >
                      {isPlaying ? (
                        <Pause className="size-3.5" />
                      ) : (
                        <Play className="size-3.5 translate-x-px" />
                      )}
                    </button>
                  </div>
                )}
              </div>
            )}
            {status && (
              <div className="absolute -bottom-1 -right-1">
                <StatusIcon status={status} />
              </div>
            )}
            {/* Playing indicator dot */}
            {isPlaying && (
              <div className="absolute -top-1 -left-1 size-2.5 rounded-full bg-primary ring-2 ring-background animate-pulse" />
            )}
          </>
        )}
      </div>
      <div className="w-full min-w-0">
        <p className={cn("truncate text-sm font-medium leading-tight", isLoaded && "text-primary")}>{item.name}</p>
        {!isFolder && item.metadata && (
          <p className="truncate text-xs text-muted-foreground">
            {item.metadata.artist}
          </p>
        )}
      </div>
    </div>
  )
}

function FileListItem({ item, isSelected, isPlaying, isLoaded, onSelect, onOpen, onPlay }: FileItemProps) {
  const isFolder = item.type === "folder"
  const status = item.metadata?.enrichmentStatus

  return (
    <div
      className={cn(
        "group flex w-full items-center gap-3 px-4 py-2 transition-colors",
        "hover:bg-secondary/50",
        isSelected && "bg-primary/10",
        isLoaded && !isSelected && "bg-primary/5"
      )}
    >
      {/* Play button for audio tracks */}
      {!isFolder && onPlay ? (
        <button
          onClick={onPlay}
          className={cn(
            "flex size-8 shrink-0 items-center justify-center rounded-full transition-all",
            isLoaded
              ? "bg-primary text-primary-foreground"
              : "bg-secondary text-muted-foreground opacity-0 group-hover:opacity-100 hover:bg-primary/20 hover:text-primary"
          )}
          aria-label={isPlaying ? "Pause" : "Play"}
        >
          {isPlaying ? (
            <Pause className="size-3.5" />
          ) : (
            <Play className="size-3.5 translate-x-px" />
          )}
        </button>
      ) : (
        <div className="size-8 shrink-0" />
      )}

      {/* Main item content — clicking selects the item */}
      <button
        onClick={onSelect}
        onDoubleClick={onOpen}
        className={cn(
          "grid min-w-0 flex-1 items-center gap-x-3 gap-y-0 text-left",
          "grid-cols-[minmax(0,1fr)]",
          "sm:grid-cols-[minmax(0,1fr)_96px]",
          "md:grid-cols-[minmax(0,1fr)_96px_72px]",
          "lg:grid-cols-[minmax(0,1fr)_96px_72px_96px_28px]"
        )}
      >
        {/* Name column */}
        <div className="flex min-w-0 items-center gap-3">
          <div className="relative shrink-0">
            {isFolder ? (
              <Folder className="size-8 text-primary" />
            ) : item.metadata?.albumArt ? (
              <div className="size-10 overflow-hidden rounded">
                <img
                  src={item.metadata.albumArt}
                  alt={item.metadata.album}
                  className="size-full object-cover"
                  crossOrigin="anonymous"
                />
              </div>
            ) : (
              <div
                className={cn(
                  "flex size-10 items-center justify-center rounded transition-colors",
                  isLoaded ? "bg-primary/20" : "bg-secondary"
                )}
              >
                <Music
                  className={cn(
                    "size-5 transition-colors",
                    isLoaded ? "text-primary" : "text-muted-foreground"
                  )}
                />
              </div>
            )}
          </div>

          <div className="min-w-0 flex-1">
            <p className={cn("truncate font-medium leading-snug", isLoaded && "text-primary")}>{item.name}</p>
            {!isFolder && item.metadata && (
              <p className="truncate text-sm leading-snug text-muted-foreground">
                {item.metadata.artist} - {item.metadata.album}
              </p>
            )}
          </div>
        </div>

        {/* Format / Kind */}
        <span className="hidden text-sm text-muted-foreground sm:block">
          {!isFolder && item.metadata ? item.metadata.format : ""}
        </span>

        {/* Duration */}
        <span className="hidden text-sm tabular-nums text-muted-foreground md:block">
          {!isFolder && item.metadata ? formatDuration(item.metadata.duration) : ""}
        </span>

        {/* Size */}
        <span className="hidden text-sm tabular-nums text-muted-foreground lg:block">
          {!isFolder && item.metadata ? formatFileSize(item.metadata.fileSize) : ""}
        </span>

        {/* Status */}
        <span className="hidden justify-self-end lg:block">
          {status ? <StatusIcon status={status} /> : null}
        </span>
      </button>
    </div>
  )
}

function StatusIcon({ status }: { status: string }) {
  switch (status) {
    case "complete":
      return <CheckCircle2 className="size-4 text-primary" />
    case "processing":
      return <Loader2 className="size-4 animate-spin text-chart-2" />
    case "needsreview":
      return <Eye className="size-4 text-amber-600 dark:text-amber-500" />
    case "pending":
      return <Clock className="size-4 text-muted-foreground" />
    case "failed":
      return <AlertCircle className="size-4 text-destructive" />
    default:
      return null
  }
}

function formatDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${mins}:${secs.toString().padStart(2, "0")}`
}

function formatFileSize(bytes: number): string {
  const mb = bytes / (1024 * 1024)
  return `${mb.toFixed(1)} MB`
}
