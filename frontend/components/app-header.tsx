"use client"

import Link from "next/link"
import { usePathname } from "next/navigation"
import { Music, LayoutDashboard, FolderOpen, FileWarning, Users, Music2 } from "lucide-react"
import { cn } from "@/lib/utils"
import { ThemeToggle } from "@/components/theme-toggle"

const navItems = [
  { href: "/overview", label: "Overview", icon: LayoutDashboard },
  { href: "/app", label: "Library", icon: FolderOpen },
  { href: "/artists", label: "Artists", icon: Users },
  { href: "/spotify", label: "Spotify", icon: Music2 },
  { href: "/review", label: "Review", icon: FileWarning },
]

export function AppHeader() {
  const pathname = usePathname()

  return (
    <header className="border-b border-border bg-sidebar">
      <div className="flex h-14 items-center gap-3 px-4 md:px-6">
        {/* Logo */}
        <Link href="/overview" className="flex items-center gap-2 mr-6 shrink-0">
          <div className="flex size-8 items-center justify-center rounded-lg bg-primary">
            <Music className="size-4 text-primary-foreground" />
          </div>
          <span className="text-lg font-semibold hidden sm:block">MusicHoarder</span>
        </Link>

        {/* Navigation */}
        <nav className="flex min-w-0 flex-1 items-center gap-1 overflow-x-auto">
          {navItems.map((item) => {
            const isActive = item.href === "/app"
              ? pathname === "/app" || pathname.startsWith("/app/")
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
        <ThemeToggle className="ml-auto" />
      </div>
    </header>
  )
}
