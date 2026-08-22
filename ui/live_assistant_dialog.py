from PySide6.QtCore import QThread, Signal

from assistant.providers import (
    get_selected_provider,
    get_selected_provider_status,
)
from ui.assistant_dialog import AssistantDialog


TEXT = {
    "ru": {
        "generating": "Источники найдены. Локальная модель формирует ответ...",
        "ready": "Ответ модели готов. Ниже сохранён и использованный grounded-контекст.",
        "not_configured": "Источники найдены, но модель не настроена. Укажите Ollama URL и имя модели в Настройках.",
        "error": "Ошибка модели: {message}",
        "answer": "ОТВЕТ МОДЕЛИ",
        "context": "ИСПОЛЬЗОВАННЫЙ КОНТЕКСТ",
        "wait": "Модель ещё формирует ответ. Дождитесь завершения.",
    },
    "uk": {
        "generating": "Джерела знайдено. Локальна модель формує відповідь...",
        "ready": "Відповідь моделі готова. Нижче збережено використаний grounded-контекст.",
        "not_configured": "Джерела знайдено, але модель не налаштована. Вкажіть Ollama URL та назву моделі в Налаштуваннях.",
        "error": "Помилка моделі: {message}",
        "answer": "ВІДПОВІДЬ МОДЕЛІ",
        "context": "ВИКОРИСТАНИЙ КОНТЕКСТ",
        "wait": "Модель ще формує відповідь. Дочекайтеся завершення.",
    },
    "en": {
        "generating": "Sources found. The local model is generating an answer...",
        "ready": "Model answer ready. The grounded context used is preserved below it.",
        "not_configured": "Sources found, but the model is not configured. Set the Ollama URL and model name in Settings.",
        "error": "Model error: {message}",
        "answer": "MODEL ANSWER",
        "context": "GROUNDING CONTEXT USED",
        "wait": "The model is still generating an answer. Wait for it to finish.",
    },
}


def _t(language, key, **values):
    language = language if language in TEXT else "ru"
    text = TEXT[language].get(key, key)
    return text.format(**values) if values else text


class AssistantGenerationWorker(QThread):
    finished_answer = Signal(str)
    failed = Signal(str)

    def __init__(self, preparation, settings, parent=None):
        super().__init__(parent)
        self.preparation = preparation
        self.settings = settings

    def run(self):
        try:
            provider = get_selected_provider(self.settings)
            answer = provider.generate(
                prompt=self.preparation.prompt,
                settings=self.settings,
            )
            self.finished_answer.emit(answer)
        except Exception as error:
            self.failed.emit(str(error))


class LiveAssistantDialog(AssistantDialog):
    """
    Existing verified retrieval UI plus a real provider call.

    Retrieval remains SQLite-only. The model is called only after at least one
    local source was found. The exact context sent to the model stays visible
    below the answer for diagnostics and citation verification.
    """

    def __init__(self, main_window):
        super().__init__(main_window)
        self.model_worker = None
        self._grounded_context_preview = ""

    def context_ready(self, preparation):
        super().context_ready(preparation)

        self._grounded_context_preview = self.preview.toPlainText()

        if not preparation.sources:
            return

        provider_status = get_selected_provider_status(self.settings)
        if not provider_status.configured:
            self.status.setText(_t(self.language, "not_configured"))
            return

        self.prepare_button.setEnabled(False)
        self.close_button.setEnabled(False)
        self.status.setText(_t(self.language, "generating"))

        self.logger.info(
            "AI MODEL REQUEST | provider=%s | model=%r | sources=%s",
            provider_status.provider_id,
            provider_status.model,
            len(preparation.sources),
        )

        self.model_worker = AssistantGenerationWorker(
            preparation,
            self.settings,
            self,
        )
        self.model_worker.finished_answer.connect(self.answer_ready)
        self.model_worker.failed.connect(self.answer_error)
        self.model_worker.start()

    def answer_ready(self, answer):
        combined = (
            f"{_t(self.language, 'answer')}\n"
            "====================\n"
            f"{answer.strip()}\n\n"
            f"{_t(self.language, 'context')}\n"
            "====================\n"
            f"{self._grounded_context_preview}"
        )
        self.preview.setPlainText(combined)
        self.copy_button.setEnabled(True)
        self.prepare_button.setEnabled(True)
        self.close_button.setEnabled(True)
        self.status.setText(_t(self.language, "ready"))

        self.logger.info("AI MODEL READY | chars=%s", len(answer or ""))
        self._cleanup_model_worker()

    def answer_error(self, message):
        self.preview.setPlainText(self._grounded_context_preview)
        self.prepare_button.setEnabled(True)
        self.close_button.setEnabled(True)
        self.status.setText(
            _t(self.language, "error", message=message)
        )
        self.logger.error("AI MODEL ERROR | %s", message)
        self._cleanup_model_worker()

    def _cleanup_model_worker(self):
        if self.model_worker:
            self.model_worker.deleteLater()
            self.model_worker = None

    def reject(self):
        if self.model_worker and self.model_worker.isRunning():
            self.status.setText(_t(self.language, "wait"))
            return
        super().reject()
