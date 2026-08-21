import base64
import ctypes
import os
from dataclasses import dataclass
from ctypes import wintypes


PROVIDER_OLLAMA = "ollama"
PROVIDER_OPENAI = "openai"

KEY_PROVIDER = "ai/provider"
KEY_OLLAMA_URL = "ai/ollama_url"
KEY_OLLAMA_MODEL = "ai/ollama_model"
KEY_OPENAI_MODEL = "ai/openai_model"
KEY_OPENAI_KEY_DPAPI = "ai/openai_key_dpapi"

DEFAULT_OLLAMA_URL = "http://127.0.0.1:11434"


@dataclass(frozen=True)
class AIProviderConfig:
    provider: str
    ollama_url: str
    ollama_model: str
    openai_model: str


class _DATA_BLOB(ctypes.Structure):
    _fields_ = [
        ("cbData", wintypes.DWORD),
        ("pbData", ctypes.POINTER(ctypes.c_ubyte)),
    ]


def _normalize_provider(value):
    value = str(value or "").strip().lower()
    if value == PROVIDER_OPENAI:
        return PROVIDER_OPENAI
    return PROVIDER_OLLAMA


def get_ai_provider_config(settings):
    return AIProviderConfig(
        provider=_normalize_provider(
            settings.value(KEY_PROVIDER, PROVIDER_OLLAMA)
        ),
        ollama_url=str(
            settings.value(KEY_OLLAMA_URL, DEFAULT_OLLAMA_URL) or ""
        ).strip(),
        ollama_model=str(
            settings.value(KEY_OLLAMA_MODEL, "") or ""
        ).strip(),
        openai_model=str(
            settings.value(KEY_OPENAI_MODEL, "") or ""
        ).strip(),
    )


def save_ai_provider_config(
    settings,
    *,
    provider,
    ollama_url,
    ollama_model,
    openai_model,
):
    settings.setValue(KEY_PROVIDER, _normalize_provider(provider))
    settings.setValue(KEY_OLLAMA_URL, str(ollama_url or "").strip())
    settings.setValue(KEY_OLLAMA_MODEL, str(ollama_model or "").strip())
    settings.setValue(KEY_OPENAI_MODEL, str(openai_model or "").strip())
    settings.sync()


def _protect_windows_dpapi(data):
    if os.name != "nt":
        raise RuntimeError("Windows DPAPI is available only on Windows")

    data = bytes(data)
    buffer = ctypes.create_string_buffer(data)
    input_blob = _DATA_BLOB(
        len(data),
        ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)),
    )
    output_blob = _DATA_BLOB()

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32

    if not crypt32.CryptProtectData(
        ctypes.byref(input_blob),
        "ZervDiag AI API key",
        None,
        None,
        None,
        0,
        ctypes.byref(output_blob),
    ):
        raise ctypes.WinError()

    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData)
    finally:
        kernel32.LocalFree(output_blob.pbData)


def _unprotect_windows_dpapi(data):
    if os.name != "nt":
        raise RuntimeError("Windows DPAPI is available only on Windows")

    data = bytes(data)
    buffer = ctypes.create_string_buffer(data)
    input_blob = _DATA_BLOB(
        len(data),
        ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)),
    )
    output_blob = _DATA_BLOB()

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32

    if not crypt32.CryptUnprotectData(
        ctypes.byref(input_blob),
        None,
        None,
        None,
        None,
        0,
        ctypes.byref(output_blob),
    ):
        raise ctypes.WinError()

    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData)
    finally:
        kernel32.LocalFree(output_blob.pbData)


def store_openai_api_key(settings, api_key):
    api_key = str(api_key or "").strip()
    if not api_key:
        return False

    protected = _protect_windows_dpapi(api_key.encode("utf-8"))
    settings.setValue(
        KEY_OPENAI_KEY_DPAPI,
        base64.b64encode(protected).decode("ascii"),
    )
    settings.sync()
    return True


def load_openai_api_key(settings):
    encoded = str(settings.value(KEY_OPENAI_KEY_DPAPI, "") or "").strip()
    if not encoded:
        return ""

    try:
        protected = base64.b64decode(encoded.encode("ascii"), validate=True)
        plain = _unprotect_windows_dpapi(protected)
        return plain.decode("utf-8").strip()
    except Exception:
        return ""


def has_openai_api_key(settings):
    return bool(load_openai_api_key(settings))


def clear_openai_api_key(settings):
    settings.remove(KEY_OPENAI_KEY_DPAPI)
    settings.sync()
