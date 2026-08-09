from database.db import get_connection
from search.search_result import SearchResult


def make_snippet(text, query, radius=150):

    if not text:
        return ""

    text_lower = text.lower()

    for word in query.lower().split():

        position = text_lower.find(word)

        if position != -1:

            start = max(
                0,
                position - radius
            )

            end = min(
                len(text),
                position + len(word) + radius
            )

            result = text[start:end].replace(
                "\n",
                " "
            )

            if start > 0:
                result = "..." + result

            if end < len(text):
                result += "..."

            return result

    return ""


def search_files(query):

    query = query.strip()

    if not query:
        return []

    words = query.lower().split()

    conn = get_connection()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT
            id,
            filename,
            extension,
            filepath,
            content,
            is_cloud
        FROM files
        ORDER BY filepath
    """)

    rows = cursor.fetchall()

    conn.close()

    results = []

    # Пути уже показанных файлов
    seen_paths = set()

    for row in rows:

        file_id = row[0]
        filename = row[1] or ""
        extension = row[2] or ""
        filepath = row[3] or ""
        content = row[4] or ""
        is_cloud = bool(row[5])

        # Нормализуем путь
        normalized_path = filepath.strip().lower()

        # Защита от дублей
        if normalized_path in seen_paths:
            continue

        searchable_text = (
            filename
            + " "
            + filepath
            + " "
            + content
        ).lower()

        if all(
            word in searchable_text
            for word in words
        ):

            seen_paths.add(
                normalized_path
            )

            snippet = make_snippet(
                content,
                query
            )

            if not snippet:

                snippet = (
                    "Найдено в имени "
                    "или пути файла"
                )

            results.append(
                SearchResult(
                    file_id=file_id,
                    filename=filename,
                    extension=extension,
                    filepath=filepath,
                    snippet=snippet,
                    is_cloud=is_cloud
                )
            )

    return results