"use client"

import { useState, useEffect } from "react"
import { AppShell } from "@/components/app-shell"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Badge } from "@/components/ui/badge"
import {
  fetchSpotifyCredentials,
  saveSpotifyCredentials,
  fetchSpotifyStatus,
  purgeAll,
  purgePostFingerprint,
  fetchPurgeStatus,
} from "@/lib/api-client"
import type {
  PurgeMode,
  PurgeSnapshot,
  SpotifyCredentialsResponse,
  SpotifyStatusResponse,
} from "@/lib/api-client"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import {
  Settings,
  KeyRound,
  Save,
  Loader2,
  CheckCircle2,
  AlertCircle,
  AlertTriangle,
  Music,
  ExternalLink,
  Trash2,
} from "lucide-react"

export default function SettingsPage() {
  const [clientId, setClientId] = useState("")
  const [clientSecret, setClientSecret] = useState("")
  const [isSaving, setIsSaving] = useState(false)
  const [isLoading, setIsLoading] = useState(true)
  const [savedCredentials, setSavedCredentials] = useState<SpotifyCredentialsResponse | null>(null)
  const [spotifyStatus, setSpotifyStatus] = useState<SpotifyStatusResponse | null>(null)
  const [saveResult, setSaveResult] = useState<{ success: boolean; message: string } | null>(null)
  const [purgeSnapshot, setPurgeSnapshot] = useState<PurgeSnapshot | null>(null)
  const [purgeStartError, setPurgeStartError] = useState<string | null>(null)

  useEffect(() => {
    async function load() {
      setIsLoading(true)
      try {
        const [creds, status, purge] = await Promise.all([
          fetchSpotifyCredentials().catch(() => ({ clientId: null, hasClientSecret: false }) as SpotifyCredentialsResponse),
          fetchSpotifyStatus().catch(() => ({ connected: false, hasCredentials: false, tokenExpired: false }) as SpotifyStatusResponse),
          fetchPurgeStatus().catch(() => null),
        ])
        setSavedCredentials(creds)
        setSpotifyStatus(status)
        if (creds.clientId) {
          setClientId(creds.clientId)
        }
        if (purge) setPurgeSnapshot(purge)
      } catch {
        // Settings page should load even if API is down
      } finally {
        setIsLoading(false)
      }
    }
    load()
  }, [])

  // While a purge is running, poll its status every 1.5s. Stops when it completes or fails,
  // and restarts if the user kicks off a new purge.
  useEffect(() => {
    if (purgeSnapshot?.status !== "running") return

    let cancelled = false
    const tick = async () => {
      try {
        const snap = await fetchPurgeStatus()
        if (!cancelled) setPurgeSnapshot(snap)
      } catch {
        // keep polling on transient errors
      }
    }
    const id = setInterval(tick, 1500)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [purgeSnapshot?.status])

  const handlePurge = async (mode: PurgeMode) => {
    setPurgeStartError(null)
    const response = mode === "post-fingerprint" ? await purgePostFingerprint() : await purgeAll()
    if (!response.ok) {
      setPurgeStartError(response.message)
      return
    }
    // Optimistically reflect "running" — the poll loop will take over with real progress.
    setPurgeSnapshot({
      status: "running",
      mode,
      jobId: response.jobId,
      startedAt: new Date().toISOString(),
      completedAt: null,
      songsTotal: 0,
      songsProcessed: 0,
      filesTotal: 0,
      filesDeleted: 0,
      filesFailed: 0,
      spotifyMatchesCleared: 0,
      error: null,
    })
  }

  const purgeRunning = purgeSnapshot?.status === "running"

  const handleSave = async () => {
    if (!clientId.trim() || !clientSecret.trim()) {
      setSaveResult({ success: false, message: "Both Client ID and Client Secret are required." })
      return
    }

    setIsSaving(true)
    setSaveResult(null)
    try {
      await saveSpotifyCredentials(clientId.trim(), clientSecret.trim())
      setSaveResult({ success: true, message: "Spotify credentials saved successfully." })
      setSavedCredentials({ clientId: clientId.trim(), hasClientSecret: true })
      setClientSecret("")
    } catch (err) {
      setSaveResult({
        success: false,
        message: err instanceof Error ? err.message : "Failed to save credentials.",
      })
    } finally {
      setIsSaving(false)
    }
  }

  return (
    <AppShell>
      <div className="flex-1 overflow-auto">
        <div className="mx-auto max-w-2xl p-6 md:p-8">
          <div className="flex items-center gap-3 mb-8">
            <div className="flex size-10 items-center justify-center rounded-lg bg-secondary">
              <Settings className="size-5 text-foreground" />
            </div>
            <div>
              <h1 className="text-2xl font-bold">Settings</h1>
              <p className="text-sm text-muted-foreground">Configure integrations and preferences</p>
            </div>
          </div>

          {/* Spotify Integration */}
          <section className="rounded-xl border border-border bg-card">
            <div className="flex items-center gap-3 border-b border-border px-6 py-4">
              <div className="flex size-8 items-center justify-center rounded-lg bg-[#1DB954]/10">
                <Music className="size-4 text-[#1DB954]" />
              </div>
              <div className="flex-1 min-w-0">
                <h2 className="font-semibold">Spotify Integration</h2>
                <p className="text-xs text-muted-foreground">
                  Connect your Spotify account to browse playlists and liked songs
                </p>
              </div>
              {spotifyStatus?.connected ? (
                <Badge className="bg-[#1DB954]/20 text-[#1DB954] border-0">Connected</Badge>
              ) : savedCredentials?.hasClientSecret ? (
                <Badge variant="secondary">Credentials Set</Badge>
              ) : (
                <Badge variant="outline" className="text-muted-foreground">Not Configured</Badge>
              )}
            </div>

            <div className="p-6 space-y-5">
              {isLoading ? (
                <div className="flex items-center justify-center py-8">
                  <Loader2 className="size-6 animate-spin text-muted-foreground" />
                </div>
              ) : (
                <>
                  <div className="rounded-lg border border-border bg-secondary/30 p-4 text-sm">
                    <div className="flex items-start gap-2">
                      <KeyRound className="size-4 text-muted-foreground mt-0.5 shrink-0" />
                      <div>
                        <p className="font-medium mb-1">How to get your Spotify API credentials</p>
                        <ol className="text-xs text-muted-foreground space-y-1 list-decimal list-inside">
                          <li>
                            Go to the{" "}
                            <a
                              href="https://developer.spotify.com/dashboard"
                              target="_blank"
                              rel="noopener noreferrer"
                              className="text-primary hover:underline inline-flex items-center gap-0.5"
                            >
                              Spotify Developer Dashboard
                              <ExternalLink className="size-3" />
                            </a>
                          </li>
                          <li>Create a new app (or use an existing one)</li>
                          <li>
                            Add a redirect URI for the API callback. Spotify does not allow{" "}
                            <code className="text-xs bg-secondary px-1 py-0.5 rounded">localhost</code> — use loopback IP
                            (e.g.{" "}
                            <code className="text-xs bg-secondary px-1 py-0.5 rounded">
                              http://127.0.0.1:5142/api/spotify/callback
                            </code>
                            ). Match <code className="text-xs bg-secondary px-1 py-0.5 rounded">Spotify:OAuthRedirectBaseUrl</code>{" "}
                            in the API config.
                          </li>
                          <li>
                            After login, the API redirects you back to this app. With .NET Aspire (AppHost), that URL is set automatically. If you run the API without Aspire, set{" "}
                            <code className="text-xs bg-secondary px-1 py-0.5 rounded">Frontend:PublicBaseUrl</code> (or env{" "}
                            <code className="text-xs bg-secondary px-1 py-0.5 rounded">Frontend__PublicBaseUrl</code>
                            ) on the API to your Next.js origin (e.g. <code className="text-xs bg-secondary px-1 py-0.5 rounded">http://localhost:3000</code>)
                          </li>
                          <li>Copy the Client ID and Client Secret from the app settings</li>
                        </ol>
                      </div>
                    </div>
                  </div>

                  <div className="space-y-4">
                    <div className="space-y-2">
                      <Label htmlFor="client-id">Client ID</Label>
                      <Input
                        id="client-id"
                        type="text"
                        placeholder="Enter your Spotify Client ID"
                        value={clientId}
                        onChange={(e) => {
                          setClientId(e.target.value)
                          setSaveResult(null)
                        }}
                        className="font-mono text-sm"
                      />
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="client-secret">
                        Client Secret
                        {savedCredentials?.hasClientSecret && !clientSecret && (
                          <span className="text-xs text-muted-foreground ml-2 font-normal">
                            (already saved — enter a new value to update)
                          </span>
                        )}
                      </Label>
                      <Input
                        id="client-secret"
                        type="password"
                        placeholder={savedCredentials?.hasClientSecret ? "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022" : "Enter your Spotify Client Secret"}
                        value={clientSecret}
                        onChange={(e) => {
                          setClientSecret(e.target.value)
                          setSaveResult(null)
                        }}
                        className="font-mono text-sm"
                      />
                    </div>
                  </div>

                  {saveResult && (
                    <div
                      className={`flex items-center gap-2 rounded-lg border px-4 py-3 text-sm ${
                        saveResult.success
                          ? "border-[#1DB954]/50 bg-[#1DB954]/10 text-[#1DB954]"
                          : "border-destructive/50 bg-destructive/10 text-destructive"
                      }`}
                    >
                      {saveResult.success ? (
                        <CheckCircle2 className="size-4 shrink-0" />
                      ) : (
                        <AlertCircle className="size-4 shrink-0" />
                      )}
                      {saveResult.message}
                    </div>
                  )}

                  <Button
                    onClick={handleSave}
                    disabled={isSaving || !clientId.trim() || !clientSecret.trim()}
                    className="w-full sm:w-auto"
                  >
                    {isSaving ? (
                      <Loader2 className="size-4 mr-2 animate-spin" />
                    ) : (
                      <Save className="size-4 mr-2" />
                    )}
                    Save Credentials
                  </Button>
                </>
              )}
            </div>
          </section>

          {/* Danger Zone */}
          <section className="mt-8 rounded-xl border border-destructive/40 bg-card">
            <div className="flex items-center gap-3 border-b border-destructive/40 px-6 py-4">
              <div className="flex size-8 items-center justify-center rounded-lg bg-destructive/10">
                <AlertTriangle className="size-4 text-destructive" />
              </div>
              <div className="flex-1 min-w-0">
                <h2 className="font-semibold">Danger zone</h2>
                <p className="text-xs text-muted-foreground">
                  Irreversible actions that purge pipeline state. Make sure no job is running.
                </p>
              </div>
            </div>

            <div className="divide-y divide-border">
              <div className="flex flex-col gap-3 p-6 sm:flex-row sm:items-start sm:justify-between">
                <div className="flex-1 pr-4">
                  <h3 className="text-sm font-semibold">Reset enrichment data</h3>
                  <p className="mt-1 text-xs text-muted-foreground">
                    Keeps your scanned files and fingerprints. Clears enrichment results, provider attempts, lyrics, duplicate detection, and library-build status for every active song, and deletes any files that were copied to the destination folder.
                  </p>
                </div>
                <AlertDialog>
                  <AlertDialogTrigger asChild>
                    <Button
                      variant="outline"
                      className="gap-2 text-destructive hover:text-destructive shrink-0"
                      disabled={purgeRunning}
                    >
                      {purgeRunning && purgeSnapshot?.mode === "post-fingerprint" ? (
                        <Loader2 className="size-4 animate-spin" />
                      ) : (
                        <Trash2 className="size-4" />
                      )}
                      Reset enrichment data
                    </Button>
                  </AlertDialogTrigger>
                  <AlertDialogContent>
                    <AlertDialogHeader>
                      <AlertDialogTitle>Reset enrichment data?</AlertDialogTitle>
                      <AlertDialogDescription>
                        This clears every song&apos;s enrichment, lyrics, duplicate flags, and library-build state, and deletes files copied to the destination folder. Fingerprints and scan data are preserved so the next run skips straight to enrichment. This runs in the background — you can navigate away and the progress will be here when you come back. This cannot be undone.
                      </AlertDialogDescription>
                    </AlertDialogHeader>
                    <AlertDialogFooter>
                      <AlertDialogCancel>Cancel</AlertDialogCancel>
                      <AlertDialogAction onClick={() => handlePurge("post-fingerprint")}>
                        Reset enrichment data
                      </AlertDialogAction>
                    </AlertDialogFooter>
                  </AlertDialogContent>
                </AlertDialog>
              </div>

              <div className="flex flex-col gap-3 p-6 sm:flex-row sm:items-start sm:justify-between">
                <div className="flex-1 pr-4">
                  <h3 className="text-sm font-semibold">Purge all data</h3>
                  <p className="mt-1 text-xs text-muted-foreground">
                    Removes every song, provider attempt, and cached Spotify match from the database, and deletes any files copied to the destination folder. Source files are not touched. The next run re-scans and re-fingerprints from source.
                  </p>
                </div>
                <AlertDialog>
                  <AlertDialogTrigger asChild>
                    <Button
                      variant="destructive"
                      className="gap-2 shrink-0"
                      disabled={purgeRunning}
                    >
                      {purgeRunning && purgeSnapshot?.mode === "all" ? (
                        <Loader2 className="size-4 animate-spin" />
                      ) : (
                        <Trash2 className="size-4" />
                      )}
                      Purge all data
                    </Button>
                  </AlertDialogTrigger>
                  <AlertDialogContent>
                    <AlertDialogHeader>
                      <AlertDialogTitle>Purge all data?</AlertDialogTitle>
                      <AlertDialogDescription>
                        This deletes every song record, provider attempt, and cached Spotify match, and removes files that were copied to the destination folder. Source files are not affected. This runs in the background — you can navigate away and the progress will be here when you come back. This cannot be undone.
                      </AlertDialogDescription>
                    </AlertDialogHeader>
                    <AlertDialogFooter>
                      <AlertDialogCancel>Cancel</AlertDialogCancel>
                      <AlertDialogAction onClick={() => handlePurge("all")}>
                        Purge all data
                      </AlertDialogAction>
                    </AlertDialogFooter>
                  </AlertDialogContent>
                </AlertDialog>
              </div>

              {purgeStartError && (
                <div className="px-6 pb-6 pt-4">
                  <div className="flex items-start gap-2 rounded-lg border border-destructive/50 bg-destructive/10 px-4 py-3 text-sm text-destructive">
                    <AlertCircle className="size-4 shrink-0 mt-0.5" />
                    <p>{purgeStartError}</p>
                  </div>
                </div>
              )}

              {purgeSnapshot && purgeSnapshot.status !== "idle" && (
                <div className="px-6 pb-6 pt-4">
                  <PurgeStatusBanner snapshot={purgeSnapshot} />
                </div>
              )}
            </div>
          </section>
        </div>
      </div>
    </AppShell>
  )
}

function PurgeStatusBanner({ snapshot }: { snapshot: PurgeSnapshot }) {
  const modeLabel = snapshot.mode === "post-fingerprint" ? "Reset enrichment data" : "Purge all data"

  if (snapshot.status === "running") {
    const songsPct = snapshot.songsTotal > 0 ? (snapshot.songsProcessed / snapshot.songsTotal) * 100 : 0
    const filesPct =
      snapshot.filesTotal > 0
        ? ((snapshot.filesDeleted + snapshot.filesFailed) / snapshot.filesTotal) * 100
        : 0
    const overallPct = snapshot.filesTotal > 0 ? filesPct : songsPct

    return (
      <div className="flex items-start gap-2 rounded-lg border border-border bg-secondary/30 px-4 py-3 text-sm">
        <Loader2 className="size-4 shrink-0 mt-0.5 animate-spin text-muted-foreground" />
        <div className="flex-1 min-w-0">
          <p className="font-medium">{modeLabel} running…</p>
          <p className="text-xs text-muted-foreground">
            {snapshot.filesTotal > 0
              ? `${snapshot.filesDeleted.toLocaleString()} / ${snapshot.filesTotal.toLocaleString()} destination files deleted${snapshot.filesFailed > 0 ? ` (${snapshot.filesFailed.toLocaleString()} failed)` : ""}.`
              : `Preparing ${snapshot.songsTotal.toLocaleString()} songs…`}
          </p>
          <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-secondary">
            <div
              className="h-full rounded-full bg-primary transition-[width] duration-300"
              style={{ width: `${Math.min(100, overallPct).toFixed(1)}%` }}
            />
          </div>
        </div>
      </div>
    )
  }

  if (snapshot.status === "completed") {
    const prefix = snapshot.mode === "post-fingerprint" ? "Reset" : "Deleted"
    return (
      <div className="flex items-start gap-2 rounded-lg border border-[#1DB954]/50 bg-[#1DB954]/10 px-4 py-3 text-sm text-[#1DB954]">
        <CheckCircle2 className="size-4 shrink-0 mt-0.5" />
        <div>
          <p className="font-medium">{modeLabel} complete</p>
          <p className="text-xs opacity-90">
            {`${prefix} ${snapshot.songsProcessed.toLocaleString()} songs, removed ${snapshot.filesDeleted.toLocaleString()} destination files, cleared ${snapshot.spotifyMatchesCleared.toLocaleString()} Spotify matches.`}
          </p>
          {snapshot.filesFailed > 0 && (
            <p className="text-xs opacity-90 mt-1">
              {snapshot.filesFailed.toLocaleString()} file{snapshot.filesFailed === 1 ? "" : "s"} could not be deleted (see server logs).
            </p>
          )}
        </div>
      </div>
    )
  }

  // failed
  return (
    <div className="flex items-start gap-2 rounded-lg border border-destructive/50 bg-destructive/10 px-4 py-3 text-sm text-destructive">
      <AlertCircle className="size-4 shrink-0 mt-0.5" />
      <div>
        <p className="font-medium">{modeLabel} failed</p>
        <p className="text-xs opacity-90">{snapshot.error ?? "Unknown error — check server logs."}</p>
      </div>
    </div>
  )
}
