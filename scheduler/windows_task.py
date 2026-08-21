import os
import subprocess
import sys
from pathlib import Path
from types import MethodType

from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication, QCheckBox, QLabel

from i18n.settings import get_language
from runtime_locks import (
    FileProcessLock,
    GUI_LOCK_PATH,
    WRITER_LOCK_PATH,
)
from ui.auto_indexing import (
    FREQUENCY_DAILY,
    FREQUENCY_INTERVAL,
    FREQUENCY_WEEKLY,
    _read_config,
)
from ui.settings_dialog import SettingsDialog


TASK_NAME = "ZervDiag Automatic Index"
KEY_WINDOWS_TASK_ENABLED = "auto_index/windows_task_enabled"
KEY_WINDOWS_TASK_LAST_ERROR = "auto_index/windows_task_last_error"

PROJECT_ROOT = Path(__file__).resolve().parent.parent
SCHEDULED_SCRIPT = PROJECT_ROOT / "run_scheduled_index.py"

WEEKDAY_CODES = ["MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN"]

TEXT = {
    "ru": {
        "checkbox": "Запускать по расписанию, даже если ZervDiag закрыт",
        "created": "Планировщик Windows: задача создана",
        "missing": "Планировщик Windows: задача пока не создана",
        "disabled": "Фоновый запуск Windows выключен",
        "busy": "Индексация/OCR уже выполняется в другом процессе.",
    },
    "uk": {
        "checkbox": "Запускати за розкладом, навіть якщо ZervDiag закрито",
        "created": "Планувальник Windows: завдання створено",
        "missing": "Планувальник Windows: завдання ще не створено",
        "disabled": "Фоновий запуск Windows вимкнено",
        "busy": "Індексація/OCR уже виконується в іншому процесі.",
    },
    "en": {
        "checkbox": "Run on schedule even when ZervDiag is closed",
        "created": "Windows Task Scheduler: task is registered",
        "missing": "Windows Task Scheduler: task is not registered yet",
        "disabled": "Windows background scheduling is disabled",
        "busy": "Indexing/OCR is already running in another process.",
    },
}


def _text(language, key):
    return TEXT.get(language, TEXT["ru"]).get(key, key)


def _as_bool(value, default=True):
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().casefold() in {"1", "true", "yes", "on"}


def windows_task_enabled(settings):
    return _as_bool(
        settings.value(KEY_WINDOWS_TASK_ENABLED, True),
        True,
    )


def _python_for_task():
    executable = Path(sys.executable).resolve()

    if os.name == "nt":
        pythonw = executable.with_name("pythonw.exe")
        if pythonw.exists():
            return pythonw

    return executable


def _task_command():
    python_exe = _python_for_task()
    return f'"{python_exe}" "{SCHEDULED_SCRIPT}"'


def _run_schtasks(arguments):
    if os.name != "nt":
        return 1, "Windows Task Scheduler is available only on Windows."

    try:
        result = subprocess.run(
            ["schtasks.exe", *arguments],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            timeout=20,
            check=False,
        )
    except Exception as error:
        return 1, str(error)

    raw = result.stdout or result.stderr or b""
    message = ""

    for encoding in ("utf-8", "cp866", "cp1251"):
        try:
            message = raw.decode(encoding).strip()
            break
        except UnicodeDecodeError:
            continue

    if not message:
        message = f"schtasks exit code {result.returncode}"

    return result.returncode, message


def task_exists():
    code, _message = _run_schtasks(
        ["/Query", "/TN", TASK_NAME]
    )
    return code == 0


def delete_windows_task():
    if not task_exists():
        return True, "Task already absent"

    code, message = _run_schtasks(
        ["/Delete", "/TN", TASK_NAME, "/F"]
    )
    return code == 0, message


def _schedule_arguments(config):
    time_value = str(config.get("time") or "18:00")
    frequency = config.get("frequency") or FREQUENCY_DAILY

    arguments = [
        "/Create",
        "/TN",
        TASK_NAME,
        "/TR",
        _task_command(),
        "/ST",
        time_value,
        "/RL",
        "LIMITED",
        "/F",
    ]

    if frequency == FREQUENCY_WEEKLY:
        weekday = max(0, min(6, int(config.get("weekday", 0) or 0)))
        arguments.extend(
            ["/SC", "WEEKLY", "/MO", "1", "/D", WEEKDAY_CODES[weekday]]
        )
    elif frequency == FREQUENCY_INTERVAL:
        days = max(1, int(config.get("interval_days", 7) or 7))
        arguments.extend(["/SC", "DAILY", "/MO", str(days)])
    else:
        arguments.extend(["/SC", "DAILY", "/MO", "1"])

    return arguments


def sync_windows_task(settings):
    config = _read_config(settings)

    if not config["enabled"] or not windows_task_enabled(settings):
        ok, message = delete_windows_task()
    else:
        code, message = _run_schtasks(_schedule_arguments(config))
        ok = code == 0

    settings.setValue(
        KEY_WINDOWS_TASK_LAST_ERROR,
        "" if ok else message,
    )
    settings.sync()
    return ok, message


def _patch_settings_dialog():
    if getattr(SettingsDialog, "_v14_windows_task_patched", False):
        return

    SettingsDialog._v14_windows_task_patched = True

    original_init = SettingsDialog.__init__
    original_update_controls = SettingsDialog._update_controls
    original_save = SettingsDialog.save

    def patched_init(self, settings, parent=None):
        original_init(self, settings, parent)

        language = get_language(settings)
        self.windows_task_checkbox = QCheckBox(
            _text(language, "checkbox")
        )
        self.windows_task_checkbox.setChecked(
            windows_task_enabled(settings)
        )

        if not self.config["enabled"]:
            state_text = _text(language, "disabled")
        elif task_exists():
            state_text = _text(language, "created")
        else:
            state_text = _text(language, "missing")

        self.windows_task_status = QLabel(state_text)
        self.windows_task_status.setWordWrap(True)

        layout = self.layout()
        insert_at = max(0, layout.count() - 2)
        layout.insertWidget(insert_at, self.windows_task_checkbox)
        layout.insertWidget(insert_at + 1, self.windows_task_status)

        self._update_controls()

    def patched_update_controls(self):
        original_update_controls(self)
        if hasattr(self, "windows_task_checkbox"):
            self.windows_task_checkbox.setEnabled(
                self.enabled.isChecked()
            )

    def patched_save(self):
        if hasattr(self, "windows_task_checkbox"):
            self.settings.setValue(
                KEY_WINDOWS_TASK_ENABLED,
                self.windows_task_checkbox.isChecked(),
            )

        language = original_save(self)
        sync_windows_task(self.settings)
        return language

    SettingsDialog.__init__ = patched_init
    SettingsDialog._update_controls = patched_update_controls
    SettingsDialog.save = patched_save


def _install_writer_locks(main_window):
    if getattr(main_window, "_v14_writer_locks_installed", False):
        return

    main_window._v14_writer_locks_installed = True

    original_start_indexing = main_window.start_indexing
    original_start_ocr = main_window.start_ocr

    def start_indexing_locked(self):
        lock = FileProcessLock(WRITER_LOCK_PATH)
        if not lock.acquire():
            language = get_language(self.settings)
            self.status.setText(_text(language, "busy"))
            return

        previous_worker = self.index_worker
        original_start_indexing()
        worker = self.index_worker

        if worker is None or worker is previous_worker:
            lock.release()
            return

        self._v14_active_writer_lock = lock

        def release_lock(*_args):
            lock.release()
            if getattr(self, "_v14_active_writer_lock", None) is lock:
                self._v14_active_writer_lock = None

        worker.finished_index.connect(release_lock)
        worker.error.connect(release_lock)

    def start_ocr_locked(self):
        lock = FileProcessLock(WRITER_LOCK_PATH)
        if not lock.acquire():
            language = get_language(self.settings)
            self.status.setText(_text(language, "busy"))
            return

        previous_worker = self.ocr_worker
        original_start_ocr()
        worker = self.ocr_worker

        if worker is None or worker is previous_worker:
            lock.release()
            return

        self._v14_active_writer_lock = lock

        def release_lock(*_args):
            lock.release()
            if getattr(self, "_v14_active_writer_lock", None) is lock:
                self._v14_active_writer_lock = None

        worker.finished_ocr.connect(release_lock)
        worker.error.connect(release_lock)

    main_window.start_indexing = MethodType(start_indexing_locked, main_window)
    main_window.start_ocr = MethodType(start_ocr_locked, main_window)


def install_windows_scheduler(main_window):
    if getattr(main_window, "_v14_windows_scheduler_installed", False):
        return

    main_window._v14_windows_scheduler_installed = True

    _patch_settings_dialog()
    _install_writer_locks(main_window)

    gui_lock = FileProcessLock(GUI_LOCK_PATH)
    if gui_lock.acquire():
        main_window._v14_gui_process_lock = gui_lock

        app = QApplication.instance()
        if app is not None:
            app.aboutToQuit.connect(gui_lock.release)

    main_window._v14_sync_windows_task = lambda: sync_windows_task(
        main_window.settings
    )

    # Existing users may already have auto-indexing enabled before this
    # V14 block is installed. Create/update the Windows task once at startup.
    QTimer.singleShot(
        1800,
        lambda: sync_windows_task(main_window.settings),
    )
