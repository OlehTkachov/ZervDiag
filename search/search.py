import re

from database.db import get_connection
from search.search_result import SearchResult
from indexer.single_file import index_single_file


def normalize_text(text):
    if not text:
        return ""

    text = text.lower()

    # Убираем переносы строк внутри слов
    text = re.sub(r"(?<=\w)-\s+", "", text)

    # Склеиваем характерные разрывы OCR/PDF:
    # "аккум ул я тор" -> "аккумулятор"
    previous = None

    while previous != text:
        previous = text

        text = re.sub(
            r"(?<=\w)\s+(?=\w)",
            " ",
            text
        )

    # Дополнительно создаём вариант без пробелов
    # для поиска слов, разбитых PDF-извлечением.
    compact = re.sub(r"\s+", "", text)

    return text + "\n" + compact


def make_snippet(text, query, radius=150):
    if not text:
        return ""

    normalized = normalize_text(text)
    query = query.lower()

    position = normalized.find(query)

    if position == -1:
        compact = re.sub(r"\s+", "", text.lower())
        position = compact.find(re.sub(r"\s+", "", query))

        if position == -1:
            return ""

        return "..." + text[:radius].replace("\n", " ") + "..."

    start = max(0, position - radius)
    end = min(len(normalized), position + len(query) + radius)

    result = normalized[start:end].replace("\n", " ")

    if start > 0:
        result = "..." + result

    if end < len(normalized):
        result += "..."

    return result


def matches(text, words):
    normalized = normalize_text(text)

    for word in words:
        if word not in normalized:
            return False

    return True


def search_files(query):

    query = query.strip()

    if not query:
        return []

    words = query.lower().split()

    conn = get_connection()

    rows = conn.execute("""
        SELECT
            id,
            filename,
            extension,
            filepath,
            content,
            is_cloud
        FROM files
        ORDER BY filepath
    """).fetchall()

    conn.close()

    results = []
    candidates = []

    for row in rows:

        file_id, filename, extension, filepath, content, is_cloud = row

        filename = filename or ""
        filepath = filepath or ""
        content = content or ""

        name_path = (
            filename + " " + filepath
        ).lower()

        # Обычный поиск
        if matches(
            name_path + " " + content,
            words
        ):

            snippet = make_snippet(
                content,
                query
            )

            if not snippet:
                snippet = "Найдено в имени или пути файла"

            results.append(
                SearchResult(
                    file_id=file_id,
                    filename=filename,
                    extension=extension,
                    filepath=filepath,
                    snippet=snippet,
                    is_cloud=bool(is_cloud)
                )
            )

            continue

        # Cloud PDF трогаем только если запрос
        # найден в имени или пути.
        if (
            is_cloud
            and extension.lower() == ".pdf"
            and matches(name_path, words)
        ):
            candidates.append(row)

    # Индексируем только кандидатов
    for row in candidates:

        file_id = row[0]
        filename = row[1]
        filepath = row[3]

        print("Загрузка:", filename)

        try:
            if not index_single_file(
                file_id,
                filepath
            ):
                continue

        except Exception as error:
            print("Ошибка:", error)
            continue

        conn = get_connection()

        fresh = conn.execute("""
            SELECT
                id,
                filename,
                extension,
                filepath,
                content,
                is_cloud
            FROM files
            WHERE id = ?
        """, (file_id,)).fetchone()

        conn.close()

        if not fresh:
            continue

        content = fresh[4] or ""

        if matches(
            (fresh[1] or "") + " " +
            (fresh[3] or "") + " " +
            content,
            words
        ):

            snippet = make_snippet(
                content,
                query
            )

            results.append(
                SearchResult(
                    file_id=fresh[0],
                    filename=fresh[1],
                    extension=fresh[2],
                    filepath=fresh[3],
                    snippet=snippet,
                    is_cloud=bool(fresh[5])
                )
            )

    return results
