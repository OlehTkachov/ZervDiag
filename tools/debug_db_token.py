import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from database.db import get_connection


def make_snippet(text, token, radius=140):
    text = text or ""
    lowered = text.lower()
    needle = token.lower()
    pos = lowered.find(needle)
    if pos < 0:
        return ""

    start = max(0, pos - radius)
    end = min(len(text), pos + len(token) + radius)
    return (
        text[start:end]
        .replace("\r", " ")
        .replace("\n", " ")
    )


def main():
    if len(sys.argv) < 2:
        print('Usage: python tools/debug_db_token.py "55724"')
        raise SystemExit(2)

    token = sys.argv[1]
    like = f"%{token}%"

    conn = get_connection()
    try:
        rows = conn.execute(
            """
            SELECT
                id,
                filename,
                extension,
                filepath,
                COALESCE(content, ''),
                status
            FROM files
            WHERE filename LIKE ?
               OR filepath LIKE ?
               OR content LIKE ?
            ORDER BY id
            LIMIT 100
            """,
            (like, like, like),
        ).fetchall()

        total = conn.execute(
            """
            SELECT COUNT(*)
            FROM files
            WHERE filename LIKE ?
               OR filepath LIKE ?
               OR content LIKE ?
            """,
            (like, like, like),
        ).fetchone()[0]
    finally:
        conn.close()

    print("TOKEN:", token)
    print("TOTAL MATCHING DB ROWS:", total)
    print()

    for file_id, filename, extension, filepath, content, status in rows:
        name_hit = token.lower() in (filename or "").lower()
        path_hit = token.lower() in (filepath or "").lower()
        content_hit = token.lower() in (content or "").lower()

        print("=" * 100)
        print("ID:", file_id)
        print("FILE:", filename)
        print("TYPE:", extension)
        print("STATUS:", status)
        print("PATH:", filepath)
        print(
            "SOURCES:",
            f"filename={name_hit}",
            f"path={path_hit}",
            f"content={content_hit}",
        )

        if content_hit:
            print("CONTENT MATCH:", make_snippet(content, token))

        print()


if __name__ == "__main__":
    main()
