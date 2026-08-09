from database.db import get_connection


def search_files(query):
    conn = get_connection()
    cursor = conn.cursor()

    query = query.strip()

    if not query:
        conn.close()
        return []

    search_text = f"%{query}%"

    cursor.execute("""
        SELECT filename, extension, filepath
        FROM files
        WHERE filename LIKE ?
           OR content LIKE ?
        ORDER BY filename
    """, (
        search_text,
        search_text,
    ))

    results = cursor.fetchall()

    conn.close()

    return results