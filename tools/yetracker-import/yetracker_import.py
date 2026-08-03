#!/usr/bin/env python3
"""Normalize the scraped yetracker (Kanye West community tracker) Google-Sheets CSV export
into a single lean JSON catalog for MusicHoarder's enrichment pipeline.

This is an OFFLINE, one-off tool. The raw sheets and their download URLs are NOT committed to
the repo; only the normalized output (MusicHoarder.Api/Data/yetracker.json) is. Re-run this when
the tracker is re-scraped.

Usage:
    python3 tools/yetracker-import/yetracker_import.py <raw_csv_dir> [out_json]

    <raw_csv_dir>  directory containing the raw sheet CSVs. Either bare sheet names
                   ("Unreleased.csv") or the names Google's own export produces
                   ("Copy of Suzy Tracker WE LOVE MILO - Unreleased.csv") — the part after the
                   last " - " is what identifies the sheet, so downloads need no renaming.
    [out_json]     output path (default: MusicHoarder.Api/Data/yetracker.json)

Output: a JSON array of objects:
    { "title", "altTitles": [...], "era", "category", "producers", "durationSeconds", "year",
      "availability", "quality", "version", "ogFilenames": [...] }

Download links and the bulk of the free-text notes are dropped; the runtime
YeTrackerCatalogService deserializes this directly into TrackerSong records.

Exits non-zero if a sheet that is present on disk yields no songs. The tracker's headers drift
(the Unreleased sheet's "Name" column became "Name\n(Join The Discord!)"), and a silent empty
sheet would otherwise overwrite the committed catalog with a truncated one.
"""

import collections
import csv
import json
import os
import re
import sys

ParsedName = collections.namedtuple(
    "ParsedName", "title alt_titles producers featured references version")

# Sheets that contain individual songs. Everything else (Key, Template, Samples, Music Videos,
# Groupbuys, Lost Media, Unwanted) is metadata, non-audio, or too small to be worth matching.
# Art and Tracklists are songs-adjacent and get their own catalogs — see ART_SHEET/TRACKLIST_SHEET.
SONG_SHEETS = [
    "Unreleased",
    "Recent",
    "Released",
    "Special",
    "Best Of",
    "Worst Of",
    "Stems",
    "Misc",
    "Album Copies",
    "SSC",
    "Remixes",
    "Grails_Wanted",
    # Known AI fakes. Imported deliberately: they're flagged aiGenerated so the matcher can
    # recognise one instead of leaving the file to mis-match some real song.
    "AI",
]

# Sheets whose rows are the AI tab — every row is a fake, marked with 🤖 in the tracker.
AI_SHEETS = {"ai"}

# Sheets whose rows are themselves a released recording, so a Spotify link on the row identifies
# *that* recording. Everywhere else the link points at the release a leak/stem derives from, and
# adopting its id would let the derivative claim the release's identity.
SPOTIFY_ID_SHEETS = {"released", "remixes"}

TRACKLIST_SHEET = "Tracklists"
ART_SHEET = "Art"

# Header label -> logical field. Mapped case-insensitively, trimmed. First match wins.
ERA_HEADERS = {"era", "main era"}
NAME_HEADERS = {"name", "main content"}
LENGTH_HEADERS = {"track length", "tracklength", "length", "full length", "copy length", "image / length"}
DATE_HEADERS_PREFERRED = {"leak date", "leakdate", "release date"}      # when the song became available
DATE_HEADERS_FALLBACK = {"file date", "filedate", "date made"}          # when it was recorded
AVAILABILITY_HEADERS = {"available length", "availablelength"}          # how much of the song circulates
QUALITY_HEADERS = {"quality"}
NOTES_HEADERS = {"notes"}
ARTIST_HEADERS = {"artist(s)", "artists", "artist"}                     # Remixes: the performer isn't Ye
TYPE_HEADERS = {"type"}                                                 # Released: Album Track / Feature / Production / …
LINK_HEADERS = {"link(s)", "links", "link"}
TRACKLIST_HEADERS = {"tracklist"}
DESIGNER_HEADERS = {"designer"}
ART_TYPE_HEADERS = {"art type"}
PROJECT_TYPE_HEADERS = {"project type"}
IMAGE_HEADERS = {"image"}

# Era-banner rows put aggregate stats in column A, e.g. "45 Full1 Tagged5 Partial77 Unavailable".
BANNER_RE = re.compile(
    r"\d+\s*(?:Full|Snippet|Unavailable|Tagged|Partial|Recording|OG File|Stem Bounce|"
    r"Album Track|Single|Feature|Production|Other|Beat Only|LQ)",
    re.IGNORECASE,
)
# Leading emoji / symbol tags used by the tracker (AI, best-of, grail, special, ...).
EMOJI_RE = re.compile(r"^[\U0001F000-\U0001FAFF☀-➿️⭐✨⁉\s]+")
LEN_RE = re.compile(r"^\d{1,2}:\d{2}(?::\d{2})?$")
YEAR_RE = re.compile(r"\b(?:19|20)\d{2}\b")
VERSION_RE = re.compile(r"\s*\[[Vv]\d+[^\]]*\]")
# The version ordinal inside that marker — the main disambiguator between same-title leaks.
VERSION_NUM_RE = re.compile(r"\[[Vv](\d+)")
# "OG Filename: <name>" in the Notes column: the name the leaked file itself carries. Label
# capitalisation and the colon are both inconsistent across the sheet.
OG_LABEL_RE = re.compile(r"OG\s*File\s*names?\s*:?[ \t]*", re.IGNORECASE)
SPOTIFY_TRACK_RE = re.compile(r"open\.spotify\.com/track/([A-Za-z0-9]{22})")
# A numbered line in a Tracklists cell: "1. FRIED", "12) Hurricane".
TRACKLIST_LINE_RE = re.compile(r"^\s*(\d{1,3})\s*[.)]\s+(.+?)\s*$")
# Tracklist rows that document a concert setlist rather than an album's running order.
SETLIST_RE = re.compile(r"\bset\s?list\b", re.IGNORECASE)
# A parenthetical that is a credit (vs. an alt-title or a mix descriptor).
CREDIT_PAREN_RE = re.compile(r"\((?:feat\.|ref\.|prod\.|with\s|\?\?\?)", re.IGNORECASE)


class SheetParseError(Exception):
    """A sheet is present but can't be understood — abort rather than write a truncated catalog."""


def parse_length(value):
    if not value:
        return None
    value = value.strip()
    if not LEN_RE.match(value):
        return None
    total = 0
    for part in value.split(":"):
        total = total * 60 + int(part)
    return total if total > 0 else None


def parse_year(*values):
    for value in values:
        if not value:
            continue
        m = YEAR_RE.search(value)
        if m:
            return int(m.group(0))
    return None


def split_paren_groups(text):
    """Return (head, [group, ...]) splitting top-level parenthetical groups off the tail.

    head is the text up to the first credit paren (feat./ref./prod./with/???); the remaining
    parenthetical groups are returned individually. Descriptive parens that are part of the title
    (e.g. "(E-Smoove Soul Mix)") stay in head because they precede any credit paren.
    """
    m = CREDIT_PAREN_RE.search(text)
    if m:
        head = text[: m.start()].strip()
        tail = text[m.start():]
    else:
        # No credits: peel only a single trailing (...) group as a possible alt-title.
        tm = re.search(r"\(([^()]*)\)\s*$", text)
        if tm and text[: tm.start()].strip():
            return text[: tm.start()].strip(), [tm.group(0)]
        return text.strip(), []

    groups = re.findall(r"\([^()]*\)", tail)
    return head, groups


def parse_name(raw):
    """Parse the packed Name column into (title, alt_titles, producers, version).

    raw example:
      "Playboi Carti - Headshot [V2](feat. Kanye West) (prod. Richie Souf)(Heads Off, Headshots)"
    """
    name = EMOJI_RE.sub("", raw).strip()
    vm = VERSION_NUM_RE.search(name)
    version = int(vm.group(1)) if vm else None
    head, groups = split_paren_groups(name)

    producers = []
    featured = []
    references = []
    alt_titles = []
    for g in groups:
        inner = g[1:-1].strip()
        low = inner.lower()
        if low.startswith("prod."):
            producers.append(inner[len("prod."):].strip())
        elif low.startswith("feat."):
            featured.append(inner[len("feat."):].strip())
        elif low.startswith("with "):
            featured.append(inner[len("with "):].strip())
        elif low.startswith("ref."):
            # The artist who cut the reference vocal — a real credit, but not the performer.
            references.append(inner[len("ref."):].strip())
        elif low.startswith("???"):
            continue
        else:
            # Alt-title group: comma-separated only (do NOT split on "&"/"and" — those occur
            # inside real titles like "Just You and I").
            for piece in inner.split(","):
                piece = piece.strip()
                if piece:
                    alt_titles.append(piece)

    title = head.strip(" -")

    # "Artist - Title" prefix: prefer the song title, keep the full form as an alias.
    if " - " in title:
        full = title
        title = title.split(" - ", 1)[1].strip()
        alt_titles.append(full)

    # Version-stripped variant so "HIGHS AND LOWS" matches "HIGHS AND LOWS [V12]".
    stripped = VERSION_RE.sub("", title).strip()
    if stripped and stripped != title:
        alt_titles.append(stripped)

    # Dedupe, drop blanks and anything equal to the title.
    seen = set()
    cleaned_alts = []
    for a in alt_titles:
        if a and a != title and a.lower() not in seen:
            seen.add(a.lower())
            cleaned_alts.append(a)

    # Credits stay as the tracker wrote them ("Charlie Wilson, Ty Dolla $ign & Lil Durk"): the
    # runtime ArtistCreditNormalizer already knows how to split a credit without truncating names
    # that contain a comma.
    return ParsedName(
        title=title,
        alt_titles=cleaned_alts,
        producers=", ".join(p for p in producers if p) or None,
        featured=" & ".join(f for f in featured if f) or None,
        references=" & ".join(r for r in references if r) or None,
        version=version,
    )


def normalize_header(cell):
    """Reduce a header cell to its bare label.

    Header cells wrap onto several lines ("File\\nDate" is one label) and carry parenthetical
    call-outs that the maintainers edit freely ("Name\\n(Join The Discord!)", "Notes\\n(Join the
    Discord to help fix any issues...)"). Collapse the whitespace, then drop a trailing
    parenthetical, so both spellings land on the same label.
    """
    label = re.sub(r"\s+", " ", cell.strip()).lower()
    return re.sub(r"\s*\([^)]*\)\s*$", "", label).strip()


def header_index(header, candidates):
    """Index of the first column whose normalized label matches, or None."""
    for i, col in enumerate(header):
        if normalize_header(col) in candidates:
            return i
    return None


def parse_og_filenames(notes):
    """Extract the "OG Filename:" value(s) from a Notes cell.

    The value runs to the end of its line; a trailing "&" continues onto the next line, e.g.
    "OG Filenames: Ashton_ALIVE REF &\\nAshton_ALIVE_Reference".
    """
    if not notes:
        return []
    m = OG_LABEL_RE.search(notes)
    if not m:
        return []

    lines = notes[m.end():].split("\n")
    value = lines[0].strip()
    i = 1
    while value.endswith("&") and i < len(lines):
        value = f"{value[:-1].strip()} & {lines[i].strip()}"
        i += 1

    out = []
    for piece in value.split(" & "):
        piece = piece.strip()
        # Only unwrap quotes that enclose the whole value — plenty of real OG filenames quote
        # just part of themselves (e.g. '"When Jesus Walks" Ruff Mix').
        if len(piece) > 1 and piece.startswith('"') and piece.endswith('"'):
            piece = piece[1:-1].strip()
        if piece and re.search(r"[A-Za-z0-9]", piece) and piece not in out:
            out.append(piece)
    return out


def process_sheet(path, sheet_name):
    rows_out = []
    with open(path, newline="", encoding="utf-8") as fh:
        reader = csv.reader(fh)
        raw_rows = list(reader)
    if not raw_rows:
        return rows_out

    # First column is the spreadsheet row-number; the real header is row index 0 (after the
    # "A,B,C,..." column-letter row which is raw_rows[0]). Find the header row that contains "Name".
    header = None
    header_pos = 0
    for idx, row in enumerate(raw_rows[:5]):
        if header_index(row, NAME_HEADERS) is not None:
            header = row
            header_pos = idx
            break
    if header is None:
        raise SheetParseError(
            f"{sheet_name}: no header row with a recognised name column "
            f"({sorted(NAME_HEADERS)}) in the first 5 rows — the tracker probably renamed it"
        )

    era_i = header_index(header, ERA_HEADERS)
    name_i = header_index(header, NAME_HEADERS)
    len_i = header_index(header, LENGTH_HEADERS)
    date_pref_i = header_index(header, DATE_HEADERS_PREFERRED)
    date_fallback_i = header_index(header, DATE_HEADERS_FALLBACK)
    avail_i = header_index(header, AVAILABILITY_HEADERS)
    quality_i = header_index(header, QUALITY_HEADERS)
    notes_i = header_index(header, NOTES_HEADERS)
    artist_i = header_index(header, ARTIST_HEADERS)
    type_i = header_index(header, TYPE_HEADERS)
    link_i = header_index(header, LINK_HEADERS) if sheet_name.lower() in SPOTIFY_ID_SHEETS else None
    ai_generated = sheet_name.lower() in AI_SHEETS

    def cell(row, i):
        return row[i].strip() if (i is not None and i < len(row)) else ""

    for row in raw_rows[header_pos + 1:]:
        era = cell(row, era_i)
        name = cell(row, name_i)
        if not name:
            continue
        if BANNER_RE.search(era):  # era-banner / stats row
            continue
        # Skip rows that are just an era banner in the name slot.
        if BANNER_RE.search(name) and not era:
            continue

        parsed = parse_name(name)
        # Drop empty / placeholder ("???", "??? [V1]") titles: require a letter/digit in the
        # base title once version/bracket markers are removed.
        if not parsed.title or not re.search(r"[A-Za-z0-9]", re.sub(r"\[[^\]]*\]", "", parsed.title)):
            continue

        # The Type column is only meaningful as a track classification on the Released sheet; on
        # Misc/SSC it holds a loose descriptor, and era-banner prose leaks into it.
        row_type = cell(row, type_i) if type_i is not None else ""
        if len(row_type) > 40:
            row_type = ""

        rows_out.append({
            "title": parsed.title,
            "altTitles": parsed.alt_titles,
            "era": era or None,
            "category": sheet_name.lower(),
            "producers": parsed.producers,
            "durationSeconds": parse_length(cell(row, len_i)),
            "year": parse_year(cell(row, date_pref_i), cell(row, date_fallback_i)),
            "availability": cell(row, avail_i) or None,
            "quality": cell(row, quality_i) or None,
            "version": parsed.version,
            "ogFilenames": parse_og_filenames(cell(row, notes_i)),
            "featured": parsed.featured,
            "references": parsed.references,
            "creditedArtists": cell(row, artist_i) or None,
            "type": row_type or None,
            "spotifyId": parse_spotify_id(cell(row, link_i)),
            "aiGenerated": ai_generated or None,
        })
    return rows_out


def parse_spotify_id(links):
    """The Spotify track id from a Link(s) cell — an exact identifier the pipeline can use."""
    if not links:
        return None
    m = SPOTIFY_TRACK_RE.search(links)
    return m.group(1) if m else None


def parse_tracklist_sheet(path):
    """Rows of the Tracklists tab: an album/project and its numbered running order.

    The cell holds free prose followed by numbered lines; only the numbered lines are the
    tracklist. Setlists live on the same tab and are flagged, not dropped — they document what
    was performed, which is not an album's running order.
    """
    with open(path, newline="", encoding="utf-8") as fh:
        raw_rows = list(csv.reader(fh))
    if not raw_rows:
        return []

    header = raw_rows[0]
    era_i = header_index(header, ERA_HEADERS)
    name_i = header_index(header, NAME_HEADERS)
    list_i = header_index(header, TRACKLIST_HEADERS)
    date_i = header_index(header, DATE_HEADERS_FALLBACK) or header_index(header, DATE_HEADERS_PREFERRED)
    quality_i = header_index(header, QUALITY_HEADERS)
    if name_i is None or list_i is None:
        raise SheetParseError(
            f"{TRACKLIST_SHEET}: expected a name and a tracklist column, got {[normalize_header(c) for c in header]}"
        )

    def cell(row, i):
        return row[i].strip() if (i is not None and i < len(row)) else ""

    out = []
    for row in raw_rows[1:]:
        name = cell(row, name_i)
        era = cell(row, era_i)
        if not name or BANNER_RE.search(era):
            continue
        album = EMOJI_RE.sub("", name).strip()
        # "???" rows document that a tracklist exists without naming the project — nothing to match on.
        if not re.search(r"[A-Za-z0-9]", album):
            continue

        tracks = []
        for line in cell(row, list_i).split("\n"):
            m = TRACKLIST_LINE_RE.match(line)
            if not m:
                continue
            title = EMOJI_RE.sub("", m.group(2)).strip()
            if title:
                tracks.append({"number": int(m.group(1)), "title": title})
        if len(tracks) < 2:
            continue

        out.append({
            "album": album,
            "era": era or None,
            "year": parse_year(cell(row, date_i)),
            "legibility": cell(row, quality_i) or None,
            "isSetlist": bool(SETLIST_RE.search(name)),
            "tracks": tracks,
        })
    return out


def parse_art_sheet(path):
    """Rows of the Art tab: cover art and imagery per project, with the image URL(s)."""
    with open(path, newline="", encoding="utf-8") as fh:
        raw_rows = list(csv.reader(fh))
    if not raw_rows:
        return []

    header = raw_rows[0]
    era_i = header_index(header, ERA_HEADERS)
    name_i = header_index(header, NAME_HEADERS)
    designer_i = header_index(header, DESIGNER_HEADERS)
    art_type_i = header_index(header, ART_TYPE_HEADERS)
    project_type_i = header_index(header, PROJECT_TYPE_HEADERS)
    image_i = header_index(header, IMAGE_HEADERS)
    link_i = header_index(header, LINK_HEADERS)
    if name_i is None:
        raise SheetParseError(
            f"{ART_SHEET}: expected a name column, got {[normalize_header(c) for c in header]}"
        )
    # The Art tab's era column header is blank; it's still column A.
    if era_i is None:
        era_i = 0

    def cell(row, i):
        return row[i].strip() if (i is not None and i < len(row)) else ""

    out = []
    for row in raw_rows[1:]:
        name = cell(row, name_i)
        era = cell(row, era_i)
        if not name or BANNER_RE.search(era):
            continue

        urls = []
        for source in (cell(row, image_i), cell(row, link_i)):
            for token in source.split("\n"):
                token = token.strip()
                if token.startswith("http") and token not in urls:
                    urls.append(token)
        if not urls:
            continue

        out.append({
            "name": EMOJI_RE.sub("", name).strip(),
            "era": era or None,
            "designer": cell(row, designer_i) or None,
            "artType": cell(row, art_type_i) or None,
            "projectType": cell(row, project_type_i) or None,
            "imageUrls": urls,
        })
    return out


def discover_sheet_files(raw_dir):
    """Map lowercased sheet name -> CSV path for everything in raw_dir.

    Google names a per-tab download "<workbook> - <Sheet>.csv", so the segment after the last
    " - " identifies the sheet; a bare "<Sheet>.csv" works too. Sorted iteration keeps the pick
    deterministic when a tab was downloaded more than once.
    """
    found = {}
    for entry in sorted(os.listdir(raw_dir)):
        if not entry.lower().endswith(".csv"):
            continue
        sheet = entry[: -len(".csv")].rsplit(" - ", 1)[-1].strip().lower()
        found.setdefault(sheet, os.path.join(raw_dir, entry))
    return found


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    raw_dir = sys.argv[1]
    out_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(__file__), "..", "..", "MusicHoarder.Api", "Data", "yetracker.json"
    )
    out_path = os.path.abspath(out_path)

    available = discover_sheet_files(raw_dir)

    all_rows = []
    errors = []
    for sheet in SONG_SHEETS:
        path = available.get(sheet.lower())
        if path is None:
            print(f"  skip (missing): {sheet}.csv")
            continue
        try:
            rows = process_sheet(path, sheet)
        except SheetParseError as ex:
            print(f"  {sheet}: ERROR — {ex}")
            errors.append(str(ex))
            continue
        print(f"  {sheet}: {len(rows)} songs")
        # A sheet that exists but parses to nothing means the layout moved under us. Writing the
        # catalog anyway would silently drop that whole category (this is how the Unreleased sheet
        # — 8k+ songs, the entire point of the provider — once went missing).
        if not rows:
            errors.append(f"{sheet}: present on disk but yielded 0 songs")
        all_rows.extend(rows)

    if errors:
        print("\nAborting without writing; fix these and re-run:")
        for e in errors:
            print(f"  - {e}")
        sys.exit(1)

    # Dedupe across sheets by (title, era), keeping the first occurrence; merge the list fields and
    # backfill scalars the winner is missing (a song can be listed on several sheets with different
    # columns filled in).
    by_key = {}
    order = []
    for r in all_rows:
        # An AI fake or a third-party remix usually carries the same title and era as the song it
        # derives from, but it is a different recording by a different artist. Keying them apart
        # keeps the real entry from swallowing them — and, just as importantly, keeps the remix's
        # credit ("Machine Gun Kelly") from being backfilled onto the real Kanye song.
        key = (r["title"].lower(), (r["era"] or "").lower(), variant_of(r))
        if key in by_key:
            existing = by_key[key]
            for field in ("altTitles", "ogFilenames"):
                for v in r[field]:
                    if v not in existing[field]:
                        existing[field].append(v)
            # Three fields are deliberately NOT backfilled:
            #   aiGenerated — SONG_SHEETS puts the AI tab last, so a real song that also has an AI
            #     fake keeps its real identity instead of inheriting the fake's flag.
            #   spotifyId / type — both describe the *released* recording. A demo and the release
            #     often share a title and era and get merged here, and lending the release's Spotify
            #     id to the demo would let a wrong match corroborate itself: ProviderIdentity treats
            #     a shared Spotify id as strong agreement.
            for field in ("producers", "durationSeconds", "year", "availability", "quality",
                          "version", "featured", "references", "creditedArtists"):
                if existing[field] is None and r[field] is not None:
                    existing[field] = r[field]
        else:
            by_key[key] = r
            order.append(key)
    deduped = [by_key[k] for k in order]

    write_json(out_path, deduped)
    print(f"\nWrote {len(deduped)} songs -> {out_path}")

    # The songs-adjacent tabs get their own catalogs: a tracklist is an album, not a song, and
    # artwork is neither.
    for sheet, parser, suffix, label in (
        (TRACKLIST_SHEET, parse_tracklist_sheet, "-tracklists", "tracklists"),
        (ART_SHEET, parse_art_sheet, "-art", "artworks"),
    ):
        path = available.get(sheet.lower())
        if path is None:
            print(f"  skip (missing): {sheet}.csv")
            continue
        try:
            rows = parser(path)
        except SheetParseError as ex:
            print(f"  {sheet}: ERROR — {ex}")
            sys.exit(1)
        base, ext = os.path.splitext(out_path)
        sheet_out = f"{base}{suffix}{ext}"
        write_json(sheet_out, rows)
        print(f"Wrote {len(rows)} {label} -> {sheet_out}")


def variant_of(row):
    """Dedupe dimension separating derivative recordings from the song they derive from."""
    if row.get("aiGenerated"):
        return "ai"
    if row.get("category") == "remixes":
        return "remix"
    return ""


def write_json(path, payload):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=0)


if __name__ == "__main__":
    main()
