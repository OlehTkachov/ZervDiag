import os
import sqlite3
from datetime import datetime
from pathlib import Path

from app_paths import DB_PATH, ensure_runtime_dirs


PENDING_IMPORT_PATH = DB_PATH.with_name(
    "zervdiag.pending-import.db"
)


def quick_check(path):
    path = Path(path)

    if not path.exists() or not path.is_file():
        return False, "Файл базы не найден."

    try:
        uri = path.resolve().as_uri() + "?mode=ro"
        conn = sqlite3.connect(
            uri,
            uri=True,
            timeout=30,
        )

        try:
            result = conn.execute(
                "PRAGMA quick_check"
            ).fetchone()
            quick = str(
                result[0]
                if result
                else ""
            ).strip().casefold()

            table = conn.execute(
                "SELECT 1 FROM sqlite_master "
                "WHERE type='table' AND name='files'"
            ).fetchone()

        finally:
            conn.close()

        if quick != "ok":
            return (
                False,
                "SQLite QUICK_CHECK: "
                f"{quick or 'нет результата'}",
            )

        if not table:
            return (
                False,
                "В базе отсутствует таблица files.",
            )

        return True, "ok"

    except Exception as error:
        return (
            False,
            f"{type(error).__name__}: {error}",
        )


def _remove_sidecars(path):
    path = Path(path)

    for candidate in (
        Path(str(path) + "-wal"),
        Path(str(path) + "-shm"),
    ):
        try:
            candidate.unlink()
        except FileNotFoundError:
            pass


def _remove_database_family(path):
    path = Path(path)

    for candidate in (
        path,
        Path(str(path) + "-wal"),
        Path(str(path) + "-shm"),
    ):
        try:
            candidate.unlink()
        except FileNotFoundError:
            pass


def _backup_copy(source_path, destination_path):
    source = Path(source_path).resolve()
    destination = Path(destination_path).resolve()

    if source == destination:
        raise RuntimeError(
            "Исходная и целевая база совпадают."
        )

    ok, message = quick_check(source)
    if not ok:
        raise RuntimeError(
            "Исходная база не прошла проверку: "
            f"{message}"
        )

    destination.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    temp_path = destination.with_name(
        destination.name + ".building"
    )
    _remove_database_family(temp_path)

    source_uri = (
        source.as_uri()
        + "?mode=ro"
    )
    source_conn = sqlite3.connect(
        source_uri,
        uri=True,
        timeout=30,
    )
    target_conn = sqlite3.connect(
        temp_path,
        timeout=30,
    )

    try:
        source_conn.backup(
            target_conn
        )
        target_conn.commit()

        result = target_conn.execute(
            "PRAGMA quick_check"
        ).fetchone()
        quick = str(
            result[0]
            if result
            else ""
        ).strip().casefold()

        if quick != "ok":
            raise RuntimeError(
                "Созданная копия не прошла "
                f"QUICK_CHECK: {quick}"
            )

    finally:
        target_conn.close()
        source_conn.close()

    os.replace(
        temp_path,
        destination,
    )

    ok, message = quick_check(
        destination
    )
    if not ok:
        raise RuntimeError(
            "Проверка готовой копии не пройдена: "
            f"{message}"
        )

    return destination


def export_database(destination_path):
    """Create a consistent user-selected backup of the live database."""
    ensure_runtime_dirs()

    if not DB_PATH.exists():
        raise RuntimeError(
            "Рабочая база ZervDiag не найдена."
        )

    return _backup_copy(
        DB_PATH,
        destination_path,
    )


def _backup_current_before_import():
    if not DB_PATH.exists():
        return None

    ok, _message = quick_check(
        DB_PATH
    )
    if not ok:
        return None

    stamp = datetime.now().strftime(
        "%Y%m%d_%H%M%S"
    )
    backup_path = DB_PATH.with_name(
        f"zervdiag_pre_import_{stamp}.db"
    )

    return export_database(
        backup_path
    )


def _replace_working_database(prepared_path):
    prepared = Path(
        prepared_path
    ).resolve()
    destination = DB_PATH.resolve()

    ok, message = quick_check(
        prepared
    )
    if not ok:
        raise RuntimeError(
            "Подготовленная база не прошла проверку: "
            f"{message}"
        )

    ensure_runtime_dirs()

    backup_path = (
        _backup_current_before_import()
    )

    # Import is applied only before MainWindow opens the database. Remove
    # sidecars belonging to the previous database so an old WAL can never be
    # associated with the replacement file.
    _remove_sidecars(
        destination
    )

    os.replace(
        prepared,
        destination,
    )

    ok, message = quick_check(
        destination
    )
    if not ok:
        raise RuntimeError(
            "Проверка после импорта не пройдена: "
            f"{message}"
        )

    return backup_path


def import_database(source_path):
    """Immediately import before the application opens its working DB."""
    source = Path(source_path).resolve()
    destination = DB_PATH.resolve()

    if source == destination:
        return None

    ensure_runtime_dirs()

    prepared = DB_PATH.with_name(
        "zervdiag.importing.db"
    )
    _remove_database_family(
        prepared
    )

    _backup_copy(
        source,
        prepared,
    )

    return _replace_working_database(
        prepared
    )


def stage_import_database(source_path):
    """Validate and stage an import while the GUI is still running."""
    source = Path(source_path).resolve()

    if source == DB_PATH.resolve():
        raise RuntimeError(
            "Выбрана текущая рабочая база ZervDiag."
        )

    ensure_runtime_dirs()
    _remove_database_family(
        PENDING_IMPORT_PATH
    )

    return _backup_copy(
        source,
        PENDING_IMPORT_PATH,
    )


def has_staged_import():
    return PENDING_IMPORT_PATH.exists()


def apply_staged_import():
    """Apply a staged import before MainWindow opens any database connection."""
    if not has_staged_import():
        return False, None

    backup_path = _replace_working_database(
        PENDING_IMPORT_PATH
    )

    return True, backup_path
