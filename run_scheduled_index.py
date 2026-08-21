import sys
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QCoreApplication, QSettings

from database.db import create_database
from indexer.indexer import index_folder
from runtime_locks import (
    FileProcessLock,
    GUI_LOCK_PATH,
    WRITER_LOCK_PATH,
)
from scheduler.windows_task import windows_task_enabled
from ui.auto_indexing import (
    KEY_ENABLED,
    KEY_LAST_SUCCESS,
    KEY_NEXT_DUE,
    _as_bool,
    _next_due_after,
    _read_config,
)


BASE_DIR = Path(__file__).resolve().parent
LOG_PATH = BASE_DIR / "data" / "scheduled_index.log"


def _log(message):
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    with LOG_PATH.open("a", encoding="utf-8") as file:
        file.write(f"[{timestamp}] {message}\n")


def _record_success(settings, config):
    now = datetime.now()
    settings.setValue(
        KEY_LAST_SUCCESS,
        now.isoformat(timespec="seconds"),
    )

    next_due = _next_due_after(
        config,
        now,
        initial=False,
    )
    settings.setValue(
        KEY_NEXT_DUE,
        next_due.isoformat(timespec="minutes"),
    )
    settings.sync()


def main():
    app = QCoreApplication(sys.argv)
    settings = QSettings("ZervDiag", "ZervDiag")
    config = _read_config(settings)

    if not _as_bool(settings.value(KEY_ENABLED, False), False):
        _log("SKIP: automatic indexing is disabled")
        return 0

    if not windows_task_enabled(settings):
        _log("SKIP: Windows background scheduling is disabled")
        return 0

    folder = str(settings.value("documentation_folder", "") or "")
    if not folder:
        _log("ERROR: documentation folder is not configured")
        return 2

    if not Path(folder).exists():
        _log(f"ERROR: documentation folder not found: {folder}")
        return 2

    # If the GUI is open, its internal V14 timer owns the schedule and shows
    # progress to the user. The Task Scheduler runner quietly steps aside.
    gui_probe = FileProcessLock(GUI_LOCK_PATH)
    if not gui_probe.acquire():
        _log("SKIP: ZervDiag GUI is running")
        return 0
    gui_probe.release()

    writer_lock = FileProcessLock(WRITER_LOCK_PATH)
    if not writer_lock.acquire():
        _log("SKIP: another indexing/OCR writer is active")
        return 0

    try:
        _log(f"START: {folder}")
        create_database()
        added, updated, skipped, deleted, total = index_folder(folder)
        _record_success(settings, config)
        _log(
            "FINISHED: "
            f"total={total}; added={added}; updated={updated}; "
            f"skipped={skipped}; deleted={deleted}"
        )
        return 0
    except Exception as error:
        _log(f"ERROR: {type(error).__name__}: {error}")
        return 1
    finally:
        writer_lock.release()


if __name__ == "__main__":
    raise SystemExit(main())
