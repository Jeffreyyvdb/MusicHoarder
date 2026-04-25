import type { Metadata, Viewport } from 'next'
import { Geist, Geist_Mono } from 'next/font/google'
import { Toaster } from '@/components/ui/sonner'
import { ThemeProvider } from '@/components/theme-provider'
import { PlayerProviderWrapper } from '@/components/player/player-provider-wrapper'
import { UmamiAnalytics } from '@/components/analytics/umami-analytics'
import { DemoModeBanner } from '@/components/demo-mode-banner'
import './globals.css'

const _geist = Geist({ subsets: ["latin"] });
const _geistMono = Geist_Mono({ subsets: ["latin"] });

const SITE_URL = process.env.NEXT_PUBLIC_SITE_URL ?? 'https://musichoarder.app'
const TITLE = 'MusicHoarder — Fix Your Messy Music Library'
const DESCRIPTION =
  'Self-hosted, open-source tool that identifies, enriches, and organizes your entire music collection automatically — AcoustID fingerprinting, MusicBrainz + Spotify metadata, synced lyrics.'

export const metadata: Metadata = {
  metadataBase: new URL(SITE_URL),
  title: { default: TITLE, template: '%s — MusicHoarder' },
  description: DESCRIPTION,
  applicationName: 'MusicHoarder',
  keywords: [
    'self-hosted music library',
    'music metadata tagger',
    'music library organizer',
    'AcoustID',
    'MusicBrainz',
    'Spotify library import',
    'open source music manager',
    'music fingerprinting',
    'synced lyrics',
  ],
  authors: [{ name: 'Jeffrey van den Brink', url: 'https://github.com/Jeffreyyvdb' }],
  creator: 'Jeffrey van den Brink',
  alternates: { canonical: '/' },
  openGraph: {
    type: 'website',
    url: '/',
    siteName: 'MusicHoarder',
    title: TITLE,
    description: DESCRIPTION,
    locale: 'en_US',
  },
  twitter: {
    card: 'summary_large_image',
    title: TITLE,
    description: DESCRIPTION,
  },
  robots: {
    index: true,
    follow: true,
    googleBot: {
      index: true,
      follow: true,
      'max-image-preview': 'large',
      'max-snippet': -1,
      'max-video-preview': -1,
    },
  },
  icons: {
    icon: [{ url: '/icon.svg', type: 'image/svg+xml' }],
    apple: '/apple-icon.png',
  },
  manifest: '/manifest.webmanifest',
  category: 'technology',
}

export const viewport: Viewport = {
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#ffffff' },
    { media: '(prefers-color-scheme: dark)', color: '#0a0a0a' },
  ],
  width: 'device-width',
  initialScale: 1,
}

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode
}>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body className="font-sans antialiased">
        <ThemeProvider attribute="class" defaultTheme="system" enableSystem disableTransitionOnChange>
          <DemoModeBanner />
          <PlayerProviderWrapper>
            {children}
          </PlayerProviderWrapper>
          <Toaster position="top-center" richColors closeButton />
          <UmamiAnalytics />
        </ThemeProvider>
      </body>
    </html>
  )
}
