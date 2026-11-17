import os
import sqlite3
from datetime import datetime
from pathlib import Path

from app_paths import ensure_runtime_dirs
from database.libraries import get_active_database_path


def _pending_import_path(database_path=None):
    path = Path(database_path or get_active_database_path()).resolve()
    return path.with_name(path.stem + ".pending-import.db")


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
    """Create a consistent user-selected backup of the active library database."""
    ensure_runtime_dirs()
    database_path = get_active_database_path()

    if not database_path.exists():
        raise RuntimeError(
            "Рабочая база активной библиотеки ZervDiag не найдена."
        )

    return _backup_copy(
        database_path,
        destination_path,
    )


def _backup_current_before_import(database_path):
    database_path = Path(database_path).resolve()
    if not database_path.exists():
        return None

    ok, _message = quick_check(database_path)
    if not ok:
        return None

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup_path = database_path.with_name(
        f"{database_path.stem}_pre_import_{stamp}.db"
    )
    return _backup_copy(database_path, backup_path)


def _replace_working_database(prepared_path, database_path=None):
    prepared = Path(prepared_path).resolve()
    destination = Path(
        database_path or get_active_database_path()
    ).resolve()

    ok, message = quick_check(
        prepared
    )
    if not ok:
        raise RuntimeError(
            "Подготовленная база не прошла проверку: "
            f"{message}"
        )

    ensure_runtime_dirs()

    backup_path = _backup_current_before_import(destination)

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
    """Immediately import into the active library before it is opened."""
    source = Path(source_path).resolve()
    destination = get_active_database_path().resolve()

    if source == destination:
        return None

    ensure_runtime_dirs()

    prepared = destination.with_name(
        destination.stem + ".importing.db"
    )
    _remove_database_family(
        prepared
    )

    _backup_copy(
        source,
        prepared,
    )

    return _replace_working_database(
        prepared,
        destination,
    )


def stage_import_database(source_path):
    """Validate and stage an import for the currently active library."""
    source = Path(source_path).resolve()
    destination = get_active_database_path().resolve()

    if source == destination:
        raise RuntimeError(
            "Выбрана текущая рабочая база ZervDiag."
        )

    ensure_runtime_dirs()
    pending_path = _pending_import_path(destination)
    _remove_database_family(pending_path)

    return _backup_copy(source, pending_path)


def has_staged_import():
    return _pending_import_path().exists()


def apply_staged_import():
    """Apply the staged import for the active library before its DB is opened."""
    database_path = get_active_database_path().resolve()
    pending_path = _pending_import_path(database_path)
    if not pending_path.exists():
        return False, None

    backup_path = _replace_working_database(
        pending_path,
        database_path,
    )

    return True, backup_path
