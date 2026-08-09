import sqlite3
from pathlib import Path


BASE_DIR = Path(__file__).resolve().parent.parent
DATA_DIR = BASE_DIR / "data"
DB_PATH = DATA_DIR / "zervdiag.db"


def get_connection():
    DATA_DIR.mkdir(exist_ok=True)
    return sqlite3.connect(DB_PATH)


def create_database():
    conn = get_connection()
    cursor = conn.cursor()

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            filename TEXT NOT NULL,
            filepath TEXT NOT NULL UNIQUE,
            extension TEXT,
            size INTEGER,
            modified REAL,
            content TEXT
        )
    """)

    cursor.execute("PRAGMA table_info(files)")
    columns = [row[1] for row in cursor.fetchall()]

    if "content" not in columns:
        cursor.execute(
            "ALTER TABLE files ADD COLUMN content TEXT"
        )

    conn.commit()
    conn.close()