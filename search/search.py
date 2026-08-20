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
FAULT_CODE_RE = re.compile(r"^[A-Za-zА-Яа-яЁё]\d{1,4}$")

TECH_PREFIX_STOPWORDS = {
    "в", "на", "за", "от", "до", "по", "и", "или",
    "с", "со", "из", "для", "к", "о", "об", "про",
    "при", "год", "года", "in", "on", "at", "by",
    "of", "for", "to", "from", "and", "or", "with", "year",
}

WORD_CHAR_CLASS = r"0-9A-Za-zА-Яа-яЁё"
TECH_SEPARATOR = r"[\s._/+\\-]*"

# Если модель есть только внутри текста, считаем её основной моделью
# документа, когда она встречается на первых страницах или многократно.
MODEL_EARLY_CHAR_LIMIT = 8000
MODEL_REPEAT_MIN = 3


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

    PAT не должен совпадать с compatible.
    """
    return (
        rf"(?<![{WORD_CHAR_CLASS}])"
        + re.escape(value)
        + rf"(?![{WORD_CHAR_CLASS}])"
    )


def _tech_groups(value):
    """
    Делит компактный код на смысловые буквенно-цифровые группы.

    ac35l  -> [ac, 35, l]
    ds350  -> [ds, 350]
    онк160 -> [онк, 160]
    e15    -> [e, 15]
    """
    return re.findall(
        r"[A-Za-zА-Яа-яЁё]+|\d+",
        value,
    )


def _tech_pattern(value):
    """
    Допускает разделители только между смысловыми группами кода.

    AC35L / AC 35L / AC-35-L / AC/35L -> совпадают.
    A C 3 5 L                          -> не совпадает.

    Для моделей, оканчивающихся цифрами, допускаем буквенный суффикс
    в документе: DS350 ищет DS350GW, ОНК160 ищет ОНК160С.
    Короткие коды ошибок E15/F123 остаются строго точными.
    """
    groups = _tech_groups(value)

    if not groups:
        return r"(?!)"

    body = TECH_SEPARATOR.join(
        re.escape(group)
        for group in groups
    )

    left = rf"(?<![{WORD_CHAR_CLASS}])"

    if FAULT_CODE_RE.fullmatch(value):
        right = rf"(?![{WORD_CHAR_CLASS}])"
    elif value[-1].isdigit():
        right = r"(?![0-9])"
    else:
        right = rf"(?![{WORD_CHAR_CLASS}])"

    return left + body + right


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
    for kind, value in requirements:
        if not any(
            _requirement_matches_field(field, kind, value)
            for field in fields
        ):
            return False

    return True


def _diagnostic_context(requirements):
    """
    PAT DS350 E15:
        PAT / DS350 — контекст оборудования;
        E15         — диагностический код.
    """
    if len(requirements) < 2:
        return []

    last_kind, last_value = requirements[-1]

    if (
        last_kind == "tech"
        and FAULT_CODE_RE.fullmatch(last_value)
    ):
        return requirements[:-1]

    return []


def _content_model_evidence(content, kind, value):
    """
    Возвращает (первое_совпадение, количество_совпадений_до_лимита).

    Нужен для различия:
      - руководство по AC35L, где модель есть на титуле/много раз;
      - каталог RC45, где AC35L один раз упомянут как чужая деталь.
    """
    pattern = _requirement_pattern(kind, value)
    first_position = None
    count = 0

    for match in re.finditer(
        pattern,
        content or "",
        flags=re.IGNORECASE,
    ):
        if first_position is None:
            first_position = match.start()

        count += 1

        if count >= MODEL_REPEAT_MIN:
            break

    return first_position, count


def _model_context_supported(
    filename,
    filepath,
    content,
    requirements,
):
    """
    Для запроса вида "Terex AC35L" слово — широкий контекст бренда,
    а технический код — модель.

    Если модель есть в имени/пути, документ однозначно относится к ней.
    Если модель встречается только в тексте, принимаем документ лишь
    когда модель есть в начале текста или повторяется несколько раз.
    Одиночные поздние ссылки в каталогах других моделей отсекаются.
    """
    if len(requirements) != 2:
        return True

    tech_requirements = [
        item
        for item in requirements
        if item[0] == "tech"
        and not FAULT_CODE_RE.fullmatch(item[1])
    ]

    word_requirements = [
        item
        for item in requirements
        if item[0] == "word"
    ]

    if len(tech_requirements) != 1 or len(word_requirements) != 1:
        return True

    kind, value = tech_requirements[0]

    if (
        _requirement_matches_field(filename, kind, value)
        or _requirement_matches_field(filepath, kind, value)
    ):
        return True

    first_position, count = _content_model_evidence(
        content,
        kind,
        value,
    )

    if first_position is None:
        return False

    return (
        first_position <= MODEL_EARLY_CHAR_LIMIT
        or count >= MODEL_REPEAT_MIN
    )


def _matches_query(
    filename,
    filepath,
    content,
    requirements,
):
    if not _matches_fields(
        (
            filename,
            filepath,
            content,
        ),
        requirements,
    ):
        return False

    context = _diagnostic_context(requirements)

    if context:
        # Для диагностического запроса хотя бы одна часть контекста
        # оборудования должна быть в имени файла или пути.
        if not any(
            _requirement_matches_field(
                field,
                kind,
                value,
            )
            for kind, value in context
            for field in (filename, filepath)
        ):
            return False

    return _model_context_supported(
        filename,
        filepath,
        content,
        requirements,
    )


def _snippet_requirements(requirements):
    """Код ошибки показываем первым, затем остальные техкоды."""
    if requirements:
        kind, value = requirements[-1]
        if kind == "tech" and FAULT_CODE_RE.fullmatch(value):
            tail = requirements[-1]
            rest = requirements[:-1]
            rest = sorted(
                rest,
                key=lambda item: 0 if item[0] == "tech" else 1,
            )
            return [tail] + rest

    return sorted(
        requirements,
        key=lambda item: 0 if item[0] == "tech" else 1,
    )


def make_snippet(text, query, radius=180):
    if not text:
        return ""

    requirements = _snippet_requirements(
        query_requirements(query)
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
    """Грубый SQL-фильтр; окончательная проверка идёт регулярками."""
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

    Имя, полный путь и извлечённый текст участвуют в поиске.
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

        if not _matches_query(
            filename,
            filepath,
            content,
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
