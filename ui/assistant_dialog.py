import os

from PySide6.QtCore import QThread, Signal
from PySide6.QtWidgets import (
    QAbstractItemView,
    QApplication,
    QDialog,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QPlainTextEdit,
    QPushButton,
    QSplitter,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
)

from assistant.service import prepare_assistant_request
from diagnostics.black_box import get_app_logger
from i18n.settings import get_language


TEXT = {
    "ru": {
        "title": "ZervDiag AI — локальные источники",
        "question": "Вопрос для ИИ:",
        "placeholder": "Например: На Terex AC35L появляется E15. Что проверить?",
        "prepare": "Подобрать источники",
        "copy": "Скопировать контекст",
        "close": "Закрыть",
        "search_query": "Локальный запрос: {query}",
        "search_query_empty": "Локальный запрос: —",
        "model": "Модель: пока не подключена. Сейчас проверяется локальный контекст.",
        "sources": "Источники, которые получит модель:",
        "preview": "Подготовленный grounded-контекст:",
        "source": "Источник",
        "file": "Файл",
        "fragment": "Фрагмент",
        "path": "Полный путь",
        "ready": "Готово. Найдено источников: {count}.",
        "none": "Локальные источники не найдены. Модель запускать нельзя.",
        "busy": "Ищу источники только в SQLite...",
        "error": "Ошибка подготовки контекста: {message}",
        "empty": "Введите диагностический вопрос.",
        "copied": "Контекст скопирован в буфер обмена.",
        "wait": "Поиск источников ещё выполняется.",
    },
    "uk": {
        "title": "ZervDiag AI — локальні джерела",
        "question": "Питання для ШІ:",
        "placeholder": "Наприклад: На Terex AC35L з’являється E15. Що перевірити?",
        "prepare": "Підібрати джерела",
        "copy": "Скопіювати контекст",
        "close": "Закрити",
        "search_query": "Локальний запит: {query}",
        "search_query_empty": "Локальний запит: —",
        "model": "Модель: поки не підключена. Зараз перевіряється локальний контекст.",
        "sources": "Джерела, які отримає модель:",
        "preview": "Підготовлений grounded-контекст:",
        "source": "Джерело",
        "file": "Файл",
        "fragment": "Фрагмент",
        "path": "Повний шлях",
        "ready": "Готово. Знайдено джерел: {count}.",
        "none": "Локальні джерела не знайдено. Модель запускати не можна.",
        "busy": "Шукаю джерела тільки в SQLite...",
        "error": "Помилка підготовки контексту: {message}",
        "empty": "Введіть діагностичне питання.",
        "copied": "Контекст скопійовано до буфера обміну.",
        "wait": "Пошук джерел ще виконується.",
    },
    "en": {
        "title": "ZervDiag AI — local sources",
        "question": "Question for AI:",
        "placeholder": "Example: Terex AC35L shows E15. What should I check?",
        "prepare": "Find sources",
        "copy": "Copy context",
        "close": "Close",
        "search_query": "Local query: {query}",
        "search_query_empty": "Local query: —",
        "model": "Model: not connected yet. Local grounding is being verified first.",
        "sources": "Sources the model will receive:",
        "preview": "Prepared grounded context:",
        "source": "Source",
        "file": "File",
        "fragment": "Excerpt",
        "path": "Full path",
        "ready": "Ready. Sources found: {count}.",
        "none": "No local sources found. The model must not be run.",
        "busy": "Searching SQLite-only sources...",
        "error": "Context preparation error: {message}",
        "empty": "Enter a diagnostic question.",
        "copied": "Context copied to the clipboard.",
        "wait": "Source retrieval is still running.",
    },
}


def _t(language, key, **values):
    language = language if language in TEXT else "ru"
    text = TEXT[language].get(key, key)
    return text.format(**values) if values else text


class AssistantPreparationWorker(QThread):
    finished_result = Signal(object)
    failed = Signal(str)

    def __init__(self, question, parent=None):
        super().__init__(parent)
        self.question = question

    def run(self):
        try:
            result = prepare_assistant_request(self.question)
            self.finished_result.emit(result)
        except Exception as error:
            self.failed.emit(str(error))


class AssistantDialog(QDialog):
    def __init__(self, main_window):
        super().__init__(main_window)
        self.main_window = main_window
        self.settings = main_window.settings
        self.language = get_language(self.settings)
        self.worker = None
        self.preparation = None
        self.logger = get_app_logger()

        self.setWindowTitle(_t(self.language, "title"))
        self.resize(1450, 860)

        root = QVBoxLayout(self)

        root.addWidget(QLabel(_t(self.language, "question")))

        self.question_input = QPlainTextEdit()
        self.question_input.setPlaceholderText(_t(self.language, "placeholder"))
        self.question_input.setMaximumHeight(115)
        root.addWidget(self.question_input)

        current_query = ""
        if hasattr(main_window, "search_input"):
            current_query = main_window.search_input.text().strip()
        if current_query:
            self.question_input.setPlainText(current_query)

        buttons = QHBoxLayout()

        self.prepare_button = QPushButton(_t(self.language, "prepare"))
        self.copy_button = QPushButton(_t(self.language, "copy"))
        self.copy_button.setEnabled(False)
        self.close_button = QPushButton(_t(self.language, "close"))

        buttons.addWidget(self.prepare_button)
        buttons.addWidget(self.copy_button)
        buttons.addStretch()
        buttons.addWidget(self.close_button)
        root.addLayout(buttons)

        self.query_label = QLabel(_t(self.language, "search_query_empty"))
        self.query_label.setWordWrap(True)
        root.addWidget(self.query_label)

        self.model_label = QLabel(_t(self.language, "model"))
        self.model_label.setWordWrap(True)
        root.addWidget(self.model_label)

        splitter = QSplitter()

        left = QDialog()
        left_layout = QVBoxLayout(left)
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.addWidget(QLabel(_t(self.language, "sources")))

        self.sources_table = QTableWidget()
        self.sources_table.setColumnCount(4)
        self.sources_table.setHorizontalHeaderLabels(
            [
                _t(self.language, "source"),
                _t(self.language, "file"),
                _t(self.language, "fragment"),
                _t(self.language, "path"),
            ]
        )
        self.sources_table.setEditTriggers(QAbstractItemView.NoEditTriggers)
        self.sources_table.setSelectionBehavior(QAbstractItemView.SelectRows)
        self.sources_table.setSelectionMode(QAbstractItemView.SingleSelection)

        header = self.sources_table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.Stretch)
        header.setSectionResizeMode(3, QHeaderView.Stretch)

        left_layout.addWidget(self.sources_table)
        splitter.addWidget(left)

        right = QDialog()
        right_layout = QVBoxLayout(right)
        right_layout.setContentsMargins(0, 0, 0, 0)
        right_layout.addWidget(QLabel(_t(self.language, "preview")))

        self.preview = QPlainTextEdit()
        self.preview.setReadOnly(True)
        right_layout.addWidget(self.preview)
        splitter.addWidget(right)

        splitter.setSizes([780, 620])
        root.addWidget(splitter, 1)

        self.status = QLabel("")
        self.status.setWordWrap(True)
        root.addWidget(self.status)

        self.prepare_button.clicked.connect(self.prepare_context)
        self.copy_button.clicked.connect(self.copy_context)
        self.close_button.clicked.connect(self.reject)
        self.sources_table.cellDoubleClicked.connect(self.open_source)

    def prepare_context(self):
        if self.worker and self.worker.isRunning():
            return

        question = self.question_input.toPlainText().strip()
        if not question:
            self.status.setText(_t(self.language, "empty"))
            return

        self.preparation = None
        self.sources_table.setRowCount(0)
        self.preview.clear()
        self.query_label.setText(_t(self.language, "search_query_empty"))
        self.copy_button.setEnabled(False)
        self.prepare_button.setEnabled(False)
        self.status.setText(_t(self.language, "busy"))

        self.logger.info("AI RETRIEVAL REQUEST | question=%r", question)

        self.worker = AssistantPreparationWorker(question, self)
        self.worker.finished_result.connect(self.context_ready)
        self.worker.failed.connect(self.context_error)
        self.worker.start()

    def context_ready(self, preparation):
        self.preparation = preparation
        self.prepare_button.setEnabled(True)

        query = preparation.search_plan.search_query or "—"
        self.query_label.setText(
            _t(self.language, "search_query", query=query)
        )

        self.sources_table.setRowCount(len(preparation.sources))

        for row, source in enumerate(preparation.sources):
            excerpt = " ".join((source.context or "").split())
            if len(excerpt) > 650:
                excerpt = excerpt[:650] + "..."

            values = [
                f"S{row + 1}",
                source.filename,
                excerpt,
                source.filepath,
            ]

            for column, value in enumerate(values):
                self.sources_table.setItem(
                    row,
                    column,
                    QTableWidgetItem(value or ""),
                )

        preview_text = (
            "SYSTEM\n======\n"
            + preparation.prompt.system
            + "\n\nUSER\n====\n"
            + preparation.prompt.user
        )
        self.preview.setPlainText(preview_text)
        self.copy_button.setEnabled(bool(preview_text))

        source_count = len(preparation.sources)
        if source_count:
            self.status.setText(
                _t(self.language, "ready", count=source_count)
            )
        else:
            self.status.setText(_t(self.language, "none"))

        self.logger.info(
            "AI RETRIEVAL READY | query=%r | sources=%s",
            preparation.search_plan.search_query,
            source_count,
        )

        if self.worker:
            self.worker.deleteLater()
            self.worker = None

    def context_error(self, message):
        self.prepare_button.setEnabled(True)
        self.copy_button.setEnabled(False)
        self.status.setText(
            _t(self.language, "error", message=message)
        )
        self.logger.error("AI RETRIEVAL ERROR | %s", message)

        if self.worker:
            self.worker.deleteLater()
            self.worker = None

    def copy_context(self):
        text = self.preview.toPlainText()
        if not text:
            return

        QApplication.clipboard().setText(text)
        self.status.setText(_t(self.language, "copied"))

    def open_source(self, row, _column):
        item = self.sources_table.item(row, 3)
        if item is None:
            return

        filepath = item.text().strip()
        if filepath and os.path.exists(filepath):
            os.startfile(filepath)

    def reject(self):
        if self.worker and self.worker.isRunning():
            self.status.setText(_t(self.language, "wait"))
            return
        super().reject()
