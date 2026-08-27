import shutil
import sqlite3
import subprocess
from dataclasses import dataclass
from pathlib import Path

from PySide6.QtCore import QSettings, QTimer
from PySide6.QtWidgets import QDialog, QDialogButtonBox, QLabel, QVBoxLayout

from app_paths import DB_PATH, is_frozen
from readers.document_reader import find_libreoffice
from readers.ocr_reader import TESSERACT


SEEN_KEY = "beta/component_check_seen_v1"


@dataclass(frozen=True)
class ComponentState:
    label: str
    ok: bool
    details: str
    optional: bool = False


def _check_database():
    if not DB_PATH.exists():
        return ComponentState("База данных", False, "не найдена")

    try:
        uri = DB_PATH.resolve().as_uri() + "?mode=ro"
        conn = sqlite3.connect(uri, uri=True, timeout=10)
        try:
            result = conn.execute("PRAGMA quick_check").fetchone()
        finally:
            conn.close()

        quick = str(result[0] if result else "").strip()
        return ComponentState(
            "База данных",
            quick.casefold() == "ok",
            f"SQLite QUICK_CHECK: {quick or 'нет результата'}",
        )
    except Exception as error:
        return ComponentState(
            "База данных",
            False,
            f"{type(error).__name__}: {error}",
        )


def _check_tesseract():
    executable = Path(TESSERACT)
    if not executable.exists():
        return ComponentState(
            "Tesseract OCR",
            False,
            "не установлен — распознавание сканов недоступно",
        )

    languages = set()
    try:
        result = subprocess.run(
            [str(executable), "--list-langs"],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=10,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        output = result.stdout.decode("utf-8", errors="replace")
        languages = {
            line.strip().casefold()
            for line in output.splitlines()
            if line.strip() and "list of available" not in line.casefold()
        }
    except Exception as error:
        return ComponentState(
            "Tesseract OCR",
            False,
            f"установлен, но проверка не выполнена: {error}",
        )

    missing = [code for code in ("rus", "eng") if code not in languages]
    if missing:
        return ComponentState(
            "Tesseract OCR",
            False,
            "нет языков: " + ", ".join(missing),
        )

    return ComponentState(
        "Tesseract OCR",
        True,
        "русский + английский доступны",
    )


def _check_libreoffice():
    path = find_libreoffice()
    if path:
        return ComponentState(
            "LibreOffice",
            True,
            "старые DOC/XLS поддерживаются",
        )

    return ComponentState(
        "LibreOffice",
        False,
        "не установлен — старые DOC/XLS будут недоступны",
        optional=True,
    )


def _find_ollama():
    path = shutil.which("ollama")
    if path:
        return path

    local = Path.home() / "AppData" / "Local" / "Programs" / "Ollama" / "ollama.exe"
    return str(local) if local.exists() else None


def _check_ollama():
    path = _find_ollama()
    if path:
        return ComponentState(
            "Ollama",
            True,
            "локальный ИИ доступен",
            optional=True,
        )

    return ComponentState(
        "Ollama",
        False,
        "не установлен — это необязательный компонент",
        optional=True,
    )


def collect_component_states():
    return (
        _check_database(),
        _check_tesseract(),
        _check_libreoffice(),
        _check_ollama(),
    )


class ComponentStatusDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("ZervDiag Beta — проверка компонентов")
        self.resize(680, 330)

        layout = QVBoxLayout(self)
        title = QLabel("Готовность компонентов ZervDiag")
        title.setStyleSheet("font-size: 17px; font-weight: bold; padding: 6px;")
        layout.addWidget(title)

        for state in collect_component_states():
            mark = "✓" if state.ok else ("○" if state.optional else "✗")
            label = QLabel(f"{mark}  {state.label}: {state.details}")
            label.setWordWrap(True)
            layout.addWidget(label)

        note = QLabel(
            "Отсутствие необязательных компонентов не мешает обычному поиску. "
            "Если Tesseract не установлен, ZervDiag продолжит работать, но OCR "
            "сканов будет недоступен."
        )
        note.setWordWrap(True)
        note.setStyleSheet("padding-top: 10px;")
        layout.addWidget(note)
        layout.addStretch()

        buttons = QDialogButtonBox(QDialogButtonBox.Close)
        buttons.button(QDialogButtonBox.Close).setText("Закрыть")
        buttons.rejected.connect(self.reject)
        buttons.accepted.connect(self.accept)
        layout.addWidget(buttons)


def show_component_status(parent=None):
    dialog = ComponentStatusDialog(parent)
    dialog.exec()


def install_beta_component_check(main_window):
    if not is_frozen():
        return

    settings = QSettings("ZervDiag", "ZervDiag")
    if str(settings.value(SEEN_KEY, "0")) == "1":
        return

    def show_once():
        show_component_status(main_window)
        settings.setValue(SEEN_KEY, "1")
        settings.sync()

    QTimer.singleShot(900, show_once)
