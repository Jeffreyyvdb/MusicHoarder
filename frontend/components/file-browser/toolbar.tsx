"use client"

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
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { cn } from "@/lib/utils"

interface ToolbarProps {
  viewMode: "grid" | "list"
  onViewModeChange: (mode: "grid" | "list") => void
  searchQuery: string
  onSearchChange: (query: string) => void
}

export function Toolbar({
  viewMode,
  onViewModeChange,
  searchQuery,
  onSearchChange,
}: ToolbarProps) {
  return (
    <div className="flex items-center gap-1.5 border-b border-border bg-card/50 px-2 py-2 sm:gap-2 sm:px-4">
      <div className="relative min-w-0 flex-1 sm:max-w-sm">
        <Search className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          type="search"
          placeholder="Search..."
          value={searchQuery}
          onChange={(e) => onSearchChange(e.target.value)}
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

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon" className="size-8 shrink-0">
            <SlidersHorizontal className="size-4" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuLabel>Sort By</DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem>Name</DropdownMenuItem>
          <DropdownMenuItem>Date Modified</DropdownMenuItem>
          <DropdownMenuItem>Size</DropdownMenuItem>
          <DropdownMenuItem>Type</DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuLabel>Filter</DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem>All Files</DropdownMenuItem>
          <DropdownMenuItem>Audio Only</DropdownMenuItem>
          <DropdownMenuItem>Folders Only</DropdownMenuItem>
          <DropdownMenuItem>Pending Enrichment</DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      <div className="hidden h-6 w-px bg-border sm:block" />

      <Button variant="ghost" size="sm" className="hidden gap-2 sm:flex">
        <FolderInput className="size-4" />
        <span className="hidden md:inline">Import</span>
      </Button>

      <Button variant="ghost" size="icon" className="size-8 shrink-0">
        <RefreshCw className="size-4" />
      </Button>
    </div>
  )
}
