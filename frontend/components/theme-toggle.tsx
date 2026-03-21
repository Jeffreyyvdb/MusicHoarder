"use client"

import * as React from "react"
import { Moon, Sun } from "lucide-react"
import { useTheme } from "next-themes"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"

type ThemeToggleProps = {
  className?: string
}

export function ThemeToggle({ className }: ThemeToggleProps) {
  const { setTheme, resolvedTheme } = useTheme()
  const [mounted, setMounted] = React.useState(false)

  React.useEffect(() => {
    setMounted(true)
  }, [])

  if (!mounted) {
    return (
      <Button
        variant="outline"
        size="icon"
        className={cn("size-9 shrink-0 border-border bg-background/80 backdrop-blur-sm", className)}
        disabled
        aria-hidden
        tabIndex={-1}
      >
        <Sun className="size-[1.125rem] opacity-0" />
      </Button>
    )
  }

  const isDark = resolvedTheme === "dark"

  return (
    <Button
      type="button"
      variant="outline"
      size="icon"
      className={cn(
        "size-9 shrink-0 border-border bg-background/80 backdrop-blur-sm shadow-sm transition-colors hover:bg-accent",
        className
      )}
      onClick={() => setTheme(isDark ? "light" : "dark")}
      aria-label={isDark ? "Switch to light mode" : "Switch to dark mode"}
      title={isDark ? "Light mode" : "Dark mode"}
    >
      {isDark ? (
        <Sun className="size-[1.125rem] text-amber-500" />
      ) : (
        <Moon className="size-[1.125rem] text-muted-foreground" />
      )}
    </Button>
  )
}
