"use client"

import Link from "next/link"
import { usePathname } from "next/navigation"
import { Music, LayoutDashboard, FolderOpen, FileWarning } from "lucide-react"
import { cn } from "@/lib/utils"

const navItems = [
  { href: "/overview", label: "Overview", icon: LayoutDashboard },
  { href: "/", label: "Library", icon: FolderOpen },
  { href: "/review", label: "Review", icon: FileWarning },
]

export function AppHeader() {
  const pathname = usePathname()

  return (
    <header className="border-b border-border bg-sidebar">
      <div className="flex h-14 items-center px-4 md:px-6">
        {/* Logo */}
        <Link href="/overview" className="flex items-center gap-2 mr-6">
          <div className="flex size-8 items-center justify-center rounded-lg bg-primary">
            <Music className="size-4 text-primary-foreground" />
          </div>
          <span className="text-lg font-semibold hidden sm:block">MusicHoarder</span>
        </Link>

        {/* Navigation */}
        <nav className="flex items-center gap-1">
          {navItems.map((item) => {
            const isActive = item.href === "/" 
              ? pathname === "/" 
              : pathname.startsWith(item.href)

            return (
              <Link
                key={item.href}
                href={item.href}
                className={cn(
                  "flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium transition-colors",
                  isActive
                    ? "bg-secondary text-foreground"
                    : "text-muted-foreground hover:bg-secondary/50 hover:text-foreground"
                )}
              >
                <item.icon className="size-4" />
                <span className="hidden sm:inline">{item.label}</span>
              </Link>
            )
          })}
        </nav>
      </div>
    </header>
  )
}
