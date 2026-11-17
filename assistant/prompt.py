from dataclasses import dataclass


SYSTEM_INSTRUCTION = """
Ты технический помощник ZervDiag.

Правила:
1. Отвечай только на основании переданных локальных источников.
2. Не используй собственные знания для заполнения пробелов в источниках.
3. Если источников недостаточно для вывода, прямо скажи об этом.
4. Не придумывай значения ошибок, параметры, процедуры, детали или номера деталей.
5. Отделяй факт из документации от своего технического вывода.
6. После существенных утверждений указывай ссылки вида [S1], [S2].
7. Не утверждай, что выполнялся поиск в интернете, если интернет-источники
   отдельно не переданы.
8. При конфликте источников покажи конфликт, а не выбирай молча один вариант.
9. Отвечай кратко и по делу. Обычно достаточно 3-8 предложений или коротких пунктов.
10. Если источник содержит точное действие изготовителя, передай его без
    расширения процедуры от себя.
""".strip()


@dataclass(frozen=True)
class GroundedPrompt:
    system: str
    user: str


def build_grounded_prompt(
    question,
    sources,
):
    """Формирует provider-independent grounded prompt."""
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
        source_text = "\n\n".join(blocks)
    else:
        source_text = "Локальные источники для этого вопроса не найдены."

    user = (
        f"Вопрос пользователя:\n{question.strip()}\n\n"
        f"Локальные источники:\n{source_text}"
    )

    return GroundedPrompt(
        system=SYSTEM_INSTRUCTION,
        user=user,
    )
