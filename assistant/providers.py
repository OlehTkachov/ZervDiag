import json
from abc import ABC, abstractmethod
from dataclasses import dataclass
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from assistant.provider_config import (
    PROVIDER_OLLAMA,
    PROVIDER_OPENAI,
    get_ai_provider_config,
    has_openai_api_key,
)


OLLAMA_TIMEOUT_SECONDS = 240
OLLAMA_MAX_OUTPUT_TOKENS = 180


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
        config = get_ai_provider_config(settings)

        if not config.ollama_url:
            raise RuntimeError("Ollama server URL is not configured")
        if not config.ollama_model:
            raise RuntimeError("Ollama model is not configured")

        endpoint = config.ollama_url.rstrip("/") + "/api/chat"
        payload = {
            "model": config.ollama_model,
            "messages": [
                {
                    "role": "system",
                    "content": prompt.system,
                },
                {
                    "role": "user",
                    "content": prompt.user,
                },
            ],
            "stream": False,
            "keep_alive": "10m",
            "options": {
                "temperature": 0.1,
                "num_predict": OLLAMA_MAX_OUTPUT_TOKENS,
            },
        }

        request = Request(
            endpoint,
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )

        try:
            with urlopen(request, timeout=OLLAMA_TIMEOUT_SECONDS) as response:
                raw = response.read()
        except HTTPError as error:
            try:
                details = error.read().decode("utf-8", errors="replace").strip()
            except Exception:
                details = ""
            suffix = f": {details}" if details else ""
            raise RuntimeError(
                f"Ollama returned HTTP {error.code}{suffix}"
            ) from error
        except URLError as error:
            raise RuntimeError(
                f"Cannot connect to Ollama at {config.ollama_url}: {error.reason}"
            ) from error
        except TimeoutError as error:
            raise RuntimeError("Ollama response timed out") from error

        try:
            data = json.loads(raw.decode("utf-8"))
        except Exception as error:
            raise RuntimeError("Ollama returned an invalid JSON response") from error

        answer = str(
            (data.get("message") or {}).get("content") or ""
        ).strip()

        if not answer:
            details = str(data.get("error") or "").strip()
            raise RuntimeError(details or "Ollama returned an empty answer")

        return answer


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
