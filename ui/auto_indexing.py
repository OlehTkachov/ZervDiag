import os
from datetime import datetime, timedelta
from types import MethodType

from PySide6.QtCore import QTime, QTimer
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

KEY_ENABLED = "auto_index/enabled"
KEY_FREQUENCY = "auto_index/frequency"
KEY_WEEKDAY = "auto_index/weekday"
KEY_INTERVAL_DAYS = "auto_index/interval_days"
KEY_TIME = "auto_index/time"
KEY_RUN_OVERDUE = "auto_index/run_overdue_at_startup"
KEY_LAST_SUCCESS = "auto_index/last_success"
KEY_NEXT_DUE = "auto_index/next_due"

FREQUENCY_DAILY = "daily"
FREQUENCY_WEEKLY = "weekly"
FREQUENCY_INTERVAL = "interval"

WEEKDAYS = [
    "Понедельник",
    "Вторник",
    "Среда",
    "Четверг",
    "Пятница",
    "Суббота",
    "Воскресенье",
]


def _as_bool(value, default=False):
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().casefold() in {"1", "true", "yes", "on"}


def _parse_dt(value):
    value = (value or "").strip()
    if not value:
        return None
    try:
        return datetime.fromisoformat(value)
    except ValueError:
        return None


def _fmt_dt(value):
    dt = _parse_dt(value) if isinstance(value, str) else value
    if not dt:
        return "—"
    return dt.strftime("%d.%m.%Y %H:%M")


def _read_config(settings):
    return {
        "enabled": _as_bool(settings.value(KEY_ENABLED, False), False),
        "frequency": settings.value(KEY_FREQUENCY, FREQUENCY_DAILY),
        "weekday": int(settings.value(KEY_WEEKDAY, 0) or 0),
        "interval_days": max(1, int(settings.value(KEY_INTERVAL_DAYS, 7) or 7)),
        "time": settings.value(KEY_TIME, "18:00"),
        "run_overdue": _as_bool(settings.value(KEY_RUN_OVERDUE, True), True),
        "last_success": settings.value(KEY_LAST_SUCCESS, ""),
        "next_due": settings.value(KEY_NEXT_DUE, ""),
    }


def _scheduled_time(config):
    try:
        hour_text, minute_text = str(config["time"]).split(":", 1)
        hour = max(0, min(23, int(hour_text)))
        minute = max(0, min(59, int(minute_text)))
        return hour, minute
    except Exception:
        return 18, 0


def _next_due_after(config, base, *, initial=False):
    hour, minute = _scheduled_time(config)
    frequency = config["frequency"]

    if frequency == FREQUENCY_WEEKLY:
        weekday = max(0, min(6, int(config["weekday"])))
        days_ahead = (weekday - base.weekday()) % 7
        candidate = datetime.combine(
            (base + timedelta(days=days_ahead)).date(),
            datetime.min.time(),
        ).replace(hour=hour, minute=minute)

        if candidate <= base or (not initial and days_ahead == 0):
            candidate += timedelta(days=7)
        return candidate

    if frequency == FREQUENCY_INTERVAL:
        days = max(1, int(config["interval_days"]))
        if initial:
            candidate = base.replace(
                hour=hour,
                minute=minute,
                second=0,
                microsecond=0,
            )
            if candidate <= base:
                candidate += timedelta(days=days)
            return candidate

        target_date = (base + timedelta(days=days)).date()
        return datetime.combine(
            target_date,
            datetime.min.time(),
        ).replace(hour=hour, minute=minute)

    if initial:
        candidate = base.replace(
            hour=hour,
            minute=minute,
            second=0,
            microsecond=0,
        )
        if candidate <= base:
            candidate += timedelta(days=1)
        return candidate

    target_date = (base + timedelta(days=1)).date()
    return datetime.combine(
        target_date,
        datetime.min.time(),
    ).replace(hour=hour, minute=minute)


class AutoIndexSettingsDialog(QDialog):
    def __init__(self, settings, parent=None):
        super().__init__(parent)
        self.settings = settings
        self.setWindowTitle("Настройки ZervDiag")
        self.resize(560, 360)

        config = _read_config(settings)

        root = QVBoxLayout(self)

        group = QGroupBox("Автоматическая индексация")
        form = QFormLayout(group)

        self.enabled = QCheckBox("Включить автоматическую индексацию")
        self.enabled.setChecked(config["enabled"])
        form.addRow(self.enabled)

        self.frequency = QComboBox()
        self.frequency.addItem("Каждый день", FREQUENCY_DAILY)
        self.frequency.addItem("Раз в неделю", FREQUENCY_WEEKLY)
        self.frequency.addItem("Каждые N дней", FREQUENCY_INTERVAL)
        index = self.frequency.findData(config["frequency"])
        self.frequency.setCurrentIndex(index if index >= 0 else 0)
        form.addRow("Периодичность:", self.frequency)

        self.weekday = QComboBox()
        for number, name in enumerate(WEEKDAYS):
            self.weekday.addItem(name, number)
        self.weekday.setCurrentIndex(max(0, min(6, config["weekday"])))
        form.addRow("День недели:", self.weekday)

        self.interval_days = QSpinBox()
        self.interval_days.setRange(1, 365)
        self.interval_days.setValue(config["interval_days"])
        form.addRow("Интервал, дней:", self.interval_days)

        self.run_time = QTimeEdit()
        self.run_time.setDisplayFormat("HH:mm")
        parsed = QTime.fromString(str(config["time"]), "HH:mm")
        self.run_time.setTime(parsed if parsed.isValid() else QTime(18, 0))
        form.addRow("Время:", self.run_time)

        self.run_overdue = QCheckBox(
            "Если компьютер был выключен — выполнить пропущенную индексацию при запуске ZervDiag"
        )
        self.run_overdue.setChecked(config["run_overdue"])
        self.run_overdue.setWordWrap(True)
        form.addRow(self.run_overdue)

        self.last_label = QLabel(_fmt_dt(config["last_success"]))
        self.next_label = QLabel(_fmt_dt(config["next_due"]))
        form.addRow("Последняя успешная:", self.last_label)
        form.addRow("Следующая:", self.next_label)

        root.addWidget(group)

        note = QLabel(
            "Автоиндексация запускает только быструю индексацию. "
            "Если в этот момент идёт OCR или другая индексация, запуск будет отложен."
        )
        note.setWordWrap(True)
        root.addWidget(note)

        buttons = QDialogButtonBox(
            QDialogButtonBox.Save | QDialogButtonBox.Cancel
        )
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        root.addWidget(buttons)

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
        self.settings.setValue(KEY_ENABLED, self.enabled.isChecked())
        self.settings.setValue(KEY_FREQUENCY, self.frequency.currentData())
        self.settings.setValue(KEY_WEEKDAY, self.weekday.currentData())
        self.settings.setValue(KEY_INTERVAL_DAYS, self.interval_days.value())
        self.settings.setValue(KEY_TIME, self.run_time.time().toString("HH:mm"))
        self.settings.setValue(KEY_RUN_OVERDUE, self.run_overdue.isChecked())


class AutoIndexController:
    def __init__(self, main_window):
        self.main_window = main_window
        self.settings = main_window.settings
        self._startup_handled = False

        self.timer = QTimer(main_window)
        self.timer.setInterval(60_000)
        self.timer.timeout.connect(self.check_due)
        self.timer.start()

        QTimer.singleShot(1500, self.handle_startup)

    def config(self):
        return _read_config(self.settings)

    def reschedule_from_now(self):
        config = self.config()

        if not config["enabled"]:
            self.settings.setValue(KEY_NEXT_DUE, "")
            return

        next_due = _next_due_after(config, datetime.now(), initial=True)
        self.settings.setValue(KEY_NEXT_DUE, next_due.isoformat(timespec="minutes"))

    def record_success(self):
        now = datetime.now()
        self.settings.setValue(KEY_LAST_SUCCESS, now.isoformat(timespec="seconds"))

        config = self.config()
        if config["enabled"]:
            next_due = _next_due_after(config, now, initial=False)
            self.settings.setValue(
                KEY_NEXT_DUE,
                next_due.isoformat(timespec="minutes"),
            )
        else:
            self.settings.setValue(KEY_NEXT_DUE, "")

    def handle_startup(self):
        if self._startup_handled:
            return
        self._startup_handled = True

        config = self.config()
        if not config["enabled"]:
            return

        next_due = _parse_dt(config["next_due"])
        if not next_due:
            self.reschedule_from_now()
            return

        now = datetime.now()
        if next_due > now:
            return

        if config["run_overdue"]:
            self.check_due()
            return

        future = next_due
        guard = 0
        while future <= now and guard < 400:
            future = _next_due_after(config, future, initial=False)
            guard += 1

        self.settings.setValue(
            KEY_NEXT_DUE,
            future.isoformat(timespec="minutes"),
        )

    def check_due(self):
        config = self.config()
        if not config["enabled"]:
            return

        due = _parse_dt(config["next_due"])
        if not due:
            self.reschedule_from_now()
            return

        if datetime.now() < due:
            return

        index_worker = getattr(self.main_window, "index_worker", None)
        if index_worker and index_worker.isRunning():
            return

        ocr_worker = getattr(self.main_window, "ocr_worker", None)
        if ocr_worker and ocr_worker.isRunning():
            return

        folder = self.settings.value("documentation_folder", "")
        if not folder or not os.path.exists(folder):
            return

        self.main_window.status.setText(
            "Автоматическая индексация: запуск по расписанию..."
        )
        self.main_window.start_indexing()


def install_auto_index_settings(main_window):
    if getattr(main_window, "_v14_auto_index_installed", False):
        return

    main_window._v14_auto_index_installed = True
    controller = AutoIndexController(main_window)
    main_window._v14_auto_index_controller = controller

    original_indexing_finished = main_window.indexing_finished

    def enhanced_indexing_finished(
        self,
        added,
        updated,
        skipped,
        deleted,
        total,
        stopped,
    ):
        original_indexing_finished(
            added,
            updated,
            skipped,
            deleted,
            total,
            stopped,
        )
        if not stopped:
            controller.record_success()

    main_window.indexing_finished = MethodType(
        enhanced_indexing_finished,
        main_window,
    )

    def open_settings():
        dialog = AutoIndexSettingsDialog(main_window.settings, main_window)
        if dialog.exec() != QDialog.Accepted:
            return

        dialog.save()
        controller.reschedule_from_now()

        config = controller.config()
        if config["enabled"]:
            main_window.status.setText(
                "Автоматическая индексация включена. "
                f"Следующая: {_fmt_dt(config['next_due'])}"
            )
        else:
            main_window.status.setText("Автоматическая индексация выключена.")

    main_window.btn_settings.clicked.connect(open_settings)
