"use client"

import * as React from "react"
import { Moon, Sun } from "lucide-react"
import { useTheme } from "next-themes"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"

type ThemeToggleVariant = "outline" | "ghost"

type ThemeToggleProps = {
  className?: string
  variant?: ThemeToggleVariant
}

export function ThemeToggle({ className, variant = "outline" }: ThemeToggleProps) {
  const { setTheme, resolvedTheme } = useTheme()
  const [mounted, setMounted] = React.useState(false)

  React.useEffect(() => {
    setMounted(true)
  }, [])

  const sizeClasses = variant === "ghost" ? "size-9" : "size-9 shrink-0"
  const outlineClasses =
    variant === "outline"
      ? "border-border bg-background/80 backdrop-blur-sm shadow-sm hover:bg-accent"
      : ""

  if (!mounted) {
    return (
      <Button
        variant={variant}
        size="icon"
        className={cn(sizeClasses, outlineClasses, className)}
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
      variant={variant}
      size="icon"
      className={cn(sizeClasses, outlineClasses, "transition-colors", className)}
      onClick={() => setTheme(isDark ? "light" : "dark")}
      aria-label={isDark ? "Switch to light mode" : "Switch to dark mode"}
      title={isDark ? "Light mode" : "Dark mode"}
    >
      {isDark ? (
        <Sun className="size-[1.125rem] text-foreground" />
      ) : (
        <Moon className="size-[1.125rem] text-foreground" />
      )}
    </Button>
  )
}
