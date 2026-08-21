import type { ProsePage } from './types';

const REPO_URL = 'https://github.com/Jeffreyyvdb/MusicHoarder';

export const aboutPage: ProsePage = {
  path: '/about',
  title: 'About MusicHoarder',
  description:
    'MusicHoarder is a free, MIT-licensed, self-hosted pipeline that fingerprints, identifies, enriches and reorganizes a messy music library into clean files on your own disk.',
  updated: '2026-08-21',
  sections: [
    {
      heading: 'What MusicHoarder is',
      blocks: [
        {
          kind: 'paragraph',
          text: 'MusicHoarder is an open-source application that turns a folder of badly named, half-tagged audio files into a clean, consistently tagged music library. You point it at a source folder and a destination folder. It fingerprints every track with Chromaprint/AcoustID, so a file called "track03 (1).mp3" is identified by how it sounds rather than by what it is called, asks several metadata providers who agrees on the answer, grades the result, and writes a tidy copy to the destination as plain files: Artist / Year - Album / NN - Track.'
        },
        {
          kind: 'paragraph',
          text: 'The source folder is never modified. MusicHoarder only reads it and writes new copies elsewhere, so a bad match costs you a rebuild and never your originals. Matches the pipeline is not confident about are not guessed into your library — they land in a human review Inbox where you approve, correct or reject them.'
        }
      ]
    },
    {
      heading: 'How it works',
      blocks: [
        {
          kind: 'list',
          items: [
            'Scan: the source directory is indexed, and rescanned on a timer so files copied onto the share are picked up without a manual step.',
            'Fingerprint: each track gets a Chromaprint acoustic fingerprint plus an exact duration.',
            'Enrich: AcoustID, MusicBrainz, Spotify, Deezer, Apple Music and community trackers are queried in parallel, and a consensus is computed across whichever providers you enabled.',
            'Grade: an LLM scores the proposed match against the file it came from, so a confident-looking wrong answer still gets flagged.',
            'Dedupe: duplicates are detected by fingerprint, and the highest-quality copy wins.',
            'Build: a clean, correctly tagged copy is written to the destination folder, with cover art and synced lyrics where they are available.'
          ]
        },
        {
          kind: 'paragraph',
          text: 'Everything runs on your hardware. There is no MusicHoarder cloud, no account to buy and no upload step — the only things that leave your machine are metadata lookups against the public music databases listed above.'
        }
      ]
    },
    {
      heading: 'Who builds it',
      blocks: [
        {
          kind: 'paragraph',
          text: 'MusicHoarder is built and maintained by Jeffrey van den Brink as an open-source side project, in public, under the MIT license. Every line of the API, the web frontend, the Android client and the deployment configuration is in the public repository. Releases are cut automatically from Conventional Commits, and the GitHub Releases page is the canonical changelog.'
        },
        {
          kind: 'paragraph',
          text: 'This site, musichoarder.app, is the project home page. It also hosts a read-only demo account so you can walk through a real library — the Inbox, the match grades, the album pages — before you install anything.'
        },
        {
          kind: 'links',
          items: [
            { label: 'Source code on GitHub', href: REPO_URL, note: 'MIT licensed, issues open' },
            { label: 'Releases and changelog', href: `${REPO_URL}/releases` },
            {
              label: 'Self-hosting quickstart',
              href: `${REPO_URL}#readme`,
              note: 'Docker Compose, no repo clone needed'
            },
            { label: 'Machine-readable pricing', href: '/pricing.md', note: 'it is free' }
          ]
        }
      ]
    },
    {
      heading: 'Cost and licensing',
      blocks: [
        {
          kind: 'paragraph',
          text: 'MusicHoarder costs nothing. There is no paid tier, no hosted plan and no "contact sales" wall, and there is no plan to add one — it operates on your audio files, which is exactly the kind of data that should stay on your own infrastructure. The software is MIT licensed, so you may use, modify and redistribute it commercially. Some providers it talks to have their own free-tier terms: AcoustID and Spotify need your own API credentials, and MusicBrainz is rate limited.'
        }
      ]
    }
  ]
};

export const contactPage: ProsePage = {
  path: '/contact',
  title: 'Contact MusicHoarder',
  description:
    'How to reach the MusicHoarder project: GitHub issues for bugs and features, private security advisories for vulnerabilities, and pull requests for contributions.',
  updated: '2026-08-21',
  sections: [
    {
      heading: 'Where to reach us',
      blocks: [
        {
          kind: 'paragraph',
          text: 'MusicHoarder is an open-source project rather than a company, so every support channel is public and lives on GitHub. There is no sales team and no support contract — but issues are read, and a reproducible bug report is the fastest way to get something fixed.'
        },
        {
          kind: 'links',
          items: [
            {
              label: 'Report a bug or request a feature',
              href: `${REPO_URL}/issues`,
              note: 'the main channel; include your version, logs and steps to reproduce'
            },
            {
              label: 'Report a security vulnerability',
              href: `${REPO_URL}/security`,
              note: 'private advisory — please do not use a public issue'
            },
            {
              label: 'Contribute a change',
              href: `${REPO_URL}/pulls`,
              note: 'pull requests welcome; see CONTRIBUTING.md in the repository'
            },
            {
              label: 'Maintainer',
              href: 'https://github.com/Jeffreyyvdb',
              note: 'Jeffrey van den Brink'
            }
          ]
        }
      ]
    },
    {
      heading: 'Before you open an issue',
      blocks: [
        {
          kind: 'paragraph',
          text: 'MusicHoarder is self-hosted, which means most problems are reproducible only with a little context from your deployment. Including the following turns a report into a fix instead of a conversation:'
        },
        {
          kind: 'list',
          items: [
            'The release version you are running, from the footer of your instance or the Releases page.',
            'How you deployed: Docker Compose, a build from source, or the Aspire dev stack.',
            'What you expected to happen and what happened instead, with the relevant API or container logs.',
            'For a matching problem: the file involved, the provider verdicts shown in the Inbox, and whether fpcalc and an AcoustID key are configured.'
          ]
        },
        {
          kind: 'paragraph',
          text: 'Please do not include secrets in an issue. API keys, database passwords and magic-link URLs are all worth redacting before you paste a log.'
        }
      ]
    },
    {
      heading: 'Response times',
      blocks: [
        {
          kind: 'paragraph',
          text: 'This is a side project maintained in spare time, so support is best effort. Security reports get priority and usually receive an initial response within a few days. Bug reports and feature requests are triaged in the open on the issue tracker, and there is no private queue that jumps ahead of it.'
        }
      ]
    }
  ]
};

export const privacyPage: ProsePage = {
  path: '/privacy',
  title: 'Privacy at MusicHoarder',
  description:
    'What musichoarder.app collects (privacy-friendly self-hosted analytics and a session cookie) and what a self-hosted MusicHoarder instance does with your music, metadata and credentials.',
  updated: '2026-08-21',
  sections: [
    {
      heading: 'The short version',
      blocks: [
        {
          kind: 'paragraph',
          text: 'MusicHoarder does not sell data, does not run advertising, and has no third-party trackers beyond the self-hosted analytics described below. Your music never leaves your machine: the software runs entirely on your own hardware, and this website does not receive, store or process any audio file. There is no public sign-up, so there is no user database of visitors to leak.'
        }
      ]
    },
    {
      heading: 'This website (musichoarder.app)',
      blocks: [
        {
          kind: 'paragraph',
          text: 'This site is the project home page plus a read-only demo of the application. It processes the following:'
        },
        {
          kind: 'list',
          items: [
            'Analytics: a self-hosted Umami instance (umami.jeffreyyvdb.com), operated by the maintainer rather than by an advertising company. It records page views and basic performance timings, and it includes a session recorder with moderate input masking and a five-minute cap so UI problems can be diagnosed. Umami does not use tracking cookies and does not build cross-site profiles.',
            'Session cookie: starting the demo sets a single first-party cookie named mh_session, which identifies your demo session and nothing else. It is removed when you sign out and expires on its own.',
            'Server and edge logs: the site is served through Cloudflare, so requests carry the usual technical data — IP address, user agent, requested path — which is used for delivery, abuse prevention and debugging.',
            'Email: an address is only ever processed if you enter one to request a magic sign-in link, in which case it is passed to Resend to deliver that one email. The demo does not require an email address.'
          ]
        },
        {
          kind: 'paragraph',
          text: 'The demo account is read-only and shared: anything you type into it may be visible to other people trying the demo, and it is reset from a fixed seed. Do not put personal information into it.'
        }
      ]
    },
    {
      heading: 'Your self-hosted instance',
      blocks: [
        {
          kind: 'paragraph',
          text: 'When you run MusicHoarder yourself, you are the data controller and the maintainer has no access to anything. Your audio files, your PostgreSQL database and your API credentials stay on your infrastructure. The maintainer receives no telemetry from installed instances — there is no phone-home, no usage beacon and no crash reporting built into the application.'
        },
        {
          kind: 'paragraph',
          text: 'Your instance does talk to third parties on your behalf, and only for metadata:'
        },
        {
          kind: 'list',
          items: [
            'Fingerprints and track durations go to AcoustID, and release lookups go to MusicBrainz, to identify songs.',
            'Spotify, Deezer, Apple Music and the Cover Art Archive are queried for metadata, artwork and album details. Connecting your own Spotify account through OAuth is optional and only used to import your liked songs and playlists.',
            'LRCLIB is queried for synced lyrics.',
            'If you enable AI match grading or AI lyrics transcription, the corresponding request is sent to whichever OpenAI-compatible endpoint you configured, using your own key.'
          ]
        },
        {
          kind: 'paragraph',
          text: 'Every one of those providers is optional, and each can be turned off in configuration. With them all disabled, MusicHoarder makes no outbound requests at all.'
        }
      ]
    },
    {
      heading: 'Your choices and contact',
      blocks: [
        {
          kind: 'paragraph',
          text: 'You can browse this site with an ad or tracker blocker and everything except the demo will work normally; the analytics script fails closed. To remove the demo session cookie, sign out or clear cookies for this domain. Because there is no visitor account system, there is no stored profile to export or delete — a request to erase analytics data can be made through the contact channels below and will be honoured.'
        },
        {
          kind: 'links',
          items: [
            { label: 'Privacy questions and data requests', href: '/contact' },
            { label: 'Security disclosure policy', href: `${REPO_URL}/security` },
            {
              label: 'Read the source',
              href: REPO_URL,
              note: 'every claim on this page is verifiable in the repository'
            }
          ]
        }
      ]
    }
  ]
};

/** Every prose page, in the order they should appear in a sitemap or navigation list. */
export const prosePages: ProsePage[] = [aboutPage, contactPage, privacyPage];
