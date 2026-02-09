import os
import re
import json
import sqlite3
import requests
from dataclasses import dataclass
from typing import Iterable, List, Optional, Tuple
import numpy as np
from tqdm import tqdm



# =========================
# Config
# =========================
OUT_DIR = os.getcwd()
DB_PATH = os.path.join(OUT_DIR, "stardew_wiki.sqlite")


ACCOUNT_ID = 'TO_BE_REPLACED'
AUTH_API = 'TO_BE_REPLACED'

# Chunking controls
MAX_CHARS_PER_CHUNK = 1200
MIN_CHARS_PER_CHUNK = 300  # try to avoid tiny fragments
JOINER = "\n\n"

# If your JSON files are mixed with manifest/error files:
SKIP_FILENAMES = {"manifest.json"}
SKIP_SUFFIXES = (".error.json",)

# =========================
# Helpers
# =========================
def iter_page_json_files(folder: str) -> Iterable[str]:
    for name in os.listdir(folder):
        if not name.endswith(".json"):
            continue
        if name in SKIP_FILENAMES:
            continue
        if any(name.endswith(suf) for suf in SKIP_SUFFIXES):
            continue
        yield os.path.join(folder, name)

def normalize_ws(s: str) -> str:
    s = re.sub(r"[ \t]+", " ", s)
    s = re.sub(r"\n{3,}", "\n\n", s)
    return s.strip()

def safe_int(x, default=None):
    try:
        return int(x)
    except Exception:
        return default

@dataclass
class Block:
    section: str
    block_type: str
    text: str

def flatten_sections_to_blocks(page: dict) -> List[Block]:
    """
    Your JSON shape: {"title","page_id","revid","sections":[{"heading","blocks":[{"type","text"}]}]}
    We convert to a flat list of blocks with (section, block_type, text).
    """
    blocks: List[Block] = []
    sections = page.get("sections") or []
    for sec in sections:
        heading = (sec.get("heading") or "Intro").strip()
        for b in (sec.get("blocks") or []):
            btype = (b.get("type") or "p").strip()
            text = normalize_ws(b.get("text") or "")
            if text:
                blocks.append(Block(section=heading, block_type=btype, text=text))
    return blocks

def chunk_blocks(blocks: List[Block]) -> List[Tuple[int, str, str, str]]:
    """
    Build chunks in order. chunk_index restarts for every page.
    Return: list of (chunk_index, section, block_type, chunk_text)

    Strategy:
      - Preserve ordering
      - Prefer to keep within MAX_CHARS_PER_CHUNK
      - Merge small trailing pieces if too tiny
    """
    chunks: List[Tuple[int, str, str, str]] = []

    buf_texts: List[str] = []
    buf_section: Optional[str] = None
    buf_type: Optional[str] = None

    def flush():
        nonlocal chunks, buf_texts, buf_section, buf_type
        if not buf_texts:
            return
        text = normalize_ws(JOINER.join(buf_texts))
        if text:
            chunk_index = len(chunks)
            chunks.append((chunk_index, buf_section or "Intro", buf_type or "mixed", text))
        buf_texts = []
        buf_section = None
        buf_type = None

    for blk in blocks:
        # If buffer empty, start a new chunk with this block's metadata
        if not buf_texts:
            buf_section = blk.section
            buf_type = blk.block_type

        candidate = normalize_ws(JOINER.join(buf_texts + [blk.text]))
        if len(candidate) <= MAX_CHARS_PER_CHUNK:
            buf_texts.append(blk.text)
        else:
            # flush existing
            flush()
            # if single block is huge, split it
            if len(blk.text) > MAX_CHARS_PER_CHUNK:
                text = blk.text
                start = 0
                while start < len(text):
                    piece = normalize_ws(text[start:start + MAX_CHARS_PER_CHUNK])
                    if piece:
                        chunk_index = len(chunks)
                        chunks.append((chunk_index, blk.section, blk.block_type, piece))
                    start += MAX_CHARS_PER_CHUNK
            else:
                # start new buffer with this block
                buf_section = blk.section
                buf_type = blk.block_type
                buf_texts = [blk.text]

    flush()

    # Post-pass: merge too-small chunks with previous if possible
    merged: List[Tuple[int, str, str, str]] = []
    for ci, sec, bt, txt in chunks:
        if merged and len(txt) < MIN_CHARS_PER_CHUNK:
            p_ci, p_sec, p_bt, p_txt = merged[-1]
            merged[-1] = (p_ci, p_sec, p_bt, normalize_ws(p_txt + JOINER + txt))
        else:
            merged.append((ci, sec, bt, txt))

    # Re-index after merges
    reindexed = [(i, sec, bt, txt) for i, (_, sec, bt, txt) in enumerate(merged)]
    return reindexed

# =========================
# Embedding stub (plug in later)
# =========================

def embed_texts(texts: List[str], model_name: str, batch_size: int = 32) -> Tuple[int, np.ndarray]:
    """
    Return (dim, vectors). This is a stub.
    Replace with:
      - local embedding model
      - or API call
      - or your own pipeline
    """
    all_vecs = []
    for i in range(0,len(texts),batch_size):

        batch = texts[i:i+batch_size]

        response = requests.post(
        f"https://api.cloudflare.com/client/v4/accounts/{ACCOUNT_ID}/ai/run/@cf/baai/{model_name}",
        headers={"Authorization": f"Bearer {AUTH_API}"},
        json={"text": batch}
        )
        data = response.json().get('result').get('data')
        vectors = np.asarray(data,dtype = np.float32)

        if vectors.ndim == 1:
            vectors = vectors.reshape(1,-1)
        all_vecs.append(vectors)

    result = np.vstack(all_vecs)

    return result.shape[1], result


def vec_to_blob_f32(vec: np.ndarray) -> bytes:
    if vec.dtype != np.float32:
        vec = vec.astype(np.float32, copy=False)
    return np.ascontiguousarray(vec).tobytes()


def blob_f32_dim(blob: bytes) -> int:
    return len(blob) // 4  # 4 bytes per float32

def l2_norm_f32(vec: np.ndarray) -> float:
    return float(np.linalg.norm(vec))

# =========================
# SQLite schema
# =========================
SCHEMA_SQL = """
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS page (
  page_id INTEGER PRIMARY KEY,
  title   TEXT NOT NULL,
  revid   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS chunk (
  chunk_id     INTEGER PRIMARY KEY AUTOINCREMENT,
  page_id      INTEGER NOT NULL,
  chunk_index  INTEGER NOT NULL,
  section      TEXT NOT NULL,
  block_type   TEXT NOT NULL,
  text         TEXT NOT NULL,
  FOREIGN KEY(page_id) REFERENCES page(page_id) ON DELETE CASCADE,
  UNIQUE(page_id, chunk_index)
);

CREATE TABLE IF NOT EXISTS embedding_model (
  model_id INTEGER PRIMARY KEY AUTOINCREMENT,
  name     TEXT NOT NULL UNIQUE,
  dim      INTEGER NOT NULL,
  distance_metric TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS embedding (
  chunk_id INTEGER NOT NULL,
  model_id INTEGER NOT NULL,
  vec      BLOB NOT NULL,
  norm     REAL NOT NULL,
  PRIMARY KEY(chunk_id, model_id),
  FOREIGN KEY(chunk_id) REFERENCES chunk(chunk_id) ON DELETE CASCADE,
  FOREIGN KEY(model_id) REFERENCES embedding_model(model_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_chunk_page ON chunk(page_id);
CREATE INDEX IF NOT EXISTS idx_embed_model ON embedding(model_id);
"""

def connect_db(db_path: str) -> sqlite3.Connection:
    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA foreign_keys = ON;")
    conn.execute("PRAGMA journal_mode=WAL;")
    conn.execute("PRAGMA synchronous=NORMAL;")
    return conn

def init_db(conn: sqlite3.Connection):
    conn.executescript(SCHEMA_SQL)
    conn.commit()

# =========================
# DB upsert logic
# =========================
def get_existing_page(conn: sqlite3.Connection, page_id: int) -> Optional[Tuple[int, str, int, str]]:
    row = conn.execute("SELECT page_id, title, revid FROM page WHERE page_id = ?", (page_id,)).fetchone()
    return row

def upsert_page(conn: sqlite3.Connection, page_id: int, title: str, revid: int):
    conn.execute("""
        INSERT INTO page(page_id, title, revid)
        VALUES(?, ?, ?)
        ON CONFLICT(page_id) DO UPDATE SET
          title=excluded.title,
          revid=excluded.revid
    """, (page_id, title, revid))

def delete_page_children(conn: sqlite3.Connection, page_id: int):
    # With ON DELETE CASCADE, deleting chunks deletes embeddings.
    conn.execute("DELETE FROM chunk WHERE page_id = ?", (page_id,))

def insert_chunks(conn: sqlite3.Connection, page_id: int, chunks: List[Tuple[int, str, str, str]]) -> List[int]:
    """
    Insert chunks; return list of chunk_ids in chunk_index order.
    """
    chunk_ids: List[int] = []
    for chunk_index, section, block_type, text in chunks:
        cur = conn.execute("""
            INSERT INTO chunk(page_id, chunk_index, section, block_type, text)
            VALUES(?, ?, ?, ?, ?)
        """, (page_id, chunk_index, section, block_type, text))
        chunk_ids.append(cur.lastrowid)
    return chunk_ids


def ensure_model_row(conn: sqlite3.Connection, model_name: str, dim: int, distance_metric: str = "cosine") -> int:
    conn.execute("""
        INSERT INTO embedding_model(name, dim, distance_metric)
        VALUES(?, ?, ?)
        ON CONFLICT(name) DO UPDATE SET
          dim=excluded.dim,
          distance_metric=excluded.distance_metric
    """, (model_name, dim, distance_metric))
    model_id = conn.execute("SELECT model_id FROM embedding_model WHERE name = ?", (model_name,)).fetchone()[0]
    return model_id

def insert_embeddings(conn: sqlite3.Connection, model_id: int, chunk_ids: List[int], vectors: np.ndarray):
    rows = []
    for chunk_id, vec in zip(chunk_ids, vectors):
        rows.append((chunk_id, model_id, vec_to_blob_f32(vec), l2_norm_f32(vec)))

    conn.executemany("""
        INSERT INTO embedding(chunk_id, model_id, vec, norm)
        VALUES(?, ?, ?, ?)
        ON CONFLICT(chunk_id, model_id) DO UPDATE SET
          vec=excluded.vec,
          norm=excluded.norm
    """, rows)

# =========================
# Page JSON parsing
# =========================
def load_page_json(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)

def extract_page_identity(page: dict) -> Tuple[int, str, int, str]:
    """
    Expect fields:
      page_id (int), title (str), revid (int)
    If your current JSON doesn't include page_id/revid yet, you MUST add them at scrape time.
    """
    page_id = page.get("pageid")
    revid = page.get("revid")
    title = page.get("title") or "Unknown"

    page_id = safe_int(page_id)
    revid = safe_int(revid)

    if page_id is None or revid is None:
        raise ValueError(
            f"Missing page_id/revid in JSON. Got page_id={page.get('page_id')} revid={page.get('revid')}. "
            "Update your scraper to store both."
        )

    return page_id, str(title), int(revid)

# =========================
# Main build
# =========================
def build_database(do_embed: bool = False, model_name: str = "example-embed-model"):
    conn = connect_db(DB_PATH)
    init_db(conn)

    files = list(iter_page_json_files(OUT_DIR))
    if not files:
        print(f"No page JSON found in: {OUT_DIR}")
        return

    updated_pages = 0
    skipped_pages = 0

    for path in tqdm(files, desc="Building DB"):
        try:
            page = load_page_json(path)
            page_id, title, revid = extract_page_identity(page)

            existing = get_existing_page(conn, page_id)

            if existing and existing[2] == revid:
                # same revid: unchanged
                skipped_pages += 1
                continue

            # Changed or new: rebuild
            conn.execute("BEGIN IMMEDIATE;")

            upsert_page(conn, page_id, title, revid)
            delete_page_children(conn, page_id)

            blocks = flatten_sections_to_blocks(page)
            chunks = chunk_blocks(blocks)
            chunk_ids = insert_chunks(conn, page_id, chunks)

            if do_embed:
                texts = [t for (_, _, _, t) in chunks]
                dim, vectors = embed_texts(texts, model_name=model_name)
                model_id = ensure_model_row(conn, model_name=model_name, dim=dim, distance_metric="cosine")
                insert_embeddings(conn, model_id, chunk_ids, vectors)

            conn.commit()
            updated_pages += 1

        except Exception as e:
            conn.execute("ROLLBACK;")
            print(f"\n[ERROR] {os.path.basename(path)}: {e}")

    conn.close()
    print(f"\nDone. Updated/new pages: {updated_pages}, skipped (unchanged revid): {skipped_pages}")
    print(f"DB: {DB_PATH}")

if __name__ == "__main__":
    # Set do_embed=False for now; you can enable later after you implement embed_texts().
    build_database(do_embed=True, model_name = 'bge-m3')
