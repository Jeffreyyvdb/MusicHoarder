"use client"

import { createContext, useContext, useState, useCallback } from "react"
import { ChevronRight, Folder, FolderOpen } from "lucide-react"
import { cn } from "@/lib/utils"
import type { FileItem } from "@/lib/types"

// Context to share expanded state across all nested folder items
interface FolderTreeContextValue {
  expandedIds: Set<string>
  toggleExpanded: (id: string) => void
  setExpanded: (id: string, expanded: boolean) => void
}

const FolderTreeContext = createContext<FolderTreeContextValue | null>(null)

function useFolderTreeContext() {
  const context = useContext(FolderTreeContext)
  if (!context) {
    throw new Error("FolderTreeItem must be used within a FolderTree")
  }
  return context
}

interface FolderTreeProps {
  items: FileItem[]
  selectedId: string | null
  onSelect: (item: FileItem) => void
  level?: number
}

export function FolderTree({ items, selectedId, onSelect, level = 0 }: FolderTreeProps) {
  // Only the root FolderTree (level 0) provides the context
  if (level === 0) {
    return (
      <FolderTreeProvider defaultExpandedIds={getDefaultExpandedIds(items)}>
        <FolderTreeInner
          items={items}
          selectedId={selectedId}
          onSelect={onSelect}
          level={level}
        />
      </FolderTreeProvider>
    )
  }

  return (
    <FolderTreeInner
      items={items}
      selectedId={selectedId}
      onSelect={onSelect}
      level={level}
    />
  )
}

// Get default expanded IDs (first level folders)
function getDefaultExpandedIds(items: FileItem[]): Set<string> {
  const ids = new Set<string>()
  for (const item of items) {
    if (item.type === "folder") {
      ids.add(item.id)
    }
  }
  return ids
}

interface FolderTreeProviderProps {
  children: React.ReactNode
  defaultExpandedIds: Set<string>
}

function FolderTreeProvider({ children, defaultExpandedIds }: FolderTreeProviderProps) {
  const [expandedIds, setExpandedIds] = useState<Set<string>>(defaultExpandedIds)

  const toggleExpanded = useCallback((id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }, [])

  const setExpanded = useCallback((id: string, expanded: boolean) => {
    setExpandedIds((prev) => {
      const next = new Set(prev)
      if (expanded) {
        next.add(id)
      } else {
        next.delete(id)
      }
      return next
    })
  }, [])

  return (
    <FolderTreeContext.Provider value={{ expandedIds, toggleExpanded, setExpanded }}>
      {children}
    </FolderTreeContext.Provider>
  )
}

function FolderTreeInner({ items, selectedId, onSelect, level }: FolderTreeProps & { level: number }) {
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
  const { expandedIds, toggleExpanded, setExpanded } = useFolderTreeContext()
  const isExpanded = expandedIds.has(item.id)
  const hasChildren = item.children?.some(child => child.type === "folder")
  const isSelected = selectedId === item.id

  const handleChevronClick = (e: React.MouseEvent) => {
    e.stopPropagation()
    toggleExpanded(item.id)
  }

  const handleFolderClick = () => {
    onSelect(item)
    // Auto-expand when selecting a folder (like Finder does)
    if (hasChildren && !isExpanded) {
      setExpanded(item.id, true)
    }
  }

  return (
    <div>
      <button
        onClick={handleFolderClick}
        className={cn(
          "flex w-full items-center gap-1 rounded-md px-2 py-1.5 text-sm transition-colors",
          "hover:bg-sidebar-accent hover:text-sidebar-accent-foreground",
          isSelected && "bg-sidebar-accent text-sidebar-accent-foreground"
        )}
        style={{ paddingLeft: `${level * 12 + 8}px` }}
      >
        <span 
          className="flex size-4 shrink-0 items-center justify-center cursor-pointer"
          onClick={hasChildren ? handleChevronClick : undefined}
          role={hasChildren ? "button" : undefined}
          aria-label={hasChildren ? (isExpanded ? "Collapse folder" : "Expand folder") : undefined}
        >
          {hasChildren && (
            <ChevronRight
              className={cn(
                "size-3 text-muted-foreground transition-transform hover:text-foreground",
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
