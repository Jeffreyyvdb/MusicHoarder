import { Suspense } from "react"
import { FileBrowser } from "@/components/file-browser/file-browser"

function AppLoading() {
  return (
    <div className="flex h-screen items-center justify-center bg-background">
      <div
        className="size-8 animate-spin rounded-full border-2 border-muted-foreground/30 border-t-primary"
        aria-label="Loading library"
      />
    </div>
  )
}

export default function AppPage() {
  return (
    <Suspense fallback={<AppLoading />}>
      <FileBrowser />
    </Suspense>
  )
}
