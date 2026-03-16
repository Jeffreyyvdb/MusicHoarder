"use client"

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

export function FileGrid({
  items,
  selectedId,
  onSelect,
  onOpen,
  viewMode,
  emptyMessage = "This folder is empty",
}: FileGridProps) {
  if (items.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center text-muted-foreground">
        <div className="text-center">
          <Folder className="mx-auto size-12 opacity-50" />
          <p className="mt-2">{emptyMessage}</p>
        </div>
      </div>
    )
  }

  if (viewMode === "list") {
    return (
      <div className="divide-y divide-border">
        {items.map((item) => (
          <FileListItem
            key={item.id}
            item={item}
            isSelected={selectedId === item.id}
            onSelect={() => onSelect(item)}
            onOpen={() => onOpen(item)}
          />
        ))}
      </div>
    )
  }

  return (
    <div className="grid grid-cols-2 gap-3 p-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
      {items.map((item) => (
        <FileGridItem
          key={item.id}
          item={item}
          isSelected={selectedId === item.id}
          onSelect={() => onSelect(item)}
          onOpen={() => onOpen(item)}
        />
      ))}
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
        "group flex flex-col items-center gap-2 rounded-lg p-3 text-center transition-all",
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
