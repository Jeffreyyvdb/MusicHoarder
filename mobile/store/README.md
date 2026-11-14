# Store declarations

What MusicHoarder has told the app stores about itself, and why. The stores hold these answers in
their consoles, where nobody sees them while writing code, so they are recorded here. They describe
the code: a feature that changes what the app collects, shows or lets people do can make one of
them false. A false declaration is grounds for rejection or removal.

**Before merging a change, check the table under [When a change touches the declarations](#when-a-change-touches-the-declarations).**
If the change affects a declaration, update the console and this file in the same change.

| Path | What |
| --- | --- |
| `play/data-safety.csv` | Google Play's Data safety form as an export. Play Console → App content → Data safety → Import from CSV restores it. |
| `play/short-description.txt`, `play/full-description.txt` | The default store listing text. |

The listing images (512 px icon, 1024×500 feature graphic, four 1440×2560 phone screenshots) are
not in the repository. They were captured from the demo account on musichoarder.app in a headless
Chromium phone viewport (360×640 at device scale 4, Roboto injected because Android's `system-ui`
is Roboto, cover `?size=` raised to 1600 so covers are not upscaled thumbnails). The icon is
`frontend/static/icon-maskable-512.png`. A feature graphic needs large phone mockups that bleed off
the edge: small ones make every line of text a few pixels tall.

## Google Play (`com.musichoarder.app`)

Recorded 2026-11-14, when the app was first set up in Play Console.

### Account and distribution

- **Trader status: non-trader.** The developer account earns no money from the app. Switch to
  trader *before* any income (a paid tier, in-app purchases, ads). As a trader, Play shows a
  verified address, phone number and email to everyone in the EU, so set up a business address
  first.
- **Release track:** `release.yml` uploads every release's App Bundle to the track named by the
  `ANDROID_PLAY_TRACK` repo variable, now `alpha` (closed testing), so closed-test testers get
  every release.
- **Production access** needs a closed test with at least 12 testers opted in for 14 days in a row
  (a rule for new personal developer accounts). Testers come from a Google Group.
- **Pre-launch reports** are not generated for internal-track uploads; closed testing produces them.

### App content

**Privacy policy:** `https://musichoarder.app/privacy`. It covers the website, the phone app and
self-hosted instances.

**Sign-in details** (Play's reviewers use these for every review, so the demo must keep working):
restricted, one set named "Demo account (no password)", with no username or password, and these
instructions: enter `musichoarder.app` on the first screen, tap Continue, tap **Try the demo**. The
demo is read-only and shared.

**Ads:** none.

**Advertising ID:** the app does not use it. No dependency declares
`com.google.android.gms.permission.AD_ID` (there is no Play services or ads SDK, and the
`google-services` plugin only applies when a `google-services.json` exists, which it does not).
Play refuses every upload from an app targeting API 33+ until this form is answered.

**Content rating** (IARC questionnaire, category *All other app types*). The app ships no media of
its own, but it streams a library, lyrics and music videos, and Play counts that as the app's
catalogue. So the questionnaire answers for what a music library can contain:

| Topic | Answers | Why |
| --- | --- | --- |
| Downloaded with the app | no rated content | The package is code, an icon and the connect page. |
| User content sharing | users interact: yes; primary source: no; public nudity or violence: no; block, report, moderation: no; **limited to invited friends: yes** | Chats and share links. Accounts exist only through an admin's email-bound invite. |
| Online content | yes | Music, covers, lyrics and videos come from the server. |
| Violence | can contain; not the purpose; shown visually: yes; **gory: no**; not limited to cartoon violence; in text or speech: yes | Lyrics and live-action music videos. |
| Sexuality | can contain; not the purpose; shown visually: yes; **nudity: no; pornographic: no**; in text or speech: yes | Suggestive music videos and lyrics. |
| Language | everything yes (mild, moderate, discriminatory, sexual expletives); not the focus | Uncensored lyrics. |
| Controlled substances | can contain; not the focus; shown visually: yes; in text or speech: yes | Lyrics and videos. |
| Age-restricted products, location sharing, digital purchases, cash or crypto rewards, browser, news or educational | no | |

"No gore" and "no nudity" rest on one fact: music videos are fetched from YouTube **without
cookies**, so videos behind YouTube's sign-in age gate cannot be downloaded. If the video fetcher
ever signs in, both answers become yes and the rating goes to 18+.

Result: ESRB Teen, PEGI Parental guidance, USK 12, IARC 12+, ClassInd 16+. (Answering yes to gore,
nudity or pornography gives 18+ everywhere and ESRB *Adults Only*.)

**Target audience:** 18 and over. Nothing under 13, which would bring in the Families policy.

**Data safety** (`play/data-safety.csv`). Everything is *collected* (stored on the server). Nothing
is *shared*: Resend (sign-in emails) and Cloudflare are service providers, and Umami is self-hosted.

| Data type | Required? | Purposes | Comes from |
| --- | --- | --- | --- |
| Name | optional | app functionality, account management | the account; shown in Chats |
| Email address | required | app functionality, account management | sign-in links, invites |
| User IDs | required | app functionality, account management | the account id |
| Other in-app messages | optional | app functionality | Chats |
| App interactions | required | app functionality, analytics, personalisation | plays and likes (radio ranks on them), Umami page views |
| Other user-generated content | optional | app functionality | playlists |
| Diagnostics | required | analytics | Umami performance timings |
| Device or other IDs | required | app functionality | playback sync's per-device id (`mh:device-id`) |

Also declared: encrypted in transit; account creation by *username and other authentication*
(email link or passkey, no password); users can request deletion of the whole account or of
part of their data, at `https://musichoarder.app/delete-account`. The CSV import rejects
`PSL_HAS_OUTSIDE_APP_ACCOUNTS` for an app that creates accounts in the app, so leave it blank.

**Financial features, health, government:** none.

### Store settings and listing

- **Category:** Music & audio. **Tags:** Lyrics, Music & audio, Video player. There is no "music
  player" tag. "Radio" means internet radio stations on Play, which is not what the app's radio is.
- **Contact:** a project email (public), website `https://musichoarder.app`, no phone number.
- **AI asset declaration:** don't label. The images are real screenshots, the repo's icon and an
  HTML layout of both.
- **The listing must say a server is needed** and point to the demo, and keep the line that the app
  does not sell or stream music, only plays files the user already owns. Without it, the app reads
  as a streaming or piracy app.

## When a change touches the declarations

| If a change… | Update |
| --- | --- |
| stores a new kind of personal data, or sends one to a new third party (an analytics or crash SDK, a new email or AI provider receiving user data) | Data safety (and `play/data-safety.csv`), the privacy page, Apple's App Privacy |
| adds a way for people to post or exchange content (comments, public profiles, image uploads in Chats) | Content rating "User content sharing"; Apple requires reporting and blocking for this (below) |
| adds blocking, reporting or moderation to Chats | Content rating: answer yes |
| makes the video fetcher sign in to YouTube, or adds any other media source | Content rating (gore and nudity answers), probably 18+ |
| adds purchases, subscriptions, ads or any income | Trader status, Ads, Financial features, Data safety (purchase history, payment info) |
| asks for a new Android permission (location, contacts, microphone, files) | Data safety, the privacy page |
| changes how sign-in works (passwords, OAuth sign-in, Sign in with Google or Apple) | Data safety account-creation method, Play sign-in details, Apple's review notes |
| breaks or restricts the demo account or "Try the demo" | Play sign-in details and Apple's review notes, **before** the next review |
| adds real account deletion | the `/delete-account` page (it describes deletion by hand within 30 days) |
| changes what the demo library contains | Content rating, if it adds content the answers above do not cover |

## Apple App Store (later)

Apple has not been submitted to yet (only TestFlight; see `../README.md`). Most answers carry over,
but App Review is stricter in places where this app currently falls short:

- **App Privacy ("nutrition label").** Same data as Data safety. Apple's names differ: Contact Info
  (name, email), Identifiers (user ID, device ID), User Content (in-app messages, other user
  content), Usage Data (product interaction), Diagnostics (performance data). All *linked to the
  user*, none *used for tracking*, so no App Tracking Transparency prompt is needed.
- **Age rating.** Answer as on Play: frequent or intense profanity, mature or suggestive themes,
  drug references and realistic violence in the streamed catalogue; no unrestricted web access;
  user-generated content: yes. Expect a high rating (16+ or 18+ under Apple's tiers).
- **User-generated content (guideline 1.2)** requires a way to report objectionable content, to
  block abusive users and to filter content. Chats has none of these. Expect a rejection until it
  does, or until Chats is hidden in the iOS app.
- **Account deletion (5.1.1(v))** has to be *started inside the app*. A web page is allowed only if
  the app links straight to it. Add a "Delete account" entry in the app's settings that opens
  `/delete-account`.
- **Minimum functionality (4.2).** A web view around a website is a common rejection. The case for
  this app is the native audio engine (an `AVQueuePlayer` that keeps playing on the lock screen),
  which a web page cannot do. Say so in the review notes.
- **Review notes:** the same demo instructions as Play's sign-in details.
