"use client"

import { useState, useEffect } from "react"
import { AppHeader } from "@/components/app-header"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Badge } from "@/components/ui/badge"
import {
  fetchSpotifyCredentials,
  saveSpotifyCredentials,
  fetchSpotifyStatus,
} from "@/lib/api-client"
import type { SpotifyCredentialsResponse, SpotifyStatusResponse } from "@/lib/api-client"
import {
  Settings,
  KeyRound,
  Save,
  Loader2,
  CheckCircle2,
  AlertCircle,
  Music,
  ExternalLink,
} from "lucide-react"

export default function SettingsPage() {
  const [clientId, setClientId] = useState("")
  const [clientSecret, setClientSecret] = useState("")
  const [isSaving, setIsSaving] = useState(false)
  const [isLoading, setIsLoading] = useState(true)
  const [savedCredentials, setSavedCredentials] = useState<SpotifyCredentialsResponse | null>(null)
  const [spotifyStatus, setSpotifyStatus] = useState<SpotifyStatusResponse | null>(null)
  const [saveResult, setSaveResult] = useState<{ success: boolean; message: string } | null>(null)

  useEffect(() => {
    async function load() {
      setIsLoading(true)
      try {
        const [creds, status] = await Promise.all([
          fetchSpotifyCredentials().catch(() => ({ clientId: null, hasClientSecret: false }) as SpotifyCredentialsResponse),
          fetchSpotifyStatus().catch(() => ({ connected: false, hasCredentials: false, tokenExpired: false }) as SpotifyStatusResponse),
        ])
        setSavedCredentials(creds)
        setSpotifyStatus(status)
        if (creds.clientId) {
          setClientId(creds.clientId)
        }
      } catch {
        // Settings page should load even if API is down
      } finally {
        setIsLoading(false)
      }
    }
    load()
  }, [])

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
    <div className="flex h-screen flex-col bg-background">
      <AppHeader />

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
        </div>
      </div>
    </div>
  )
}
