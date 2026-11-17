"""Persistent registry for independent ZervDiag document libraries.

Each library owns its own SQLite database. The historical zervdiag.db remains
the default library so existing installations keep all indexed data in place.
"""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import threading
import uuid

from app_paths import (
    DB_PATH,
    LIBRARIES_DIR as _LIBRARIES_DIR,
    LIBRARY_REGISTRY_PATH as _LIBRARY_REGISTRY_PATH,
)


# These module-level paths are intentionally easy to replace in isolated smoke
# tests. Production uses the paths supplied by app_paths.
DEFAULT_DB_PATH = DB_PATH
LIBRARIES_DIR = _LIBRARIES_DIR
REGISTRY_PATH = _LIBRARY_REGISTRY_PATH

_SCHEMA_VERSION = 1
_LOCK = threading.RLock()


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _normalise_folder(folder):
    value = str(folder or "").strip()
    if not value:
        return ""
    return str(Path(value).expanduser().resolve())


def _folder_key(folder):
    if not folder:
        return ""
    return os.path.normcase(os.path.normpath(str(folder))).casefold()


def _read_registry_unlocked():
    if not REGISTRY_PATH.is_file():
        return None

    try:
        data = json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))
    except Exception as error:
        raise RuntimeError(
            f"Не удалось прочитать реестр библиотек {REGISTRY_PATH}: {error}"
        ) from error

    if not isinstance(data, dict) or data.get("schema_version") != _SCHEMA_VERSION:
        raise RuntimeError(
            "Формат реестра библиотек ZervDiag не поддерживается. "
            "Файл libraries.json не изменён."
        )

    libraries = data.get("libraries")
    if not isinstance(libraries, list) or not libraries:
        raise RuntimeError(
            "Реестр библиотек пуст или повреждён. "
            "Файл libraries.json не изменён."
        )

    normalised = []
    ids = set()
    for item in libraries:
        if not isinstance(item, dict):
            raise RuntimeError("В реестре найден некорректный элемент библиотеки.")
        library_id = str(item.get("id") or "").strip()
        name = str(item.get("name") or "").strip()
        database_path = str(item.get("database_path") or "").strip()
        if not library_id or not name or not database_path or library_id in ids:
            raise RuntimeError("В реестре найдена библиотека без ID, имени или пути базы.")
        ids.add(library_id)
        normalised.append({
            "id": library_id,
            "name": name,
            "source_folder": _normalise_folder(item.get("source_folder", "")),
            "database_path": str(Path(database_path).expanduser().resolve()),
            "created_at": str(item.get("created_at") or ""),
        })

    active_id = str(data.get("active_library_id") or "").strip()
    if active_id not in ids:
        active_id = normalised[0]["id"]

    return {
        "schema_version": _SCHEMA_VERSION,
        "active_library_id": active_id,
        "libraries": normalised,
    }


def _write_registry_unlocked(data):
    REGISTRY_PATH.parent.mkdir(parents=True, exist_ok=True)
    temporary = REGISTRY_PATH.with_name(
        REGISTRY_PATH.name + f".{uuid.uuid4().hex}.tmp"
    )
    payload = json.dumps(
        data,
        ensure_ascii=False,
        indent=2,
    )
    try:
        temporary.write_text(payload + "\n", encoding="utf-8")
        os.replace(temporary, REGISTRY_PATH)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def ensure_library_registry(legacy_folder=""):
    """Create a default library once, preserving the historical database."""
    with _LOCK:
        data = _read_registry_unlocked()
        if data is not None:
            return data

        LIBRARIES_DIR.mkdir(parents=True, exist_ok=True)
        default = {
            "id": "default",
            "name": "Основная библиотека",
            "source_folder": _normalise_folder(legacy_folder),
            "database_path": str(Path(DEFAULT_DB_PATH).expanduser().resolve()),
            "created_at": _now(),
        }
        data = {
            "schema_version": _SCHEMA_VERSION,
            "active_library_id": "default",
            "libraries": [default],
        }
        _write_registry_unlocked(data)
        return data


def get_libraries():
    with _LOCK:
        data = _read_registry_unlocked()
        if data is None:
            data = ensure_library_registry()
        return [dict(item) for item in data["libraries"]]


def get_library(library_id):
    library_id = str(library_id or "")
    for item in get_libraries():
        if item["id"] == library_id:
            return item
    raise KeyError(f"Библиотека не найдена: {library_id}")


def get_active_library_id():
    with _LOCK:
        data = _read_registry_unlocked()
        if data is None:
            data = ensure_library_registry()
        return data["active_library_id"]


def get_active_library():
    return get_library(get_active_library_id())


def get_active_database_path():
    """Resolve the database for the currently selected library."""
    try:
        library = get_active_library()
        return Path(library["database_path"]).expanduser().resolve()
    except FileNotFoundError:
        return Path(DEFAULT_DB_PATH).expanduser().resolve()


def set_active_library(library_id):
    library_id = str(library_id or "").strip()
    with _LOCK:
        data = _read_registry_unlocked()
        if data is None:
            data = ensure_library_registry()
        if not any(item["id"] == library_id for item in data["libraries"]):
            raise KeyError(f"Библиотека не найдена: {library_id}")
        data["active_library_id"] = library_id
        _write_registry_unlocked(data)
    return get_library(library_id)


def add_library(name, source_folder):
    name = str(name or "").strip()
    folder = _normalise_folder(source_folder)

    if not name:
        raise ValueError("Укажите название библиотеки.")
    if not folder or not Path(folder).is_dir():
        raise ValueError("Выбранная папка библиотеки не существует.")

    with _LOCK:
        data = _read_registry_unlocked()
        if data is None:
            data = ensure_library_registry()

        folder_key = _folder_key(folder)
        for item in data["libraries"]:
            if _folder_key(item.get("source_folder", "")) == folder_key:
                raise ValueError(
                    f"Эта папка уже добавлена в библиотеку «{item['name']}»."
                )
            if item["name"].casefold() == name.casefold():
                raise ValueError(
                    f"Библиотека с названием «{name}» уже существует."
                )

        LIBRARIES_DIR.mkdir(parents=True, exist_ok=True)
        library_id = uuid.uuid4().hex
        database_path = LIBRARIES_DIR / f"{library_id}.db"
        library = {
            "id": library_id,
            "name": name,
            "source_folder": folder,
            "database_path": str(database_path.resolve()),
            "created_at": _now(),
        }
        data["libraries"].append(library)
        _write_registry_unlocked(data)

    return dict(library)


def update_library_folder(library_id, source_folder):
    folder = _normalise_folder(source_folder)
    if not folder or not Path(folder).is_dir():
        raise ValueError("Выбранная папка библиотеки не существует.")

    with _LOCK:
        data = _read_registry_unlocked()
        if data is None:
            data = ensure_library_registry()

        target = next(
            (item for item in data["libraries"] if item["id"] == str(library_id)),
            None,
        )
        if target is None:
            raise KeyError(f"Библиотека не найдена: {library_id}")

        folder_key = _folder_key(folder)
        for item in data["libraries"]:
            if item["id"] != target["id"] and _folder_key(
                item.get("source_folder", "")
            ) == folder_key:
                raise ValueError(
                    f"Эта папка уже принадлежит библиотеке «{item['name']}»."
                )

        target["source_folder"] = folder
        _write_registry_unlocked(data)

    return dict(target)


def remove_library(library_id, *, delete_database=False):
    """Remove a library record; database deletion is opt-in and used only for rollback."""
    library_id = str(library_id or "")
    with _LOCK:
        data = _read_registry_unlocked()
        if data is None:
            return False
        target = next(
            (item for item in data["libraries"] if item["id"] == library_id),
            None,
        )
        if target is None:
            return False
        if target["id"] == "default":
            raise ValueError("Основную библиотеку нельзя удалить.")
        if len(data["libraries"]) <= 1:
            raise ValueError("Нельзя удалить последнюю библиотеку.")

        data["libraries"] = [
            item for item in data["libraries"] if item["id"] != library_id
        ]
        if data["active_library_id"] == library_id:
            data["active_library_id"] = data["libraries"][0]["id"]
        _write_registry_unlocked(data)

    if delete_database:
        database_path = Path(target["database_path"])
        for candidate in (
            database_path,
            Path(str(database_path) + "-wal"),
            Path(str(database_path) + "-shm"),
        ):
            try:
                candidate.unlink()
            except FileNotFoundError:
                pass
    return True
