import { env } from '$env/dynamic/public';

const SITE_URL = env.PUBLIC_SITE_URL ?? 'https://musichoarder.app';
const REPO_URL = 'https://github.com/Jeffreyyvdb/MusicHoarder';

/**
 * SoftwareApplication JSON-LD. Built as a factory so the schema's `softwareVersion` reflects the
 * running build's real semver (passed in from `page.data.appVersion`) instead of a stale literal.
 */
export function buildSoftwareApplicationSchema(version?: string | null) {
  return {
    '@context': 'https://schema.org',
    '@type': 'SoftwareApplication',
    name: 'MusicHoarder',
    applicationCategory: 'MultimediaApplication',
    applicationSubCategory: 'Music Library Manager',
    operatingSystem: 'Linux, macOS, Windows, Docker',
    url: SITE_URL,
    downloadUrl: `${REPO_URL}/releases`,
    softwareVersion: version ?? undefined,
    license: 'https://opensource.org/licenses/MIT',
    description:
      'Self-hosted, open-source pipeline that identifies, enriches, and organizes a messy music library. It fingerprints every track with Chromaprint/AcoustID, reaches consensus across seven metadata providers, grades each match with a quality LLM, deduplicates by fingerprint, and writes clean files to your own disk.',
    offers: {
      '@type': 'Offer',
      price: '0',
      priceCurrency: 'USD'
    },
    author: {
      '@type': 'Person',
      name: 'Jeffrey van den Brink',
      url: 'https://github.com/Jeffreyyvdb'
    },
    featureList: [
      'AcoustID / Chromaprint audio fingerprinting',
      'Multi-provider metadata consensus (AcoustID, Spotify, MusicBrainz, Deezer, Apple Music, community trackers)',
      'AI quality grading of every match (score + verdict)',
      'Fingerprint-based duplicate detection that keeps the highest-quality copy',
      'Synced lyrics via LRCLIB and artwork via the Cover Art Archive',
      'Non-destructive: source is mounted read-only, clean copies written elsewhere',
      'Plain files on disk — Artist / Year - Album / NN - Track, no proprietary database',
      'Human-review Inbox for uncertain matches',
      'Self-hosted via Docker Compose'
    ]
  };
}

export const organizationSchema = {
  '@context': 'https://schema.org',
  '@type': 'Organization',
  name: 'MusicHoarder',
  url: SITE_URL,
  logo: `${SITE_URL}/icon.svg`,
  sameAs: [REPO_URL]
};

export const faqPageSchema = {
  '@context': 'https://schema.org',
  '@type': 'FAQPage',
  mainEntity: [
    {
      '@type': 'Question',
      name: 'What is MusicHoarder?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'MusicHoarder is a self-hosted, open-source tool that identifies, enriches, and organizes your music library automatically. It fingerprints every audio file with Chromaprint/AcoustID to identify songs regardless of filename, reaches consensus across several metadata providers, grades each match with an LLM, deduplicates, and writes a clean library of plain files to your own disk.'
      }
    },
    {
      '@type': 'Question',
      name: 'Is MusicHoarder free?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'Yes. MusicHoarder is free and open source under the MIT license. There is no paid tier — you self-host it on your own machine or server.'
      }
    },
    {
      '@type': 'Question',
      name: 'How does MusicHoarder identify songs?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'Each file is fingerprinted with Chromaprint/AcoustID, then seven providers are queried in parallel — AcoustID, Spotify, MusicBrainz, Deezer, Apple Music, and two community trackers. MusicHoarder reaches a consensus across them and a quality LLM grades the result; anything uncertain is sent to a manual review Inbox so you stay in control.'
      }
    },
    {
      '@type': 'Question',
      name: 'How do I self-host MusicHoarder?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'Clone the repo, copy .env.example to .env (set your source/destination folders, a Postgres password, and owner email), then run "docker compose up -d --build". It starts PostgreSQL, the API on port 5050, and the web UI on port 3000. Prebuilt images are also published to GHCR for a Dokploy-style deploy.'
      }
    },
    {
      '@type': 'Question',
      name: 'Does MusicHoarder modify my original files?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'No. The source folder is mounted read-only. MusicHoarder reads your files, writes a clean, tagged copy to a separate destination folder (Artist / Year - Album / NN - Track), and never touches the originals.'
      }
    },
    {
      '@type': 'Question',
      name: 'What tech stack does MusicHoarder use?',
      acceptedAnswer: {
        '@type': 'Answer',
        text: 'A .NET ASP.NET Core API with a SvelteKit (Svelte 5) frontend, orchestrated via .NET Aspire. Data is persisted in PostgreSQL. Audio fingerprinting uses Chromaprint/fpcalc, and AI quality grading runs against any OpenAI-compatible endpoint.'
      }
    }
  ]
};
