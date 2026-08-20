import re
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from database.db import get_connection
from search.search import _requirement_matches_field, _requirement_pattern


DOCUMENT_EXTENSIONS = {
    ".pdf", ".doc", ".docx", ".xls", ".xlsx",
    ".odt", ".ods", ".txt", ".csv", ".json",
}


def snippet(text, pattern, radius=120):
    match = re.search(pattern, text or "", flags=re.IGNORECASE)
    if not match:
        return ""

    start = max(0, match.start() - radius)
    end = min(len(text), match.end() + radius)
    return (
        text[start:end]
        .replace("\r", " ")
        .replace("\n", " ")
    )


def main():
    latin_pattern = _requirement_pattern("tech", "e10")
    cyr_pattern = _requirement_pattern("tech", "е10")

    conn = get_connection()
    try:
        rows = conn.execute(
            """
            SELECT
                id,
                filename,
                extension,
                filepath,
                COALESCE(content, '')
            FROM files
            WHERE length(COALESCE(content, '')) > 0
            ORDER BY id
            """
        ).fetchall()
    finally:
        conn.close()

    context_rows = []
    for row in rows:
        file_id, filename, extension, filepath, content = row
        extension = (extension or "").lower()

        if extension not in DOCUMENT_EXTENSIONS:
            continue

        if not (
            _requirement_matches_field(filename or "", "tech", "онк160с")
            or _requirement_matches_field(filepath or "", "tech", "онк160с")
        ):
            continue

        context_rows.append(row)

    latin_hits = []
    cyr_hits = []

    for row in context_rows:
        file_id, filename, extension, filepath, content = row

        if re.search(latin_pattern, content, flags=re.IGNORECASE):
            latin_hits.append(row)

        if re.search(cyr_pattern, content, flags=re.IGNORECASE):
            cyr_hits.append(row)

    print("ONK160C DOCUMENTS WITH TEXT:", len(context_rows))
    print("LATIN E10 HITS:", len(latin_hits))
    print("CYRILLIC Е10 HITS:", len(cyr_hits))
    print()

    shown = set()
    for label, pattern, hits in (
        ("LATIN E10", latin_pattern, latin_hits),
        ("CYRILLIC Е10", cyr_pattern, cyr_hits),
    ):
        print("=" * 100)
        print(label)
        print("=" * 100)

        for file_id, filename, extension, filepath, content in hits[:20]:
            key = (label, file_id)
            if key in shown:
                continue
            shown.add(key)

            print("ID:", file_id)
            print("FILE:", filename)
            print("TYPE:", extension)
            print("PATH:", filepath)
            print("MATCH:", snippet(content, pattern))
            print()


if __name__ == "__main__":
    main()
