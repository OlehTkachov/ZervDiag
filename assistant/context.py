import re
from dataclasses import dataclass

from assistant.query_planner import plan_search_query
from database.db import get_connection
from search.search import (
    FAULT_CODE_RE,
    _requirement_pattern,
    query_requirements,
    search_files,
)


@dataclass(frozen=True)
class AssistantSource:
    file_id: int
    filename: str
    filepath: str
    context: str


def _priority_requirements(requirements):
    """
    Для диагностического вопроса сначала ищем код ошибки,
    затем технические обозначения, затем обычные слова.
    """
    faults = []
    tech = []
    words = []

    for kind, value in requirements:
        if (
            kind == "tech"
            and FAULT_CODE_RE.fullmatch(value)
        ):
            faults.append((kind, value))
        elif kind == "tech":
            tech.append((kind, value))
        else:
            words.append((kind, value))

    return faults + tech + words


def _extract_context_chunk(
    text,
    requirements,
    max_chars=3200,
):
    text = text or ""

    if not text:
        return ""

    anchor = None

    for kind, value in _priority_requirements(
        requirements
    ):
        match = re.search(
            _requirement_pattern(kind, value),
            text,
            flags=re.IGNORECASE,
        )

        if match:
            anchor = match.start()
            break

    if anchor is None:
        return text[:max_chars].strip()

    before = max_chars // 3
    start = max(
        0,
        anchor - before,
    )
    end = min(
        len(text),
        start + max_chars,
    )

    if end - start < max_chars:
        start = max(
            0,
            end - max_chars,
        )

    chunk = (
        text[start:end]
        .replace("\x00", " ")
        .strip()
    )

    if start > 0:
        chunk = "..." + chunk

    if end < len(text):
        chunk += "..."

    return chunk


def retrieve_local_context(
    question,
    search_query=None,
    max_documents=8,
    chunk_chars=3200,
    max_total_chars=24000,
):
    """
    Готовит контекст для будущего ИИ только из SQLite.

    Важно:
    - документы не гидратируются и не скачиваются;
    - наружу ничего не отправляется;
    - разговорный вопрос сначала проходит консервативный query planner;
    - search_query можно передать явно, если нужен точный контроль.
    """
    question = (question or "").strip()

    if search_query is None:
        plan = plan_search_query(
            question
        )
        retrieval_query = (
            plan.search_query
        )
    else:
        retrieval_query = (
            search_query or ""
        ).strip()

    if not retrieval_query:
        return []

    results = search_files(
        retrieval_query,
        limit=max_documents,
    )

    if not results:
        return []

    file_ids = [
        result.file_id
        for result in results
    ]

    placeholders = ",".join(
        "?"
        for _ in file_ids
    )

    conn = get_connection()

    try:
        rows = conn.execute(
            f"""
            SELECT
                id,
                filename,
                filepath,
                COALESCE(content, '')
            FROM files
            WHERE id IN ({placeholders})
            """,
            file_ids,
        ).fetchall()
    finally:
        conn.close()

    by_id = {
        row[0]: row
        for row in rows
    }

    requirements = query_requirements(
        retrieval_query
    )

    sources = []
    used_chars = 0

    for result in results:
        row = by_id.get(
            result.file_id
        )

        if not row:
            continue

        (
            file_id,
            filename,
            filepath,
            content,
        ) = row

        if not (content or "").strip():
            continue

        remaining = (
            max_total_chars
            - used_chars
        )

        if remaining <= 0:
            break

        allowed_chars = min(
            chunk_chars,
            remaining,
        )

        context = _extract_context_chunk(
            content,
            requirements,
            max_chars=allowed_chars,
        )

        if not context:
            continue

        sources.append(
            AssistantSource(
                file_id=file_id,
                filename=filename or "",
                filepath=filepath or "",
                context=context,
            )
        )

        used_chars += len(context)

    return sources
