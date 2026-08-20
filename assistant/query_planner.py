from dataclasses import dataclass

from search.search import (
    TECH_PREFIX_STOPWORDS,
    _compact,
    _is_tech_token,
    _tokenize,
)


QUESTION_STOPWORDS = {
    "что", "как", "почему", "где", "когда", "какой", "какая", "какие",
    "проверить", "означает", "значит", "появляется", "появилась", "ошибка",
    "код", "не", "работает", "работать", "у", "на", "в", "и", "или",
    "що", "як", "чому", "де", "коли", "який", "яка", "які", "перевірити",
    "означає", "з'являється", "помилка", "код", "не", "працює", "у", "на",
    "what", "how", "why", "where", "when", "which", "check", "means",
    "error", "code", "not", "working", "works", "on", "in", "at", "and",
}


@dataclass(frozen=True)
class SearchPlan:
    question: str
    search_query: str
    technical_tokens: tuple[str, ...]
    context_tokens: tuple[str, ...]


def _is_context_word(token):
    compact = _compact(token)

    return bool(
        compact
        and compact not in TECH_PREFIX_STOPWORDS
        and compact not in QUESTION_STOPWORDS
        and compact.isalpha()
        and 2 <= len(compact) <= 24
    )


def plan_search_query(question):
    """
    Делает консервативный технический запрос из разговорного вопроса.

    Примеры:
      "На ОНК-160С появляется E10. Что проверить?"
          -> "онк160с e10"
      "Terex AC35L не работает телескопирование"
          -> "terex ac35l"
      "PAT DS350 E15 что значит"
          -> "pat ds350 e15"

    Планировщик специально не пытается быть ИИ. Его задача — не отправлять
    в строгий SQLite-поиск весь разговорный текст, где каждое лишнее слово
    могло бы стать обязательным условием.
    """
    question = (question or "").strip()

    if not question:
        return SearchPlan(
            question="",
            search_query="",
            technical_tokens=(),
            context_tokens=(),
        )

    tokens = _tokenize(question)
    tech_indexes = [
        index
        for index, token in enumerate(tokens)
        if _is_tech_token(token)
    ]

    technical = []
    context = []

    for index in tech_indexes:
        value = _compact(tokens[index])

        if value and value not in technical:
            technical.append(value)

    if tech_indexes:
        first_tech = tech_indexes[0]

        # Бренд/семейство обычно стоит непосредственно перед моделью:
        # Terex AC35L, PAT DS350. Служебные слова вроде "на" отбрасываем.
        if first_tech > 0:
            candidate = tokens[first_tech - 1]

            if _is_context_word(candidate):
                context.append(candidate.lower())

        # Чисто числовой код ошибки иногда встречается после слова "ошибка"
        # или "код". Добавляем его как контекст только при явном маркере.
        for index, token in enumerate(tokens):
            compact = _compact(token)

            if not compact.isdigit() or not (2 <= len(compact) <= 8):
                continue

            if index <= 0:
                continue

            marker = _compact(tokens[index - 1])

            if marker in {
                "ошибка", "код", "помилка", "error", "code",
            }:
                if compact not in context:
                    context.append(compact)

        parts = context + technical

        return SearchPlan(
            question=question,
            search_query=" ".join(parts),
            technical_tokens=tuple(technical),
            context_tokens=tuple(context),
        )

    # Пока семантического/векторного поиска нет, для вопроса без технического
    # обозначения оставляем до трёх содержательных слов. Это лучше, чем делать
    # обязательными все слова длинной разговорной фразы.
    fallback = []

    for token in tokens:
        compact = _compact(token)

        if not _is_context_word(compact):
            continue

        if compact in fallback:
            continue

        fallback.append(compact)

        if len(fallback) >= 3:
            break

    return SearchPlan(
        question=question,
        search_query=" ".join(fallback),
        technical_tokens=(),
        context_tokens=tuple(fallback),
    )
