import sqlite3
from pathlib import Path

from PySide6.QtWidgets import QFileDialog, QMessageBox

from app_paths import DB_PATH, ensure_runtime_dirs, is_frozen
from database.db import create_database
from database.transfer import (
    apply_staged_import,
    import_database,
    quick_check,
)


DATABASE_SETUP_KEY = "beta_database_choice_v2"


def _existing_database_ok():
    if not DB_PATH.exists():
        return False

    ok, _message = quick_check(
        DB_PATH
    )
    return ok


def _database_choice_acknowledged():
    if not DB_PATH.exists():
        return False

    try:
        conn = sqlite3.connect(
            DB_PATH,
            timeout=30,
        )

        try:
            table = conn.execute(
                "SELECT 1 FROM sqlite_master "
                "WHERE type='table' AND name='app_meta'"
            ).fetchone()

            if not table:
                return False

            row = conn.execute(
                "SELECT value FROM app_meta "
                "WHERE key = ?",
                (DATABASE_SETUP_KEY,),
            ).fetchone()

            return bool(
                row
                and str(row[0]) == "1"
            )

        finally:
            conn.close()

    except Exception:
        return False


def _mark_database_choice_acknowledged():
    # Ensure app_meta exists even for an imported database created by an older
    # ZervDiag build, then store the one-time migration acknowledgement inside
    # the database itself.
    create_database()

    conn = sqlite3.connect(
        DB_PATH,
        timeout=30,
    )

    try:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS app_meta (
                key TEXT PRIMARY KEY,
                value TEXT
            )
            """
        )
        conn.execute(
            """
            INSERT INTO app_meta (key, value)
            VALUES (?, '1')
            ON CONFLICT(key)
            DO UPDATE SET value = '1'
            """,
            (DATABASE_SETUP_KEY,),
        )
        conn.commit()

    finally:
        conn.close()


def _backup_note(backup_path):
    if not backup_path:
        return ""

    return (
        "\n\nПредыдущая рабочая база автоматически сохранена:\n"
        f"{backup_path}"
    )


def _choose_database_file(parent):
    filename, _selected_filter = QFileDialog.getOpenFileName(
        parent,
        "Выберите готовую базу ZervDiag",
        str(Path.home()),
        "SQLite database (*.db *.sqlite *.sqlite3);;Все файлы (*.*)",
    )

    return filename


def _import_selected_database(parent, filename):
    try:
        backup_path = import_database(
            filename
        )
        _mark_database_choice_acknowledged()

        QMessageBox.information(
            parent,
            "ZervDiag Beta",
            "База успешно импортирована и проверена.\n\n"
            f"Рабочая копия:\n{DB_PATH}"
            + _backup_note(backup_path),
        )
        return True

    except Exception as error:
        QMessageBox.critical(
            parent,
            "ZervDiag Beta — импорт базы",
            "Импорт не выполнен. Исходный файл не изменён.\n\n"
            f"{type(error).__name__}: {error}",
        )
        return False


def _confirm_existing_database(parent):
    """One-time migration choice for a valid DB left by an older Beta."""
    while True:
        box = QMessageBox(parent)
        box.setWindowTitle(
            "ZervDiag Beta — база данных"
        )
        box.setIcon(
            QMessageBox.Icon.Information
        )
        box.setText(
            "Обнаружена существующая локальная база ZervDiag."
        )
        box.setInformativeText(
            "Она могла остаться от предыдущей установки Beta. "
            "Выберите явно, какую базу использовать.\n\n"
            f"Текущая база:\n{DB_PATH}"
        )

        use_button = box.addButton(
            "Использовать текущую базу",
            QMessageBox.ButtonRole.AcceptRole,
        )
        import_button = box.addButton(
            "Импортировать другую базу",
            QMessageBox.ButtonRole.ActionRole,
        )
        exit_button = box.addButton(
            "Выйти",
            QMessageBox.ButtonRole.RejectRole,
        )

        box.setDefaultButton(
            import_button
        )
        box.exec()
        clicked = box.clickedButton()

        if clicked is exit_button or clicked is None:
            return False

        if clicked is use_button:
            _mark_database_choice_acknowledged()
            return True

        filename = _choose_database_file(
            parent
        )
        if not filename:
            continue

        if _import_selected_database(
            parent,
            filename,
        ):
            return True


def ensure_database_ready(parent=None):
    """Prepare the writable database before MainWindow creates/migrates it."""
    # Source mode keeps the established developer workflow. A missing source
    # DB is simply created as before; the migration UI is for installed Beta.
    if not is_frozen():
        create_database()
        return True

    ensure_runtime_dirs()

    # Settings can stage an import while the GUI is running. It is applied
    # here, before MainWindow opens the working database, so the replacement
    # remains atomic and no live SQLite connection is swapped underneath UI.
    try:
        applied, backup_path = (
            apply_staged_import()
        )
    except Exception as error:
        QMessageBox.critical(
            parent,
            "ZervDiag Beta — импорт базы",
            "Не удалось применить подготовленный импорт. "
            "Текущая база не будет открыта автоматически.\n\n"
            f"{type(error).__name__}: {error}",
        )
        return False

    if applied:
        _mark_database_choice_acknowledged()
        QMessageBox.information(
            parent,
            "ZervDiag Beta — импорт базы",
            "Подготовленная база успешно применена и проверена.\n\n"
            f"Рабочая база:\n{DB_PATH}"
            + _backup_note(backup_path),
        )
        return True

    if _existing_database_ok():
        if _database_choice_acknowledged():
            return True

        return _confirm_existing_database(
            parent
        )

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
        box.setWindowTitle(
            "ZervDiag Beta — первый запуск"
        )
        box.setIcon(
            QMessageBox.Icon.Information
        )
        box.setText(
            "Локальная база ZervDiag ещё не подготовлена."
        )
        box.setInformativeText(
            "Можно импортировать уже проиндексированную zervdiag.db "
            "или создать новую пустую базу.\n\n"
            f"Рабочая база будет храниться здесь:\n{DB_PATH}"
        )

        import_button = box.addButton(
            "Импортировать готовую базу",
            QMessageBox.ButtonRole.AcceptRole,
        )
        create_button = box.addButton(
            "Создать новую базу",
            QMessageBox.ButtonRole.ActionRole,
        )
        exit_button = box.addButton(
            "Выйти",
            QMessageBox.ButtonRole.RejectRole,
        )

        box.setDefaultButton(
            import_button
        )
        box.exec()
        clicked = box.clickedButton()

        if clicked is exit_button or clicked is None:
            return False

        if clicked is create_button:
            try:
                create_database()
                _mark_database_choice_acknowledged()
                return True
            except Exception as error:
                QMessageBox.critical(
                    parent,
                    "ZervDiag Beta",
                    "Не удалось создать базу:\n"
                    f"{type(error).__name__}: {error}",
                )
                continue

        filename = _choose_database_file(
            parent
        )

        if not filename:
            continue

        if _import_selected_database(
            parent,
            filename,
        ):
            return True
