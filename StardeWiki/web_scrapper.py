import os
import re
import json
import time
import requests
from bs4 import BeautifulSoup
from tqdm import tqdm
from urllib.parse import unquote

# =========================
# Config
# =========================
OUT_DIR = "/Users/chili/Desktop/StardeWiki2"
MANIFEST_PATH = os.path.join(OUT_DIR, "manifest.json")

BASE = "https://stardewvalleywiki.com"
API = f"{BASE}/mediawiki/api.php"

SLEEP_SECONDS = 0.5  # be polite
HEADERS = {
    "User-Agent": "StardewGPT-WikiScraper/1.0 (offline build; polite)"
}

# Namespace 0 = main content pages (no Category:, File:, Special:, etc.)
NAMESPACE = 0

# For testing, set to an int (e.g., 20). Set to None for full run.
MAX_PAGES = None


# =========================
# File helpers
# =========================
def ensure_dirs():
    os.makedirs(OUT_DIR, exist_ok=True)

def load_manifest() -> dict:
    """
    manifest schema (by pageid):
      {
        "16089": {
          "pageid": 16089,
          "title": "Joja HQ Painting",
          "revid": 187940,
          "file": "Joja HQ Painting.json"
        },
        ...
      }
    """
    if not os.path.exists(MANIFEST_PATH):
        return {}
    with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
        try:
            data = json.load(f)
            return data if isinstance(data, dict) else {}
        except Exception:
            return {}

def save_manifest(manifest: dict):
    with open(MANIFEST_PATH, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

def safe_filename(title: str, max_len: int = 160) -> str:
    title = title.strip()
    title = re.sub(r"[\\/:*?\"<>|]+", "_", title)  # Windows-illegal chars
    title = re.sub(r"\s+", " ", title)
    if len(title) > max_len:
        title = title[:max_len].rstrip()
    return title

def resolve_filename_collision(title: str, pageid: int) -> str:
    """
    Prefer clean "Title.json".
    If already exists for a different page, use "Title__<pageid>.json".
    """
    base = safe_filename(title)
    fname = f"{base}.json"
    path = os.path.join(OUT_DIR, fname)
    if not os.path.exists(path):
        return fname
    # If file exists, avoid collision by appending pageid
    return f"{base}__{pageid}.json"

def write_page_json(filename: str, data: dict):
    path = os.path.join(OUT_DIR, filename)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


# =========================
# MediaWiki API helpers
# =========================
def api_get(session: requests.Session, params: dict) -> dict:
    r = session.get(API, params=params, headers=HEADERS, timeout=30)
    r.raise_for_status()
    time.sleep(SLEEP_SECONDS)
    return r.json()

def iter_allpages(session: requests.Session, namespace: int = 0):
    """
    Yields dicts: {pageid, title, revid}
    Uses list=allpages + prop=info to get pageid, then also queries latest revid.
    More efficient: use generator=allpages with prop=revisions.
    """
    cont = None
    yielded = 0

    while True:
        params = {
            "action": "query",
            "format": "json",
            "generator": "allpages",
            "gapnamespace": namespace,
            "gaplimit": "500",       
            "prop": "revisions",
            "rvprop": "ids",
            "rvslots": "main",
        }
        if cont:
            params.update(cont)

        data = api_get(session, params)

        pages = data.get("query", {}).get("pages", {})
        # pages is a dict keyed by pageid as string
        for _, page in pages.items():
            pageid = page.get("pageid")
            title = page.get("title")
            revs = page.get("revisions") or []
            revid = revs[0].get("revid") if revs else None
            if pageid and title and revid:
                yield {"pageid": pageid, "title": title, "revid": revid}
                yielded += 1
                if MAX_PAGES is not None and yielded >= MAX_PAGES:
                    return

        if "continue" not in data:
            break
        cont = data["continue"]


def parse_page_html(session: requests.Session, title: str) -> dict:
    """
    Returns:
      {
        "title": "...",
        "pageid": ...,
        "revid": ...,
        "redirects": [...],
        "sections": [...]
      }

    Uses action=parse + redirects=1 to resolve redirects.
    """
    params = {
        "action": "parse",
        "format": "json",
        "page": title,
        "prop": "text|sections",
        "redirects": "1",
        "disabletoc": "1",
    }
    data = api_get(session, params)

    if "error" in data:
        raise RuntimeError(f"API parse error for {title}: {data['error']}")

    parse = data["parse"]
    html = parse["text"]["*"]
    pageid = parse.get("pageid")
    revid = parse.get("revid")  # important!
    resolved_title = parse.get("title", title)
    redirects = parse.get("redirects", [])
    sections_meta = parse.get("sections", [])

    sections = extract_page_structured(html)

    return {
        "title": resolved_title,
        "pageid": pageid,
        "revid": revid,
        "redirects": redirects,
        "sections_meta": sections_meta,
        "sections": sections,
    }


# =========================
# HTML cleanup + extraction
# =========================
def clean_text(s: str) -> str:
    s = re.sub(r"\[\d+\]", "", s)          # [1] citations
    s = re.sub(r"[ \t]{2,}", " ", s)
    s = re.sub(r"\n{3,}", "\n\n", s)
    return s.strip()

def table_to_text(table_tag) -> str:
    # remove nested tables to avoid crazy duplication
    for nested in table_tag.select("table"):
        nested.decompose()

    rows = table_tag.find_all("tr")
    if not rows:
        return ""

    headers = []
    for r in rows:
        ths = r.find_all("th")
        if ths:
            headers = [clean_text(th.get_text(" ", strip=True)) for th in ths]
            headers = [h for h in headers if h]
            break

    out_lines = []
    for r in rows:
        cells = r.find_all(["th", "td"])
        vals = [clean_text(c.get_text(" ", strip=True)) for c in cells]
        vals = [v for v in vals if v]
        if not vals:
            continue
        if headers and vals == headers:
            continue

        if headers:
            pairs = []
            for i, v in enumerate(vals):
                h = headers[i] if i < len(headers) else f"Col{i+1}"
                pairs.append(f"{h}: {v}")
            out_lines.append(" | ".join(pairs))
        else:
            out_lines.append(" | ".join(vals))

    return "\n".join(out_lines).strip()

def extract_page_structured(html: str):
    soup = BeautifulSoup(html, "lxml")

    # Remove common noise blocks
    noise_selectors = [
        "#toc",
        ".mw-editsection",
        "script",
        "style",
        "sup.reference",
        ".reference",
        ".reflist",
        "table#navbox",
        "table.navbox",
        ".navbox",
        ".mw-jump-link",
        ".printfooter",
        ".catlinks",
    ]
    for sel in noise_selectors:
        for node in soup.select(sel):
            node.decompose()

    # Work inside the parser output div when available
    content = soup.select_one(".mw-parser-output") or soup

    sections = []
    current = {"heading": "Intro", "blocks": []}

    def push_block(block_type: str, text: str):
        text = clean_text(text)
        if text:
            current["blocks"].append({"type": block_type, "text": text})

    # iterate through top-level elements
    for el in content.find_all(["h2", "h3", "p", "ul", "ol", "table"], recursive=False):
        if el.name in ("h2", "h3"):
            heading = el.get_text(" ", strip=True).replace("[edit]", "").strip()
            if heading:
                if current["blocks"]:
                    sections.append(current)
                current = {"heading": heading, "blocks": []}

        elif el.name == "p":
            push_block("p", el.get_text(" ", strip=True))

        elif el.name in ("ul", "ol"):
            items = [clean_text(li.get_text(" ", strip=True)) for li in el.find_all("li", recursive=False)]
            items = [x for x in items if x]
            if items:
                push_block("list", "\n".join(f"- {x}" for x in items))

        elif el.name == "table":
            t = table_to_text(el)
            if t:
                push_block("table", t)

    if current["blocks"]:
        sections.append(current)

    return sections


# =========================
# Main
# =========================
def main():
    ensure_dirs()
    session = requests.Session()

    manifest = load_manifest()
    print(f"Loaded manifest entries: {len(manifest)}")

    updated = 0
    skipped = 0
    errored = 0

    # We don’t need timestamps. We only care about revid changes.
    # Key: pageid (as string to be JSON-friendly)
    all_pages = list(iter_allpages(session, namespace=NAMESPACE))
    print(f"Discovered pages in ns={NAMESPACE}: {len(all_pages)}")

    for info in tqdm(all_pages, desc="Updating", unit="page"):
        pageid = info["pageid"]
        title = info["title"]
        latest_revid = info["revid"]

        key = str(pageid)
        prev = manifest.get(key)

        if prev and prev.get("revid") == latest_revid:
            skipped += 1
            continue

        try:
            parsed = parse_page_html(session, title)

            # resolved identity
            resolved_pageid = parsed["pageid"] or pageid
            resolved_revid = parsed["revid"] or latest_revid
            resolved_title = parsed["title"] or title

            out_file = resolve_filename_collision(resolved_title, resolved_pageid)

            write_page_json(out_file, {
                "title": resolved_title,
                "pageid": resolved_pageid,
                "revid": resolved_revid,
                "redirects": parsed.get("redirects", []),
                "sections": parsed["sections"],
            })

            manifest[str(resolved_pageid)] = {
                "pageid": resolved_pageid,
                "title": resolved_title,
                "revid": resolved_revid,
                "file": out_file,
            }

            updated += 1

        except Exception as e:
            errored += 1
            err_name = safe_filename(title) + f"__{pageid}.error.json"
            with open(os.path.join(OUT_DIR, err_name), "w", encoding="utf-8") as f:
                json.dump({"pageid": pageid, "title": title, "error": str(e)}, f, ensure_ascii=False, indent=2)

    save_manifest(manifest)

    print("Done.")
    print(f"Updated/new: {updated}")
    print(f"Skipped (same revid): {skipped}")
    print(f"Errors: {errored}")
    print(f"Manifest written: {MANIFEST_PATH}")


if __name__ == "__main__":
    main()
