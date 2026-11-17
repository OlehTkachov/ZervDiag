from dataclasses import dataclass

from assistant.context import AssistantSource, retrieve_local_context
from assistant.prompt import GroundedPrompt, build_grounded_prompt
from assistant.query_planner import SearchPlan, plan_search_query


@dataclass(frozen=True)
class AssistantPreparation:
    question: str
    search_plan: SearchPlan
    sources: tuple[AssistantSource, ...]
    prompt: GroundedPrompt


def prepare_assistant_request(
    question,
    *,
    max_documents=4,
    chunk_chars=1800,
    max_total_chars=6500,
):
    """Prepare a compact grounded request using SQLite-only retrieval."""
    question = (question or "").strip()

    plan = plan_search_query(question)

    sources = tuple(
        retrieve_local_context(
            question,
            search_query=plan.search_query,
            max_documents=max_documents,
            chunk_chars=chunk_chars,
            max_total_chars=max_total_chars,
        )
    )

    prompt = build_grounded_prompt(
        question,
        sources,
    )

    return AssistantPreparation(
        question=question,
        search_plan=plan,
        sources=sources,
        prompt=prompt,
    )
