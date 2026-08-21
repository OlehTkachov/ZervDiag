from abc import ABC, abstractmethod
from dataclasses import dataclass

from assistant.provider_config import (
    PROVIDER_OLLAMA,
    PROVIDER_OPENAI,
    get_ai_provider_config,
    has_openai_api_key,
)


@dataclass(frozen=True)
class ProviderStatus:
    provider_id: str
    configured: bool
    model: str
    endpoint: str
    reason: str


class AIProvider(ABC):
    provider_id = ""

    @abstractmethod
    def status(self, settings):
        raise NotImplementedError

    @abstractmethod
    def generate(self, *, prompt, settings):
        raise NotImplementedError


class OllamaProvider(AIProvider):
    provider_id = PROVIDER_OLLAMA

    def status(self, settings):
        config = get_ai_provider_config(settings)
        configured = bool(config.ollama_url and config.ollama_model)
        reason = "" if configured else "Local server URL or model is not configured"
        return ProviderStatus(
            provider_id=self.provider_id,
            configured=configured,
            model=config.ollama_model,
            endpoint=config.ollama_url,
            reason=reason,
        )

    def generate(self, *, prompt, settings):
        raise RuntimeError(
            "Ollama network adapter is not enabled yet. "
            "Provider configuration is ready."
        )


class OpenAIProvider(AIProvider):
    provider_id = PROVIDER_OPENAI

    def status(self, settings):
        config = get_ai_provider_config(settings)
        key_ready = has_openai_api_key(settings)
        configured = bool(config.openai_model and key_ready)

        if configured:
            reason = ""
        elif not config.openai_model:
            reason = "OpenAI model is not configured"
        else:
            reason = "OpenAI API key is not configured"

        return ProviderStatus(
            provider_id=self.provider_id,
            configured=configured,
            model=config.openai_model,
            endpoint="",
            reason=reason,
        )

    def generate(self, *, prompt, settings):
        raise RuntimeError(
            "OpenAI network adapter is not enabled yet. "
            "Provider configuration and encrypted key storage are ready."
        )


_PROVIDERS = {
    PROVIDER_OLLAMA: OllamaProvider(),
    PROVIDER_OPENAI: OpenAIProvider(),
}


def get_provider(provider_id):
    return _PROVIDERS.get(provider_id, _PROVIDERS[PROVIDER_OLLAMA])


def get_selected_provider(settings):
    config = get_ai_provider_config(settings)
    return get_provider(config.provider)


def get_selected_provider_status(settings):
    return get_selected_provider(settings).status(settings)
