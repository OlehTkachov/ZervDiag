from PySide6.QtCore import QTime
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QFormLayout,
    QGroupBox,
    QLabel,
    QSpinBox,
    QTimeEdit,
    QVBoxLayout,
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


class SettingsDialog(QDialog):
    """Unified V14 settings dialog with interface language and auto-indexing."""

    def __init__(self, settings, parent=None):
        super().__init__(parent)
        self.settings = settings
        self.language = get_language(settings)
        self.config = _read_config(settings)

        self.setWindowTitle(tr("settings.title", self.language))
        self.resize(620, 430)

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
        self._update_controls()

    def _update_controls(self):
        enabled = self.enabled.isChecked()
        mode = self.frequency.currentData()

        self.frequency.setEnabled(enabled)
        self.run_time.setEnabled(enabled)
        self.run_overdue.setEnabled(enabled)
        self.weekday.setEnabled(enabled and mode == FREQUENCY_WEEKLY)
        self.interval_days.setEnabled(enabled and mode == FREQUENCY_INTERVAL)

    def save(self):
        language = set_language(self.settings, self.language_box.currentData())

        self.settings.setValue(KEY_ENABLED, self.enabled.isChecked())
        self.settings.setValue(KEY_FREQUENCY, self.frequency.currentData())
        self.settings.setValue(KEY_WEEKDAY, self.weekday.currentData())
        self.settings.setValue(KEY_INTERVAL_DAYS, self.interval_days.value())
        self.settings.setValue(KEY_TIME, self.run_time.time().toString("HH:mm"))
        self.settings.setValue(KEY_RUN_OVERDUE, self.run_overdue.isChecked())

        return language
