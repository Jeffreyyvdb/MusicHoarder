"use client"

import { useRef, useState, useEffect, useLayoutEffect, useCallback } from "react"
import { useVirtualizer } from "@tanstack/react-virtual"
import { Folder, Music, CheckCircle2, Clock, AlertCircle, Loader2, Eye } from "lucide-react"
import { cn } from "@/lib/utils"
import type { FileItem } from "@/lib/types"

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
}: {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  parentRef: React.RefObject<HTMLDivElement | null>
  containerRef: (el: HTMLDivElement | null) => void
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
                onSelect={() => onSelect(item)}
                onOpen={() => onOpen(item)}
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
}: {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  parentRef: React.RefObject<HTMLDivElement | null>
  containerRef: (el: HTMLDivElement | null) => void
  columnCount: number
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
              {rowItems.map((item) => (
                <div
                  key={item.id}
                  className="flex justify-start"
                  style={{ width: `${GRID_TILE_SIZE}px`, height: `${GRID_TILE_SIZE}px` }}
                >
                  <FileGridItem
                    item={item}
                    isSelected={selectedId === item.id}
                    onSelect={() => onSelect(item)}
                    onOpen={() => onOpen(item)}
                  />
                </div>
              ))}
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
  onSelect: () => void
  onOpen: () => void
}

function FileGridItem({ item, isSelected, onSelect, onOpen }: FileItemProps) {
  const isFolder = item.type === "folder"
  const status = item.metadata?.enrichmentStatus

  return (
    <button
      onClick={onSelect}
      onDoubleClick={onOpen}
      className={cn(
        "group flex h-full w-full select-none flex-col items-center justify-center gap-2 rounded-xl px-3 py-3 text-center transition-colors",
        "hover:bg-secondary/40",
        isSelected && "bg-secondary/70 ring-1 ring-ring/30"
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
                <div className="absolute inset-0 flex items-center justify-center bg-black/40 opacity-0 transition-opacity group-hover:opacity-100">
                  <Music className="size-5 text-foreground" />
                </div>
              </div>
            ) : (
              <div className="flex size-12 items-center justify-center rounded-md bg-secondary">
                <Music className="size-6 text-muted-foreground" />
              </div>
            )}
            {status && (
              <div className="absolute -bottom-1 -right-1">
                <StatusIcon status={status} />
              </div>
            )}
          </>
        )}
      </div>
      <div className="w-full min-w-0">
        <p className="truncate text-xs font-medium leading-tight">{item.name}</p>
      </div>
    </button>
  )
}

function FileListItem({ item, isSelected, onSelect, onOpen }: FileItemProps) {
  const isFolder = item.type === "folder"
  const status = item.metadata?.enrichmentStatus

  return (
    <button
      onClick={onSelect}
      onDoubleClick={onOpen}
      className={cn(
        "w-full px-4 py-2 text-left transition-colors",
        "hover:bg-secondary/50",
        isSelected && "bg-primary/10"
      )}
    >
      <div
        className={cn(
          "grid items-center gap-x-3 gap-y-0",
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
              <div className="flex size-10 items-center justify-center rounded bg-secondary">
                <Music className="size-5 text-muted-foreground" />
              </div>
            )}
          </div>

          <div className="min-w-0 flex-1">
            <p className="truncate font-medium leading-snug">{item.name}</p>
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
      </div>
    </button>
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
