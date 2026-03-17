"use client"

import { useRef, useState, useEffect } from "react"
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
const GRID_GAP = 12
const GRID_PADDING = 16

function getColumnCount(containerWidth: number): number {
  if (containerWidth >= 1280) return 6
  if (containerWidth >= 1024) return 5
  if (containerWidth >= 768) return 4
  if (containerWidth >= 640) return 3
  return 2
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
  const [containerWidth, setContainerWidth] = useState(800)

  useEffect(() => {
    const el = parentRef.current
    if (!el) return

    const observer = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (entry) {
        setContainerWidth(entry.contentRect.width)
      }
    })

    observer.observe(el)
    setContainerWidth(el.clientWidth)

    return () => observer.disconnect()
  }, [])

  const columnCount = getColumnCount(containerWidth)

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
      columnCount={columnCount}
      containerWidth={containerWidth}
    />
  )
}

function VirtualizedList({
  items,
  selectedId,
  onSelect,
  onOpen,
  parentRef,
}: {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  parentRef: React.RefObject<HTMLDivElement | null>
}) {
  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => LIST_ROW_HEIGHT,
    overscan: 15,
  })

  return (
    <div ref={parentRef} className="h-full overflow-y-auto">
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
  columnCount,
  containerWidth,
}: {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  onOpen: (item: FileItem) => void
  parentRef: React.RefObject<HTMLDivElement | null>
  columnCount: number
  containerWidth: number
}) {
  const rowCount = Math.ceil(items.length / columnCount)
  const cellWidth = Math.floor(
    (containerWidth - GRID_PADDING * 2 - GRID_GAP * (columnCount - 1)) / columnCount
  )
  const rowHeight = cellWidth + GRID_GAP

  const virtualizer = useVirtualizer({
    count: rowCount,
    getScrollElement: () => parentRef.current,
    estimateSize: () => rowHeight,
    overscan: 5,
  })

  return (
    <div ref={parentRef} className="h-full overflow-y-auto">
      <div
        className="relative w-full"
        style={{
          height: `${virtualizer.getTotalSize() + GRID_PADDING * 2}px`,
          padding: `${GRID_PADDING}px`,
        }}
      >
        {virtualizer.getVirtualItems().map((virtualRow) => {
          const startIdx = virtualRow.index * columnCount
          const rowItems = items.slice(startIdx, startIdx + columnCount)

          return (
            <div
              key={virtualRow.key}
              className="absolute left-0 right-0"
              style={{
                height: `${virtualRow.size}px`,
                transform: `translateY(${virtualRow.start + GRID_PADDING}px)`,
                padding: `0 ${GRID_PADDING}px`,
              }}
            >
              <div
                className="grid h-full"
                style={{
                  gridTemplateColumns: `repeat(${columnCount}, minmax(0, 1fr))`,
                  gap: `${GRID_GAP}px`,
                }}
              >
                {rowItems.map((item) => (
                  <FileGridItem
                    key={item.id}
                    item={item}
                    isSelected={selectedId === item.id}
                    onSelect={() => onSelect(item)}
                    onOpen={() => onOpen(item)}
                  />
                ))}
              </div>
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
        "group flex aspect-square flex-col items-center justify-center gap-2 rounded-lg p-3 text-center transition-all",
        "hover:bg-secondary/50",
        isSelected && "bg-primary/10 ring-1 ring-primary"
      )}
    >
      <div className="relative">
        {isFolder ? (
          <Folder className="size-12 text-primary" />
        ) : (
          <>
            {item.metadata?.albumArt ? (
              <div className="relative size-16 overflow-hidden rounded-md">
                <img
                  src={item.metadata.albumArt}
                  alt={item.metadata.album}
                  className="size-full object-cover"
                  crossOrigin="anonymous"
                />
                <div className="absolute inset-0 flex items-center justify-center bg-black/40 opacity-0 transition-opacity group-hover:opacity-100">
                  <Music className="size-6 text-foreground" />
                </div>
              </div>
            ) : (
              <div className="flex size-16 items-center justify-center rounded-md bg-secondary">
                <Music className="size-8 text-muted-foreground" />
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
      <div className="w-full">
        <p className="truncate text-sm font-medium">{item.name}</p>
        {!isFolder && item.metadata && (
          <p className="truncate text-xs text-muted-foreground">
            {item.metadata.artist}
          </p>
        )}
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
        "flex w-full items-center gap-3 px-4 py-2 text-left transition-colors",
        "hover:bg-secondary/50",
        isSelected && "bg-primary/10"
      )}
    >
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
        <p className="truncate font-medium">{item.name}</p>
        {!isFolder && item.metadata && (
          <p className="truncate text-sm text-muted-foreground">
            {item.metadata.artist} - {item.metadata.album}
          </p>
        )}
      </div>

      {!isFolder && item.metadata && (
        <>
          <span className="hidden text-sm text-muted-foreground sm:block">
            {item.metadata.format}
          </span>
          <span className="hidden text-sm text-muted-foreground md:block">
            {formatDuration(item.metadata.duration)}
          </span>
          <span className="hidden text-sm text-muted-foreground lg:block">
            {formatFileSize(item.metadata.fileSize)}
          </span>
          {status && <StatusIcon status={status} />}
        </>
      )}
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
