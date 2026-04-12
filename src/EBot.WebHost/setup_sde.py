#!/usr/bin/env python3
"""
EBot SDE Setup – download Fuzzwork CSV dumps and import into SQLite.

Run once before starting EBot (or after an EVE patch):
    python setup_sde.py

Creates:  data/eve_sde.db

Tables imported (from https://www.fuzzwork.co.uk/dump/latest/):
    mapSolarSystems  – system ID, name, region, security status
    mapRegions       – region ID, name
    staStations      – NPC station ID, name, solar system, type
"""
import bz2
import csv
import io
import sqlite3
import sys
import time
import urllib.request
from pathlib import Path

DB_PATH  = Path(__file__).parent / "data" / "eve_sde.db"
BASE_URL = "https://www.fuzzwork.co.uk/dump/latest/"

TABLES = {
    "mapSolarSystems": {
        "filename": "mapSolarSystems.csv.bz2",
        "columns":  ["solarSystemID", "solarSystemName", "regionID", "security"],
        "create": """
            CREATE TABLE IF NOT EXISTS mapSolarSystems (
                solarSystemID    INTEGER PRIMARY KEY,
                solarSystemName  TEXT,
                regionID         INTEGER,
                security         REAL
            )
        """,
    },
    "mapRegions": {
        "filename": "mapRegions.csv.bz2",
        "columns":  ["regionID", "regionName"],
        "create": """
            CREATE TABLE IF NOT EXISTS mapRegions (
                regionID    INTEGER PRIMARY KEY,
                regionName  TEXT
            )
        """,
    },
    "staStations": {
        "filename": "staStations.csv.bz2",
        "columns":  ["stationID", "stationName", "solarSystemID", "regionID", "stationTypeID"],
        "create": """
            CREATE TABLE IF NOT EXISTS staStations (
                stationID       INTEGER PRIMARY KEY,
                stationName     TEXT,
                solarSystemID   INTEGER,
                regionID        INTEGER,
                stationTypeID   INTEGER
            )
        """,
    },
}

INDEX_SQL = [
    "CREATE INDEX IF NOT EXISTS idx_sys_name   ON mapSolarSystems (solarSystemName)",
    "CREATE INDEX IF NOT EXISTS idx_sys_region  ON mapSolarSystems (regionID)",
    "CREATE INDEX IF NOT EXISTS idx_sta_name    ON staStations     (stationName)",
    "CREATE INDEX IF NOT EXISTS idx_sta_sysid   ON staStations     (solarSystemID)",
]


def download_csv(table_name: str, info: dict) -> list[dict]:
    url = BASE_URL + info["filename"]
    print(f"  Downloading {url} ...", end=" ", flush=True)
    t0 = time.time()

    try:
        with urllib.request.urlopen(url, timeout=120) as resp:
            compressed = resp.read()
    except Exception as e:
        print(f"FAILED: {e}")
        return []

    raw    = bz2.decompress(compressed).decode("utf-8", errors="replace")
    reader = csv.reader(io.StringIO(raw))
    header = next(reader, None)
    if header is None:
        print("empty file")
        return []

    col_map = {col: idx for idx, col in enumerate(header)}
    rows = []
    for row in reader:
        record = {}
        for col in info["columns"]:
            if col in col_map:
                record[col] = row[col_map[col]] if col_map[col] < len(row) else None
            else:
                record[col] = None
        rows.append(record)

    print(f"done ({len(rows):,} rows, {time.time() - t0:.1f}s)")
    return rows


def import_table(conn: sqlite3.Connection, table_name: str, info: dict, rows: list[dict]):
    if not rows:
        return
    conn.execute(f"DROP TABLE IF EXISTS {table_name}")
    conn.execute(info["create"])
    cols = info["columns"]
    ph   = ",".join("?" * len(cols))
    sql  = f"INSERT OR IGNORE INTO {table_name} ({','.join(cols)}) VALUES ({ph})"

    def coerce(val):
        if val in (None, "", "None"):
            return None
        return val

    conn.executemany(sql, [[coerce(r[c]) for c in cols] for r in rows])
    print(f"  Imported {len(rows):,} rows into {table_name}")


def main():
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)

    print("=" * 55)
    print("EBot SDE Setup – Fuzzwork CSV import")
    print("=" * 55)

    conn = sqlite3.connect(str(DB_PATH))
    conn.execute("PRAGMA journal_mode=WAL")

    for table_name, info in TABLES.items():
        print(f"\n[{table_name}]")
        rows = download_csv(table_name, info)
        if rows:
            conn.execute("BEGIN")
            import_table(conn, table_name, info, rows)
            conn.commit()
        else:
            print(f"  WARNING: No data for {table_name} – skipping")

    print("\nBuilding indexes...")
    for sql in INDEX_SQL:
        conn.execute(sql)
    conn.commit()
    conn.close()

    print(f"\nDone!  Database written to: {DB_PATH}")
    print("You can now start EBot.\n")


if __name__ == "__main__":
    main()
