import { ImageResponse } from 'next/og'

export const runtime = 'edge'
export const alt = 'MusicHoarder — Fix Your Messy Music Library'
export const size = { width: 1200, height: 630 }
export const contentType = 'image/png'

export default async function Image() {
  return new ImageResponse(
    (
      <div
        style={{
          height: '100%',
          width: '100%',
          display: 'flex',
          flexDirection: 'column',
          justifyContent: 'center',
          padding: '80px',
          backgroundColor: '#0a0a0a',
          backgroundImage:
            'radial-gradient(circle at 25% 25%, rgba(139, 92, 246, 0.18), transparent 55%), radial-gradient(circle at 80% 80%, rgba(236, 72, 153, 0.12), transparent 55%)',
          color: '#ffffff',
        }}
      >
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: '16px',
            padding: '10px 20px',
            borderRadius: '9999px',
            border: '1px solid rgba(255, 255, 255, 0.15)',
            backgroundColor: 'rgba(255, 255, 255, 0.04)',
            fontSize: '22px',
            color: 'rgba(255, 255, 255, 0.7)',
            alignSelf: 'flex-start',
            marginBottom: '40px',
          }}
        >
          <div
            style={{
              width: '10px',
              height: '10px',
              borderRadius: '9999px',
              backgroundColor: '#8b5cf6',
            }}
          />
          Self-hosted & Open Source
        </div>

        <div
          style={{
            fontSize: '88px',
            fontWeight: 800,
            lineHeight: 1.05,
            letterSpacing: '-0.03em',
            display: 'flex',
            flexDirection: 'column',
          }}
        >
          <span>Your music library is a mess.</span>
          <span style={{ color: '#a78bfa', marginTop: '8px' }}>MusicHoarder fixes it.</span>
        </div>

        <div
          style={{
            marginTop: '40px',
            fontSize: '28px',
            color: 'rgba(255, 255, 255, 0.65)',
            maxWidth: '900px',
            lineHeight: 1.4,
          }}
        >
          Identifies, enriches, and organizes your entire collection automatically.
          AcoustID, MusicBrainz, Spotify, synced lyrics.
        </div>

        <div
          style={{
            position: 'absolute',
            bottom: '64px',
            left: '80px',
            right: '80px',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            fontSize: '22px',
            color: 'rgba(255, 255, 255, 0.5)',
          }}
        >
          <span>musichoarder.app</span>
          <span>MIT License · .NET + Next.js</span>
        </div>
      </div>
    ),
    size,
  )
}
