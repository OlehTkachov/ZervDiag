from dataclasses import dataclass


SYSTEM_INSTRUCTION = """
Ты технический помощник ZervDiag.

Правила:
1. Отвечай только на основании переданных локальных источников.
2. Если источников недостаточно для вывода, прямо скажи об этом.
3. Не придумывай значения ошибок, параметры, процедуры или номера деталей.
4. Отделяй факт из документации от своего технического вывода.
5. После существенных утверждений указывай ссылки вида [S1], [S2].
6. Не утверждай, что выполнялся поиск в интернете, если интернет-источники
   отдельно не переданы.
7. При конфликте источников покажи конфликт, а не выбирай молча один вариант.
""".strip()


@dataclass(frozen=True)
class GroundedPrompt:
    system: str
    user: str


def build_grounded_prompt(
    question,
    sources,
):
    """
    Формирует provider-independent prompt.

    Позже тот же объект можно отправить в облачный или локальный LLM.
    """
    blocks = []

    for index, source in enumerate(
        sources,
        start=1,
    ):
        blocks.append(
            "\n".join(
                [
                    f"[S{index}] {source.filename}",
                    f"Путь: {source.filepath}",
                    "Фрагмент:",
                    source.context,
                ]
            )
        )

    if blocks:
        source_text = "\n\n".join(
            blocks
        )
    else:
        source_text = (
            "Локальные источники для этого вопроса "
            "не найдены."
        )

    user = (
        f"Вопрос пользователя:\n{question.strip()}\n\n"
        f"Локальные источники:\n{source_text}"
    )

    return GroundedPrompt(
        system=SYSTEM_INSTRUCTION,
        user=user,
    )
