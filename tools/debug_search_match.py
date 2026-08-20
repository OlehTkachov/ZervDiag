import re
import sys

from database.db import get_connection
from search.search import (
    _requirement_matches_field,
    _requirement_pattern,
    query_requirements,
)


def _contexts(text, kind, value, radius=140, limit=3):
    text = text or ""
    pattern = _requirement_pattern(kind, value)
    found = []

    for match in re.finditer(pattern, text, flags=re.IGNORECASE):
        start = max(0, match.start() - radius)
        end = min(len(text), match.end() + radius)
        fragment = (
            text[start:end]
            .replace("\r", " ")
            .replace("\n", " ")
        )
        found.append(fragment)
        if len(found) >= limit:
            break

    return found


def main():
    if len(sys.argv) < 3:
        print('Usage: python tools/debug_search_match.py "Terex AC 35L" "RC45"')
        raise SystemExit(2)

    query = sys.argv[1]
    filename_part = sys.argv[2]
    requirements = query_requirements(query)

    print("QUERY:", query)
    print("REQUIREMENTS:", requirements)
    print("FILENAME FILTER:", filename_part)
    print()

    conn = get_connection()
    try:
        rows = conn.execute(
            """
            SELECT
                id,
                filename,
                filepath,
                COALESCE(content, '')
            FROM files
            WHERE lower(filename) LIKE lower(?)
            ORDER BY filename, id
            LIMIT 30
            """,
            (f"%{filename_part}%",),
        ).fetchall()
    finally:
        conn.close()

    if not rows:
        print("NO FILES FOUND")
        return

    for file_id, filename, filepath, content in rows:
        print("=" * 100)
        print("ID:", file_id)
        print("FILE:", filename)
        print("PATH:", filepath)
        print("CONTENT CHARS:", len(content))

        for kind, value in requirements:
            print(f"\nREQUIREMENT: {kind}={value}")

            name_match = _requirement_matches_field(
                filename,
                kind,
                value,
            )
            path_match = _requirement_matches_field(
                filepath,
                kind,
                value,
            )
            content_match = _requirement_matches_field(
                content,
                kind,
                value,
            )

            print(
                "  SOURCES:",
                f"filename={name_match}",
                f"path={path_match}",
                f"content={content_match}",
            )

            if content_match:
                for number, fragment in enumerate(
                    _contexts(content, kind, value),
                    start=1,
                ):
                    print(f"  CONTENT MATCH {number}: {fragment}")

        print()


if __name__ == "__main__":
    main()
