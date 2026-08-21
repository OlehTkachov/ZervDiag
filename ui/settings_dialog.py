from PySide6.QtCore import QTime
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFormLayout,
    QGroupBox,
    QLabel,
    QLineEdit,
    QSpinBox,
    QTimeEdit,
    QVBoxLayout,
)

from assistant.provider_config import (
    PROVIDER_OLLAMA,
    PROVIDER_OPENAI,
    clear_openai_api_key,
    get_ai_provider_config,
    has_openai_api_key,
    save_ai_provider_config,
    store_openai_api_key,
)
from i18n.catalog import SUPPORTED_LANGUAGES, tr
from i18n.settings import get_language, set_language
from ui.auto_indexing import (
    FREQUENCY_DAILY,
    FREQUENCY_INTERVAL,
    FREQUENCY_WEEKLY,
    KEY_ENABLED,
    KEY_FREQUENCY,
    KEY_INTERVAL_DAYS,
    KEY_RUN_OVERDUE,
    KEY_TIME,
    KEY_WEEKDAY,
    _fmt_dt,
    _read_config,
)


AI_TEXT = {
    "ru": {
        "group": "ИИ-помощник",
        "provider": "Провайдер:",
        "ollama": "Локальная модель (Ollama)",
        "openai": "OpenAI API",
        "local_url": "Адрес Ollama:",
        "local_model": "Локальная модель:",
        "openai_model": "Модель OpenAI:",
        "api_key": "API-ключ OpenAI:",
        "key_saved": "Ключ сохранён и зашифрован Windows DPAPI.",
        "key_missing": "Ключ пока не сохранён.",
        "key_placeholder_saved": "Оставьте пустым, чтобы сохранить текущий ключ",
        "key_placeholder_new": "Вставьте ключ один раз — он не будет показан снова",
        "delete_key": "Удалить сохранённый API-ключ",
        "note": "Выбор провайдера не влияет на локальный поиск. ZervDiag сначала подбирает источники из SQLite и только затем передаёт подготовленный контекст выбранной модели.",
    },
    "uk": {
        "group": "ШІ-помічник",
        "provider": "Провайдер:",
        "ollama": "Локальна модель (Ollama)",
        "openai": "OpenAI API",
        "local_url": "Адреса Ollama:",
        "local_model": "Локальна модель:",
        "openai_model": "Модель OpenAI:",
        "api_key": "API-ключ OpenAI:",
        "key_saved": "Ключ збережено та зашифровано Windows DPAPI.",
        "key_missing": "Ключ ще не збережено.",
        "key_placeholder_saved": "Залиште порожнім, щоб зберегти поточний ключ",
        "key_placeholder_new": "Вставте ключ один раз — він більше не показуватиметься",
        "delete_key": "Видалити збережений API-ключ",
        "note": "Вибір провайдера не впливає на локальний пошук. ZervDiag спочатку підбирає джерела з SQLite і лише потім передає підготовлений контекст вибраній моделі.",
    },
    "en": {
        "group": "AI assistant",
        "provider": "Provider:",
        "ollama": "Local model (Ollama)",
        "openai": "OpenAI API",
        "local_url": "Ollama address:",
        "local_model": "Local model:",
        "openai_model": "OpenAI model:",
        "api_key": "OpenAI API key:",
        "key_saved": "The key is stored and encrypted with Windows DPAPI.",
        "key_missing": "No key is stored yet.",
        "key_placeholder_saved": "Leave empty to keep the stored key",
        "key_placeholder_new": "Paste the key once — it will not be shown again",
        "delete_key": "Delete the stored API key",
        "note": "Provider selection does not change local retrieval. ZervDiag first selects sources from SQLite and only then sends the prepared context to the selected model.",
    },
}


def _ai_t(language, key):
    language = language if language in AI_TEXT else "ru"
    return AI_TEXT[language].get(key, key)


class SettingsDialog(QDialog):
    """Unified settings dialog: language, indexing and AI provider."""

    def __init__(self, settings, parent=None):
        super().__init__(parent)
        self.settings = settings
        self.language = get_language(settings)
        self.config = _read_config(settings)
        self.ai_config = get_ai_provider_config(settings)
        self._openai_key_was_saved = has_openai_api_key(settings)

        self.setWindowTitle(tr("settings.title", self.language))
        self.resize(720, 700)

        root = QVBoxLayout(self)

        language_group = QGroupBox(tr("settings.language", self.language))
        language_form = QFormLayout(language_group)

        self.language_box = QComboBox()
        for code, name in SUPPORTED_LANGUAGES.items():
            self.language_box.addItem(name, code)

        language_index = self.language_box.findData(self.language)
        self.language_box.setCurrentIndex(language_index if language_index >= 0 else 0)
        language_form.addRow(tr("settings.language", self.language) + ":", self.language_box)
        root.addWidget(language_group)

        ai_group = QGroupBox(_ai_t(self.language, "group"))
        ai_form = QFormLayout(ai_group)

        self.ai_provider = QComboBox()
        self.ai_provider.addItem(_ai_t(self.language, "ollama"), PROVIDER_OLLAMA)
        self.ai_provider.addItem(_ai_t(self.language, "openai"), PROVIDER_OPENAI)
        provider_index = self.ai_provider.findData(self.ai_config.provider)
        self.ai_provider.setCurrentIndex(provider_index if provider_index >= 0 else 0)
        ai_form.addRow(_ai_t(self.language, "provider"), self.ai_provider)

        self.ollama_url = QLineEdit(self.ai_config.ollama_url)
        ai_form.addRow(_ai_t(self.language, "local_url"), self.ollama_url)

        self.ollama_model = QLineEdit(self.ai_config.ollama_model)
        self.ollama_model.setPlaceholderText("model name")
        ai_form.addRow(_ai_t(self.language, "local_model"), self.ollama_model)

        self.openai_model = QLineEdit(self.ai_config.openai_model)
        self.openai_model.setPlaceholderText("model name")
        ai_form.addRow(_ai_t(self.language, "openai_model"), self.openai_model)

        self.openai_key = QLineEdit()
        self.openai_key.setEchoMode(QLineEdit.Password)
        self.openai_key.setPlaceholderText(
            _ai_t(
                self.language,
                "key_placeholder_saved" if self._openai_key_was_saved else "key_placeholder_new",
            )
        )
        ai_form.addRow(_ai_t(self.language, "api_key"), self.openai_key)

        self.openai_key_status = QLabel(
            _ai_t(
                self.language,
                "key_saved" if self._openai_key_was_saved else "key_missing",
            )
        )
        self.openai_key_status.setWordWrap(True)
        ai_form.addRow("", self.openai_key_status)

        self.delete_openai_key = QCheckBox(_ai_t(self.language, "delete_key"))
        self.delete_openai_key.setChecked(False)
        self.delete_openai_key.setEnabled(self._openai_key_was_saved)
        ai_form.addRow(self.delete_openai_key)

        ai_note = QLabel(_ai_t(self.language, "note"))
        ai_note.setWordWrap(True)
        ai_form.addRow(ai_note)

        root.addWidget(ai_group)

        auto_group = QGroupBox(tr("settings.auto_group", self.language))
        form = QFormLayout(auto_group)

        self.enabled = QCheckBox(tr("settings.auto_enable", self.language))
        self.enabled.setChecked(self.config["enabled"])
        form.addRow(self.enabled)

        self.frequency = QComboBox()
        self.frequency.addItem(tr("settings.daily", self.language), FREQUENCY_DAILY)
        self.frequency.addItem(tr("settings.weekly", self.language), FREQUENCY_WEEKLY)
        self.frequency.addItem(tr("settings.interval", self.language), FREQUENCY_INTERVAL)
        frequency_index = self.frequency.findData(self.config["frequency"])
        self.frequency.setCurrentIndex(frequency_index if frequency_index >= 0 else 0)
        form.addRow(tr("settings.frequency", self.language), self.frequency)

        self.weekday = QComboBox()
        for number in range(7):
            self.weekday.addItem(tr(f"weekday.{number}", self.language), number)
        self.weekday.setCurrentIndex(max(0, min(6, self.config["weekday"])))
        form.addRow(tr("settings.weekday", self.language), self.weekday)

        self.interval_days = QSpinBox()
        self.interval_days.setRange(1, 365)
        self.interval_days.setValue(self.config["interval_days"])
        form.addRow(tr("settings.interval_days", self.language), self.interval_days)

        self.run_time = QTimeEdit()
        self.run_time.setDisplayFormat("HH:mm")
        parsed = QTime.fromString(str(self.config["time"]), "HH:mm")
        self.run_time.setTime(parsed if parsed.isValid() else QTime(18, 0))
        form.addRow(tr("settings.time", self.language), self.run_time)

        self.run_overdue = QCheckBox(tr("settings.run_overdue", self.language))
        self.run_overdue.setChecked(self.config["run_overdue"])
        form.addRow(self.run_overdue)

        self.last_label = QLabel(_fmt_dt(self.config["last_success"]))
        self.next_label = QLabel(_fmt_dt(self.config["next_due"]))
        form.addRow(tr("settings.last_success", self.language), self.last_label)
        form.addRow(tr("settings.next_due", self.language), self.next_label)

        root.addWidget(auto_group)

        note = QLabel(tr("settings.note", self.language))
        note.setWordWrap(True)
        root.addWidget(note)

        self.buttons = QDialogButtonBox(
            QDialogButtonBox.Save | QDialogButtonBox.Cancel
        )
        self.buttons.button(QDialogButtonBox.Save).setText(
            tr("common.save", self.language)
        )
        self.buttons.button(QDialogButtonBox.Cancel).setText(
            tr("common.cancel", self.language)
        )
        self.buttons.accepted.connect(self.accept)
        self.buttons.rejected.connect(self.reject)
        root.addWidget(self.buttons)

        self.frequency.currentIndexChanged.connect(self._update_controls)
        self.enabled.toggled.connect(self._update_controls)
        self.ai_provider.currentIndexChanged.connect(self._update_controls)
        self.delete_openai_key.toggled.connect(self._update_controls)
        self._update_controls()

    def _update_controls(self):
        enabled = self.enabled.isChecked()
        mode = self.frequency.currentData()

        self.frequency.setEnabled(enabled)
        self.run_time.setEnabled(enabled)
        self.run_overdue.setEnabled(enabled)
        self.weekday.setEnabled(enabled and mode == FREQUENCY_WEEKLY)
        self.interval_days.setEnabled(enabled and mode == FREQUENCY_INTERVAL)

        provider = self.ai_provider.currentData()
        is_local = provider == PROVIDER_OLLAMA
        is_openai = provider == PROVIDER_OPENAI

        self.ollama_url.setEnabled(is_local)
        self.ollama_model.setEnabled(is_local)
        self.openai_model.setEnabled(is_openai)
        self.openai_key.setEnabled(is_openai and not self.delete_openai_key.isChecked())
        self.openai_key_status.setEnabled(is_openai)
        self.delete_openai_key.setEnabled(is_openai and self._openai_key_was_saved)

    def save(self):
        language = set_language(self.settings, self.language_box.currentData())

        save_ai_provider_config(
            self.settings,
            provider=self.ai_provider.currentData(),
            ollama_url=self.ollama_url.text(),
            ollama_model=self.ollama_model.text(),
            openai_model=self.openai_model.text(),
        )

        if self.delete_openai_key.isChecked():
            clear_openai_api_key(self.settings)
        else:
            new_key = self.openai_key.text().strip()
            if new_key:
                store_openai_api_key(self.settings, new_key)

        self.settings.setValue(KEY_ENABLED, self.enabled.isChecked())
        self.settings.setValue(KEY_FREQUENCY, self.frequency.currentData())
        self.settings.setValue(KEY_WEEKDAY, self.weekday.currentData())
        self.settings.setValue(KEY_INTERVAL_DAYS, self.interval_days.value())
        self.settings.setValue(KEY_TIME, self.run_time.time().toString("HH:mm"))
        self.settings.setValue(KEY_RUN_OVERDUE, self.run_overdue.isChecked())
        self.settings.sync()

        return language
