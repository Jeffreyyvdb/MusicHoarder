"use client"

import { ChevronRight, Home } from "lucide-react"
import type { FileItem } from "@/lib/types"

interface BreadcrumbNavProps {
  path: FileItem[]
  onNavigate: (item: FileItem) => void
}

export function BreadcrumbNav({ path, onNavigate }: BreadcrumbNavProps) {
  // On mobile, show only first and last 2 items with ellipsis
  const shouldCollapse = path.length > 3
  const visiblePath = shouldCollapse
    ? [path[0], ...path.slice(-2)]
    : path

  return (
    <nav className="flex items-center gap-0.5 text-sm overflow-x-auto scrollbar-none sm:gap-1">
      {visiblePath.map((item, index) => {
        const isFirst = index === 0
        const isLast = index === visiblePath.length - 1
        const showEllipsis = shouldCollapse && index === 1

        return (
          <div key={item.id} className="flex shrink-0 items-center gap-0.5 sm:gap-1">
            {index > 0 && (
              <>
                {showEllipsis && (
                  <>
                    <ChevronRight className="size-3.5 text-muted-foreground sm:size-4" />
                    <span className="px-1 text-muted-foreground">...</span>
                  </>
                )}
                <ChevronRight className="size-3.5 text-muted-foreground sm:size-4" />
              </>
            )}
            <button
              onClick={() => onNavigate(item)}
              className="flex items-center gap-1 rounded px-1.5 py-1 transition-colors hover:bg-secondary sm:gap-1.5 sm:px-2"
            >
              {isFirst && <Home className="size-3.5" />}
              <span className={`truncate max-w-[80px] sm:max-w-[150px] ${isLast ? "font-medium" : "text-muted-foreground"}`}>
                {item.name}
              </span>
            </button>
          </div>
        )
      })}
    </nav>
  )
}
