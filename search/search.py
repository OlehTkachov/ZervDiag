import re

from database.db import get_connection
from search.search_result import SearchResult


TECH_RE = re.compile(
    r"(?=.*[A-Za-zА-Яа-яЁё])"
    r"(?=.*\d)"
    r"[A-Za-zА-Яа-яЁё0-9]"
    r"[A-Za-zА-Яа-яЁё0-9._/+\\-]*"
)

ALPHA_RE = re.compile(r"^[A-Za-zА-Яа-яЁё]+$")
DIGIT_OR_SUFFIX_RE = re.compile(r"^\d+[A-Za-zА-Яа-яЁё]*$")

TECH_PREFIX_STOPWORDS = {
    "в", "на", "за", "от", "до", "по", "и", "или",
    "с", "со", "из", "для", "к", "о", "об", "про",
    "при", "год", "года", "in", "on", "at", "by",
    "of", "for", "to", "from", "and", "or", "with", "year",
}

WORD_CHAR_CLASS = r"0-9A-Za-zА-Яа-яЁё"


def _raw_tokens(query):
    return [
        token.lower()
        for token in re.findall(
            r"[A-Za-zА-Яа-яЁё0-9._/+\\-]+",
            query,
        )
        if token
    ]


def _compact(text):
    return re.sub(
        r"[^0-9a-zа-яё]+",
        "",
        (text or "").lower(),
    )


def _is_tech_token(token):
    return bool(TECH_RE.fullmatch(token))


def _can_be_tech_prefix(token):
    compact = _compact(token)
    return bool(
        compact
        and len(compact) <= 4
        and compact not in TECH_PREFIX_STOPWORDS
        and ALPHA_RE.fullmatch(compact)
    )


def _merge_technical_tokens(tokens):
    """
    Склеивает технические обозначения, разделённые пробелами.

    AC 35L   -> AC35L
    AC 35 L  -> AC35L
    КС 55724 -> КС55724
    ОНК 160  -> ОНК160
    RT 100   -> RT100
    """
    merged = []
    i = 0

    while i < len(tokens):
        first = tokens[i]

        if i + 1 < len(tokens) and _can_be_tech_prefix(first):
            second = _compact(tokens[i + 1])

            if second and DIGIT_OR_SUFFIX_RE.fullmatch(second):
                candidate = _compact(first) + second
                consumed = 2

                if i + 2 < len(tokens):
                    third = _compact(tokens[i + 2])

                    if (
                        third
                        and len(third) <= 2
                        and ALPHA_RE.fullmatch(third)
                    ):
                        candidate += third
                        consumed = 3

                if _is_tech_token(candidate):
                    merged.append(candidate)
                    i += consumed
                    continue

        merged.append(first)
        i += 1

    return merged


def _tokenize(query):
    return _merge_technical_tokens(_raw_tokens(query))


def query_requirements(query):
    requirements = []

    for token in _tokenize(query):
        if _is_tech_token(token):
            requirements.append(("tech", _compact(token)))
        else:
            requirements.append(("word", token))

    return requirements


def _word_pattern(value):
    """
    Обычное слово ищем как самостоятельный токен.

    Например PAT не должен совпадать с comPATible.
    Дефис, пробел, подчёркивание и слэш считаются разделителями.
    """
    return (
        rf"(?<![{WORD_CHAR_CLASS}])"
        + re.escape(value)
        + rf"(?![{WORD_CHAR_CLASS}])"
    )


def _tech_pattern(value):
    """
    Технический код допускает реальные разделители между символами:
    AC35L / AC 35L / AC-35L / AC/35L.

    Важно: мы НЕ удаляем разделители из целого документа. Иначе
    символы из разных слов могли случайно склеиваться в код.
    """
    return r"[\s._/+\\-]*".join(
        re.escape(char)
        for char in value
    )


def _requirement_pattern(kind, value):
    if kind == "word":
        return _word_pattern(value)

    return _tech_pattern(value)


def _requirement_matches_field(field, kind, value):
    field = field or ""

    return bool(
        re.search(
            _requirement_pattern(kind, value),
            field,
            flags=re.IGNORECASE,
        )
    )


def _matches_fields(fields, requirements):
    """
    Каждое условие должно встретиться хотя бы в одном поле:
    имя, полный путь или содержимое.

    Разные условия могут находиться в разных полях.
    """
    for kind, value in requirements:
        if not any(
            _requirement_matches_field(field, kind, value)
            for field in fields
        ):
            return False

    return True


def make_snippet(text, query, radius=180):
    if not text:
        return ""

    requirements = query_requirements(query)

    # Для диагностических/технических запросов сначала показываем
    # совпадение самого кода (AC35L, КС55724, ОНК160...), а уже
    # потом общих слов вроде Terex/PAT.
    requirements = sorted(
        requirements,
        key=lambda item: 0 if item[0] == "tech" else 1,
    )

    for kind, value in requirements:
        match = re.search(
            _requirement_pattern(kind, value),
            text,
            flags=re.IGNORECASE,
        )

        if not match:
            continue

        start = max(0, match.start() - radius)
        end = min(len(text), match.end() + radius)

        result = (
            text[start:end]
            .replace("\r", " ")
            .replace("\n", " ")
        )

        if start > 0:
            result = "..." + result

        if end < len(text):
            result += "..."

        return result

    return ""


def _rough_sql_filter(requirements):
    """
    Грубый SQL-фильтр только уменьшает число кандидатов.
    Окончательная проверка всегда выполняется Python-регулярками.
    """
    conditions = []
    parameters = []

    for kind, value in requirements:
        if kind == "word":
            pattern = f"%{value}%"
        else:
            prefix_match = re.match(r"[a-zа-яё]+", value)
            prefix = (
                prefix_match.group(0)
                if prefix_match
                else value[:2]
            )
            pattern = f"%{prefix}%"

        conditions.append(
            """
            (
                lower(filename) LIKE ?
                OR lower(filepath) LIKE ?
                OR lower(content) LIKE ?
            )
            """
        )

        parameters.extend([pattern, pattern, pattern])

    return " AND ".join(conditions), parameters


def _relevance_score(filename, filepath, content, requirements):
    """
    Имя и путь сильнее содержимого.

    Это не меняет условие совпадения, а только поднимает наиболее
    очевидные каталоги/руководства выше в выдаче.
    """
    score = 0

    for kind, value in requirements:
        if _requirement_matches_field(filename, kind, value):
            score += 120
        elif _requirement_matches_field(filepath, kind, value):
            score += 80
        elif _requirement_matches_field(content, kind, value):
            score += 10

    return score


def search_files(query, limit=200):
    """
    Поиск только по SQLite.

    Полный путь остаётся частью поиска, потому что структура
    папок несёт смысловую информацию о документе.
    """
    query = query.strip()

    if not query:
        return []

    requirements = query_requirements(query)

    if not requirements:
        return []

    conditions, parameters = _rough_sql_filter(requirements)

    conn = get_connection()

    try:
        rows = conn.execute(
            f"""
            SELECT
                id,
                filename,
                extension,
                filepath,
                content,
                is_cloud
            FROM files
            WHERE {conditions}
            LIMIT 2500
            """,
            parameters,
        ).fetchall()
    finally:
        conn.close()

    ranked = []

    for row in rows:
        (
            file_id,
            filename,
            extension,
            filepath,
            content,
            is_cloud,
        ) = row

        filename = filename or ""
        filepath = filepath or ""
        content = content or ""

        if not _matches_fields(
            (
                filename,
                filepath,
                content,
            ),
            requirements,
        ):
            continue

        snippet = make_snippet(content, query)

        if not snippet:
            snippet = "Найдено в имени или пути файла"

        result = SearchResult(
            file_id=file_id,
            filename=filename,
            extension=extension or "",
            filepath=filepath,
            snippet=snippet,
            is_cloud=bool(is_cloud),
        )

        score = _relevance_score(
            filename,
            filepath,
            content,
            requirements,
        )

        ranked.append(
            (
                score,
                filename.lower(),
                result,
            )
        )

    ranked.sort(
        key=lambda item: (
            -item[0],
            item[1],
        )
    )

    return [
        item[2]
        for item in ranked[:limit]
    ]
