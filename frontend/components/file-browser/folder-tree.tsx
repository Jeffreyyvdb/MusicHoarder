"use client"

import { useState } from "react"
import { ChevronRight, Folder, FolderOpen } from "lucide-react"
import { cn } from "@/lib/utils"
import type { FileItem } from "@/lib/types"

interface FolderTreeProps {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  level?: number
}

export function FolderTree({ items, selectedId, onSelect, level = 0 }: FolderTreeProps) {
  return (
    <div className="space-y-0.5">
      {items.filter(item => item.type === "folder").map((item) => (
        <FolderTreeItem
          key={item.id}
          item={item}
          selectedId={selectedId}
          onSelect={onSelect}
          level={level}
        />
      ))}
    </div>
  )
}

interface FolderTreeItemProps {
  item: FileItem
  selectedId: string | null
  onSelect: (item: FileItem) => void
  level: number
}

function FolderTreeItem({ item, selectedId, onSelect, level }: FolderTreeItemProps) {
  const [isExpanded, setIsExpanded] = useState(level < 1)
  const hasChildren = item.children?.some(child => child.type === "folder")
  const isSelected = selectedId === item.id

  return (
    <div>
      <button
        onClick={() => {
          onSelect(item)
          if (hasChildren) {
            setIsExpanded(!isExpanded)
          }
        }}
        className={cn(
          "flex w-full items-center gap-1 rounded-md px-2 py-1.5 text-sm transition-colors",
          "hover:bg-sidebar-accent hover:text-sidebar-accent-foreground",
          isSelected && "bg-sidebar-accent text-sidebar-accent-foreground"
        )}
        style={{ paddingLeft: `${level * 12 + 8}px` }}
      >
        <span className="flex size-4 shrink-0 items-center justify-center">
          {hasChildren && (
            <ChevronRight
              className={cn(
                "size-3 text-muted-foreground transition-transform",
                isExpanded && "rotate-90"
              )}
            />
          )}
        </span>
        {isExpanded ? (
          <FolderOpen className="size-4 shrink-0 text-primary" />
        ) : (
          <Folder className="size-4 shrink-0 text-primary" />
        )}
        <span className="truncate">{item.name}</span>
      </button>
      
      {isExpanded && hasChildren && item.children && (
        <FolderTree
          items={item.children}
          selectedId={selectedId}
          onSelect={onSelect}
          level={level + 1}
        />
      )}
    </div>
  )
}
