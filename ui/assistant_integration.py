from PySide6.QtWidgets import QPushButton

from assistant.provider_config import PROVIDER_OLLAMA, PROVIDER_OPENAI
from assistant.providers import get_selected_provider_status
from i18n.settings import get_language
from ui.assistant_dialog import AssistantDialog


PROVIDER_TEXT = {
    "ru": {
        "ollama_ready": "Провайдер: локальная модель (Ollama). Модель: {model}. Сервер: {endpoint}. Сетевой адаптер будет подключён следующим этапом.",
        "ollama_missing": "Провайдер: локальная модель (Ollama). Укажите адрес сервера и имя модели в Настройках.",
        "openai_ready": "Провайдер: OpenAI API. Модель: {model}. API-ключ сохранён и зашифрован Windows DPAPI. Сетевой адаптер будет подключён следующим этапом.",
        "openai_missing": "Провайдер: OpenAI API. Укажите модель и сохраните API-ключ в Настройках.",
    },
    "uk": {
        "ollama_ready": "Провайдер: локальна модель (Ollama). Модель: {model}. Сервер: {endpoint}. Мережевий адаптер буде підключено наступним етапом.",
        "ollama_missing": "Провайдер: локальна модель (Ollama). Вкажіть адресу сервера та назву моделі в Налаштуваннях.",
        "openai_ready": "Провайдер: OpenAI API. Модель: {model}. API-ключ збережено та зашифровано Windows DPAPI. Мережевий адаптер буде підключено наступним етапом.",
        "openai_missing": "Провайдер: OpenAI API. Вкажіть модель і збережіть API-ключ у Налаштуваннях.",
    },
    "en": {
        "ollama_ready": "Provider: local model (Ollama). Model: {model}. Server: {endpoint}. The network adapter will be connected in the next stage.",
        "ollama_missing": "Provider: local model (Ollama). Set the server address and model name in Settings.",
        "openai_ready": "Provider: OpenAI API. Model: {model}. The API key is stored and encrypted with Windows DPAPI. The network adapter will be connected in the next stage.",
        "openai_missing": "Provider: OpenAI API. Set a model and save an API key in Settings.",
    },
}


def _provider_status_text(settings):
    language = get_language(settings)
    language = language if language in PROVIDER_TEXT else "ru"
    text = PROVIDER_TEXT[language]
    status = get_selected_provider_status(settings)

    if status.provider_id == PROVIDER_OPENAI:
        key = "openai_ready" if status.configured else "openai_missing"
    else:
        key = "ollama_ready" if status.configured else "ollama_missing"

    return text[key].format(
        model=status.model or "—",
        endpoint=status.endpoint or "—",
    )


def install_ai_assistant(main_window):
    if getattr(main_window, "_v15_ai_assistant_installed", False):
        return

    main_window._v15_ai_assistant_installed = True

    central = main_window.centralWidget()
    if central is None or central.layout() is None:
        raise RuntimeError("MainWindow layout not found")

    main_layout = central.layout()
    left_item = main_layout.itemAt(0)
    left_layout = left_item.layout() if left_item else None

    if left_layout is None:
        raise RuntimeError("Left panel layout not found")

    button = QPushButton("ZervDiag AI")
    button.setToolTip(
        "Grounded AI: first retrieve local SQLite sources, then ask a model."
    )
    main_window.btn_ai_assistant = button

    search_index = left_layout.indexOf(main_window.btn_search)
    insert_index = search_index + 1 if search_index >= 0 else 2
    left_layout.insertWidget(insert_index, button)

    def open_assistant():
        dialog = AssistantDialog(main_window)
        dialog.model_label.setText(_provider_status_text(main_window.settings))
        dialog.exec()

    button.clicked.connect(open_assistant)
