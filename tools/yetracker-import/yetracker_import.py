#!/usr/bin/env python3
"""Normalize the scraped yetracker (Kanye West community tracker) Google-Sheets CSV export
into a single lean JSON catalog for MusicHoarder's enrichment pipeline.

This is an OFFLINE, one-off tool. The raw sheets and their download URLs are NOT committed to
the repo; only the normalized output (MusicHoarder.Api/Data/yetracker.json) is. Re-run this when
the tracker is re-scraped.

Usage:
    python3 tools/yetracker-import/yetracker_import.py <raw_csv_dir> [out_json]

    <raw_csv_dir>  directory containing the raw sheet CSVs (Unreleased.csv, Released.csv, ...)
    [out_json]     output path (default: MusicHoarder.Api/Data/yetracker.json)

Output: a JSON array of objects:
    { "title", "altTitles": [...], "era", "category", "producers", "durationSeconds", "year" }

Download links, raw notes, and per-sheet column noise are dropped. The runtime
YeTrackerCatalogService deserializes this directly into TrackerSong records.
"""

import csv
import json
import os
import re
import sys

# Sheets that contain individual, matchable songs. Everything else (Key, Template, Art,
# Tracklists, Samples, Fakes, Grails-Wanted, Fundraisers) is metadata or non-matchable.
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
]

# Header label -> logical field. Mapped case-insensitively, trimmed. First match wins.
ERA_HEADERS = {"era", "main era"}
NAME_HEADERS = {"name", "main content"}
LENGTH_HEADERS = {"track length", "tracklength", "length", "full length", "copy length", "image / length"}
DATE_HEADERS_PREFERRED = {"leak date", "leakdate", "release date"}      # when the song became available
DATE_HEADERS_FALLBACK = {"file date", "filedate", "date made"}          # when it was recorded

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
# A parenthetical that is a credit (vs. an alt-title or a mix descriptor).
CREDIT_PAREN_RE = re.compile(r"\((?:feat\.|ref\.|prod\.|with\s|\?\?\?)", re.IGNORECASE)


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
    """Parse the packed Name column into (title, alt_titles, producers).

    raw example:
      "Playboi Carti - Headshot [V2](feat. Kanye West) (prod. Richie Souf)(Heads Off, Headshots)"
    """
    name = EMOJI_RE.sub("", raw).strip()
    head, groups = split_paren_groups(name)

    producers = []
    alt_titles = []
    for g in groups:
        inner = g[1:-1].strip()
        low = inner.lower()
        if low.startswith("prod."):
            producers.append(inner[len("prod."):].strip())
        elif low.startswith(("feat.", "ref.", "with", "???")):
            continue  # guest/reference credits — not needed for title matching
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

    return title, cleaned_alts, ", ".join(p for p in producers if p) or None


def header_index(header, candidates):
    for i, col in enumerate(header):
        if col.strip().lower() in candidates:
            return i
    return None


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
        return rows_out

    era_i = header_index(header, ERA_HEADERS)
    name_i = header_index(header, NAME_HEADERS)
    len_i = header_index(header, LENGTH_HEADERS)
    date_pref_i = header_index(header, DATE_HEADERS_PREFERRED)
    date_fallback_i = header_index(header, DATE_HEADERS_FALLBACK)
    if name_i is None:
        return rows_out

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

        title, alts, producers = parse_name(name)
        # Drop empty / placeholder ("???", "??? [V1]") titles: require a letter/digit in the
        # base title once version/bracket markers are removed.
        if not title or not re.search(r"[A-Za-z0-9]", re.sub(r"\[[^\]]*\]", "", title)):
            continue

        rows_out.append({
            "title": title,
            "altTitles": alts,
            "era": era or None,
            "category": sheet_name.lower(),
            "producers": producers,
            "durationSeconds": parse_length(cell(row, len_i)),
            "year": parse_year(cell(row, date_pref_i), cell(row, date_fallback_i)),
        })
    return rows_out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    raw_dir = sys.argv[1]
    out_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(__file__), "..", "..", "MusicHoarder.Api", "Data", "yetracker.json"
    )
    out_path = os.path.abspath(out_path)

    all_rows = []
    for sheet in SONG_SHEETS:
        path = os.path.join(raw_dir, f"{sheet}.csv")
        if not os.path.exists(path):
            print(f"  skip (missing): {sheet}.csv")
            continue
        rows = process_sheet(path, sheet)
        print(f"  {sheet}: {len(rows)} songs")
        all_rows.extend(rows)

    # Dedupe across sheets by (title, era), keeping the first occurrence; merge alt-titles.
    by_key = {}
    order = []
    for r in all_rows:
        key = (r["title"].lower(), (r["era"] or "").lower())
        if key in by_key:
            existing = by_key[key]
            for a in r["altTitles"]:
                if a not in existing["altTitles"]:
                    existing["altTitles"].append(a)
        else:
            by_key[key] = r
            order.append(key)
    deduped = [by_key[k] for k in order]

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(deduped, fh, ensure_ascii=False, indent=0)

    print(f"\nWrote {len(deduped)} songs -> {out_path}")


if __name__ == "__main__":
    main()
