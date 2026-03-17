"use client"

import { useEffect, useRef, useState } from "react"
import {
  Grid3X3,
  List,
  Search,
  SlidersHorizontal,
  FolderInput,
  RefreshCw,
} from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import type { LibraryPathMode } from "@/lib/api-client"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { cn } from "@/lib/utils"

const SEARCH_DEBOUNCE_MS = 150

type LibrarySortBy = "name" | "dateModified" | "size" | "type"
type LibrarySortDirection = "asc" | "desc"
type LibraryFilterBy = "all" | "audio" | "folders" | "pendingEnrichment"

interface ToolbarProps {
  viewMode: "grid" | "list"
  onViewModeChange: (mode: "grid" | "list") => void
  searchQuery: string
  onSearchChange: (query: string) => void
  sortBy: LibrarySortBy
  sortDirection: LibrarySortDirection
  filterBy: LibraryFilterBy
  onSortByChange: (value: LibrarySortBy) => void
  onSortDirectionChange: (value: LibrarySortDirection) => void
  onFilterByChange: (value: LibraryFilterBy) => void
  libraryMode: LibraryPathMode
  onLibraryModeChange: (mode: LibraryPathMode) => void
  onRefresh: () => void
  isRefreshing: boolean
}

export function Toolbar({
  viewMode,
  onViewModeChange,
  searchQuery,
  onSearchChange,
  sortBy,
  sortDirection,
  filterBy,
  onSortByChange,
  onSortDirectionChange,
  onFilterByChange,
  libraryMode,
  onLibraryModeChange,
  onRefresh,
  isRefreshing,
}: ToolbarProps) {
  const [localQuery, setLocalQuery] = useState(searchQuery)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => {
    setLocalQuery(searchQuery)
  }, [searchQuery])

  const handleSearchChange = (value: string) => {
    setLocalQuery(value)
    if (debounceRef.current !== null) {
      clearTimeout(debounceRef.current)
    }
    debounceRef.current = setTimeout(() => {
      onSearchChange(value)
    }, SEARCH_DEBOUNCE_MS)
  }

  return (
    <div className="flex items-center gap-1.5 border-b border-border bg-card/50 px-2 py-2 sm:gap-2 sm:px-4">
      <div className="relative min-w-0 flex-1 sm:max-w-sm">
        <Search className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          type="search"
          placeholder="Search..."
          value={localQuery}
          onChange={(e) => handleSearchChange(e.target.value)}
          className="h-8 pl-8 bg-secondary border-0"
        />
      </div>

      <div className="flex shrink-0 items-center gap-1 rounded-lg bg-secondary p-1">
        <Button
          variant="ghost"
          size="icon"
          className={cn(
            "size-7",
            viewMode === "grid" && "bg-background shadow-sm"
          )}
          onClick={() => onViewModeChange("grid")}
        >
          <Grid3X3 className="size-4" />
        </Button>
        <Button
          variant="ghost"
          size="icon"
          className={cn(
            "size-7",
            viewMode === "list" && "bg-background shadow-sm"
          )}
          onClick={() => onViewModeChange("list")}
        >
          <List className="size-4" />
        </Button>
      </div>

      <div className="hidden shrink-0 items-center gap-1 rounded-lg bg-secondary p-1 sm:flex">
        <Button
          variant="ghost"
          size="sm"
          className={cn("h-7 px-2 text-xs", libraryMode === "destination" && "bg-background shadow-sm")}
          onClick={() => onLibraryModeChange("destination")}
        >
          Destination
        </Button>
        <Button
          variant="ghost"
          size="sm"
          className={cn("h-7 px-2 text-xs", libraryMode === "source" && "bg-background shadow-sm")}
          onClick={() => onLibraryModeChange("source")}
        >
          Source
        </Button>
      </div>

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon" className="size-8 shrink-0">
            <SlidersHorizontal className="size-4" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuLabel>Sort By</DropdownMenuLabel>
          <DropdownMenuRadioGroup value={sortBy} onValueChange={(value) => onSortByChange(value as LibrarySortBy)}>
            <DropdownMenuRadioItem value="name">Name</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="dateModified">Date Modified</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="size">Size</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="type">Type</DropdownMenuRadioItem>
          </DropdownMenuRadioGroup>
          <DropdownMenuSeparator />
          <DropdownMenuLabel>Sort Direction</DropdownMenuLabel>
          <DropdownMenuRadioGroup
            value={sortDirection}
            onValueChange={(value) => onSortDirectionChange(value as LibrarySortDirection)}
          >
            <DropdownMenuRadioItem value="asc">Ascending</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="desc">Descending</DropdownMenuRadioItem>
          </DropdownMenuRadioGroup>
          <DropdownMenuSeparator />
          <DropdownMenuLabel>Filter</DropdownMenuLabel>
          <DropdownMenuRadioGroup value={filterBy} onValueChange={(value) => onFilterByChange(value as LibraryFilterBy)}>
            <DropdownMenuRadioItem value="all">All Files</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="audio">Audio Only</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="folders">Folders Only</DropdownMenuRadioItem>
            <DropdownMenuRadioItem value="pendingEnrichment">Pending Enrichment</DropdownMenuRadioItem>
          </DropdownMenuRadioGroup>
        </DropdownMenuContent>
      </DropdownMenu>

      <div className="hidden h-6 w-px bg-border sm:block" />

      <Button variant="ghost" size="sm" className="hidden gap-2 sm:flex">
        <FolderInput className="size-4" />
        <span className="hidden md:inline">Import</span>
      </Button>

      <Button
        variant="ghost"
        size="icon"
        className="size-8 shrink-0"
        onClick={onRefresh}
        disabled={isRefreshing}
        aria-label="Refresh library"
      >
        <RefreshCw className={cn("size-4", isRefreshing && "animate-spin")} />
      </Button>
    </div>
  )
}
