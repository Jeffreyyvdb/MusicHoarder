const SITE_URL = process.env.NEXT_PUBLIC_SITE_URL ?? 'https://musichoarder.app'
const REPO_URL = 'https://github.com/Jeffreyyvdb/MusicHoarder'

const softwareApplication = {
  '@context': 'https://schema.org',
  '@type': 'SoftwareApplication',
  name: 'MusicHoarder',
  applicationCategory: 'MultimediaApplication',
  applicationSubCategory: 'Music Library Manager',
  operatingSystem: 'Linux, macOS, Windows, Docker',
  url: SITE_URL,
  downloadUrl: `${REPO_URL}/releases`,
  softwareVersion: '1.0',
  license: 'https://opensource.org/licenses/MIT',
  description:
    'Self-hosted, open-source tool that identifies, enriches, and organizes your music library using AcoustID fingerprinting, MusicBrainz, and Spotify metadata.',
  offers: {
    '@type': 'Offer',
    price: '0',
    priceCurrency: 'USD',
  },
  author: {
    '@type': 'Person',
    name: 'Jeffrey van den Brink',
    url: 'https://github.com/Jeffreyyvdb',
  },
  featureList: [
    'AcoustID audio fingerprinting',
    'MusicBrainz metadata enrichment',
    'Spotify library import and matching',
    'Automatic file organization and renaming',
    'Synced lyrics fetching',
    'Duplicate detection',
    'Manual review queue for uncertain matches',
    'Docker deployment',
  ],
}

const organization = {
  '@context': 'https://schema.org',
  '@type': 'Organization',
  name: 'MusicHoarder',
  url: SITE_URL,
  logo: `${SITE_URL}/icon.svg`,
  sameAs: [REPO_URL],
}

const faqPage = {
  '@context': 'https://schema.org',
  '@type': 'FAQPage',
  mainEntity: [
    {
      '@type': 'Question',
      name: 'What is MusicHoarder?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'MusicHoarder is a self-hosted, open-source tool that identifies, enriches, and organizes your music library automatically. It uses AcoustID audio fingerprinting to identify songs regardless of filename, then pulls metadata from MusicBrainz and Spotify to build a clean, consistent library.',
      },
    },
    {
      '@type': 'Question',
      name: 'Is MusicHoarder free?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'Yes. MusicHoarder is free and open source under the MIT license. There is no paid tier or hosted version — you self-host it on your own machine or server.',
      },
    },
    {
      '@type': 'Question',
      name: 'How does MusicHoarder identify songs?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'MusicHoarder fingerprints each audio file with Chromaprint/AcoustID, then queries AcoustID, MusicBrainz, and Spotify for matching metadata. Uncertain matches are sent to a manual review queue so you stay in control.',
      },
    },
    {
      '@type': 'Question',
      name: 'Does MusicHoarder work with Spotify?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'Yes. You can connect your Spotify account to import your liked songs and playlists, and MusicHoarder will match them against your local library so you can see exactly what you already own and what is missing.',
      },
    },
    {
      '@type': 'Question',
      name: 'Do I need to self-host MusicHoarder?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'Yes. MusicHoarder is designed to run on your own infrastructure. It ships with Docker support so you can spin it up in a single command alongside the required PostgreSQL database.',
      },
    },
    {
      '@type': 'Question',
      name: 'What tech stack does MusicHoarder use?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'A .NET ASP.NET Core API with a Next.js (React) frontend, orchestrated via .NET Aspire. Data is persisted in PostgreSQL. Audio fingerprinting uses Chromaprint/fpcalc.',
      },
    },
  ],
}

export function StructuredData() {
  return (
    <>
      <script
        type="application/ld+json"
        // eslint-disable-next-line react/no-danger
        dangerouslySetInnerHTML={{ __html: JSON.stringify(softwareApplication) }}
      />
      <script
        type="application/ld+json"
        // eslint-disable-next-line react/no-danger
        dangerouslySetInnerHTML={{ __html: JSON.stringify(organization) }}
      />
      <script
        type="application/ld+json"
        // eslint-disable-next-line react/no-danger
        dangerouslySetInnerHTML={{ __html: JSON.stringify(faqPage) }}
      />
    </>
  )
}
