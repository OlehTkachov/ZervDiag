import os
import sqlite3
from pathlib import Path

from PySide6.QtWidgets import QFileDialog, QMessageBox

from app_paths import DB_PATH, ensure_runtime_dirs, is_frozen
from database.db import create_database


def _quick_check(path):
    path = Path(path)
    if not path.exists() or not path.is_file():
        return False, "Файл базы не найден."

    try:
        uri = path.resolve().as_uri() + "?mode=ro"
        conn = sqlite3.connect(uri, uri=True, timeout=30)
        try:
            result = conn.execute("PRAGMA quick_check").fetchone()
            quick = str(result[0] if result else "").strip().casefold()
            table = conn.execute(
                "SELECT 1 FROM sqlite_master "
                "WHERE type='table' AND name='files'"
            ).fetchone()
        finally:
            conn.close()

        if quick != "ok":
            return False, f"SQLite QUICK_CHECK: {quick or 'нет результата'}"

        if not table:
            return False, "В базе отсутствует таблица files."

        return True, "ok"

    except Exception as error:
        return False, f"{type(error).__name__}: {error}"


def import_database(source_path):
    source = Path(source_path).resolve()
    destination = DB_PATH.resolve()

    ok, message = _quick_check(source)
    if not ok:
        raise RuntimeError(f"Исходная база не прошла проверку: {message}")

    ensure_runtime_dirs()

    if source == destination:
        return

    temp_path = destination.with_suffix(".importing.db")

    for candidate in (
        temp_path,
        Path(str(temp_path) + "-wal"),
        Path(str(temp_path) + "-shm"),
    ):
        try:
            candidate.unlink()
        except FileNotFoundError:
            pass

    source_uri = source.as_uri() + "?mode=ro"
    source_conn = sqlite3.connect(source_uri, uri=True, timeout=30)
    target_conn = sqlite3.connect(temp_path, timeout=30)

    try:
        source_conn.backup(target_conn)
        target_conn.commit()
        result = target_conn.execute("PRAGMA quick_check").fetchone()
        quick = str(result[0] if result else "").strip().casefold()
        if quick != "ok":
            raise RuntimeError(
                f"Импортированная копия не прошла QUICK_CHECK: {quick}"
            )
    finally:
        target_conn.close()
        source_conn.close()

    os.replace(temp_path, destination)

    ok, message = _quick_check(destination)
    if not ok:
        raise RuntimeError(f"Проверка после импорта не пройдена: {message}")


def _existing_database_ok():
    if not DB_PATH.exists():
        return False

    ok, _message = _quick_check(DB_PATH)
    return ok


def ensure_database_ready(parent=None):
    """Prepare the writable database before MainWindow creates/migrates it."""
    # Source mode keeps the established developer workflow. A missing source
    # DB is simply created as before; the wizard is for the installed Beta.
    if not is_frozen():
        create_database()
        return True

    ensure_runtime_dirs()

    if _existing_database_ok():
        return True

    if DB_PATH.exists():
        QMessageBox.warning(
            parent,
            "ZervDiag Beta — база данных",
            "Локальная база найдена, но не прошла проверку SQLite.\n\n"
            f"Путь: {DB_PATH}\n\n"
            "ZervDiag не будет перезаписывать её автоматически. "
            "Выберите проверенную резервную копию.",
        )

    while True:
        box = QMessageBox(parent)
        box.setWindowTitle("ZervDiag Beta — первый запуск")
        box.setIcon(QMessageBox.Information)
        box.setText("Локальная база ZervDiag ещё не подготовлена.")
        box.setInformativeText(
            "Можно импортировать уже проиндексированную zervdiag.db "
            "или создать новую пустую базу.\n\n"
            f"Рабочая база будет храниться здесь:\n{DB_PATH}"
        )

        import_button = box.addButton(
            "Импортировать готовую базу",
            QMessageBox.AcceptRole,
        )
        create_button = box.addButton(
            "Создать новую базу",
            QMessageBox.ActionRole,
        )
        exit_button = box.addButton(
            "Выйти",
            QMessageBox.RejectRole,
        )

        box.setDefaultButton(import_button)
        box.exec()
        clicked = box.clickedButton()

        if clicked is exit_button or clicked is None:
            return False

        if clicked is create_button:
            try:
                create_database()
                return True
            except Exception as error:
                QMessageBox.critical(
                    parent,
                    "ZervDiag Beta",
                    "Не удалось создать базу:\n"
                    f"{type(error).__name__}: {error}",
                )
                continue

        filename, _selected_filter = QFileDialog.getOpenFileName(
            parent,
            "Выберите готовую базу ZervDiag",
            str(Path.home()),
            "SQLite database (*.db *.sqlite *.sqlite3);;Все файлы (*.*)",
        )

        if not filename:
            continue

        try:
            import_database(filename)
            QMessageBox.information(
                parent,
                "ZervDiag Beta",
                "База успешно импортирована и проверена.\n\n"
                f"Рабочая копия:\n{DB_PATH}",
            )
            return True
        except Exception as error:
            QMessageBox.critical(
                parent,
                "ZervDiag Beta — импорт базы",
                "Импорт не выполнен. Исходный файл не изменён.\n\n"
                f"{type(error).__name__}: {error}",
            )
