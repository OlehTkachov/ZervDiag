"""Read-only comparison of two ZervDiag SQLite database snapshots.

The comparison is based on indexed metadata and extracted text stored in each
database. Source documents are never opened, hydrated, modified, or re-indexed.
"""
from collections import Counter, defaultdict
import difflib
import hashlib
import ntpath
import posixpath
import sqlite3
import unicodedata
from pathlib import Path


MIN_COMPARE_TEXT_CHARS = 10
MAX_DIFF_CHARS = 200_000


def _readonly_connection(database_path, check=True):
    path = Path(database_path).expanduser().resolve()

    if not path.is_file():
        raise FileNotFoundError(f"Файл базы не найден: {path}")

    connection = sqlite3.connect(
        path.as_uri() + "?mode=ro",
        uri=True,
        timeout=30,
    )
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA query_only=ON")
    connection.execute("PRAGMA busy_timeout=30000")

    try:
        table = connection.execute(
            "SELECT 1 FROM sqlite_master "
            "WHERE type='table' AND name='files'"
        ).fetchone()

        if not table:
            raise ValueError(
                f"В базе отсутствует таблица files: {path.name}"
            )

        if check:
            result = connection.execute(
                "PRAGMA quick_check"
            ).fetchone()
            quick = str(result[0] if result else "").strip().casefold()

            if quick != "ok":
                raise ValueError(
                    f"База {path.name} не прошла QUICK_CHECK: "
                    f"{quick or 'нет результата'}"
                )

        return connection

    except Exception:
        connection.close()
        raise


def _normalise_text(value):
    if value is None:
        return ""

    if isinstance(value, bytes):
        value = value.decode("utf-8", errors="replace")
    else:
        value = str(value)

    value = value.replace("\r\n", "\n").replace("\r", "\n")
    return unicodedata.normalize("NFC", value)


def _text_fingerprint(value):
    text = _normalise_text(value)
    useful_length = len(text.strip())

    if useful_length < MIN_COMPARE_TEXT_CHARS:
        return None, useful_length

    digest = hashlib.sha256(
        text.encode("utf-8", errors="replace")
    ).hexdigest()

    return digest, useful_length


def _path_module(value):
    value = str(value or "")

    if ntpath.splitdrive(value)[0] or "\\" in value:
        return ntpath

    return posixpath


def _infer_root(records):
    paths = [
        str(record["filepath"])
        for record in records
        if record.get("filepath")
    ]

    if not paths:
        return None, posixpath

    modules = {_path_module(path) for path in paths}
    if len(modules) != 1:
        return None, _path_module(paths[0])

    module = next(iter(modules))
    parents = [
        module.dirname(module.normpath(path))
        for path in paths
    ]

    try:
        common = module.commonpath(parents)
    except (ValueError, OSError):
        return None, module

    _drive, tail = module.splitdrive(common)
    if not tail or tail in ("\\", "/"):
        return None, module

    return common, module


def _relative_path_key(record, root, module):
    if not root or not record.get("filepath"):
        return None

    path = module.normpath(str(record["filepath"]))

    try:
        relative = module.relpath(path, root)
    except (ValueError, OSError):
        return None

    if relative in ("", "."):
        return None

    if relative == ".." or relative.startswith(".." + module.sep):
        return None

    return relative.replace("\\", "/").casefold()


def _load_snapshot(database_path):
    path = Path(database_path).expanduser().resolve()
    connection = _readonly_connection(path, check=True)

    try:
        columns = {
            row["name"]
            for row in connection.execute(
                "PRAGMA table_info(files)"
            ).fetchall()
        }

        if "filepath" not in columns or "filename" not in columns:
            raise ValueError(
                f"В базе {path.name} отсутствуют filename/filepath."
            )

        defaults = {
            "id": "rowid",
            "filename": "''",
            "filepath": "''",
            "extension": "''",
            "size": "NULL",
            "modified": "NULL",
            "content": "NULL",
            "file_hash": "NULL",
            "is_cloud": "0",
            "extraction_status": "'pending'",
            "extraction_error": "NULL",
        }

        selected = []
        for name, fallback in defaults.items():
            if name in columns:
                selected.append(f'"{name}" AS "{name}"')
            else:
                selected.append(f'{fallback} AS "{name}"')

        query = (
            "SELECT "
            + ", ".join(selected)
            + " FROM files ORDER BY id"
        )

        records = []

        for row in connection.execute(query):
            filename = str(row["filename"] or "")
            extension = str(row["extension"] or Path(filename).suffix).lower()
            status = str(row["extraction_status"] or "pending")
            fingerprint, text_chars = _text_fingerprint(row["content"])
            content_hash = fingerprint if status == "ok" else None

            records.append({
                "id": int(row["id"]),
                "filename": filename,
                "filepath": str(row["filepath"] or ""),
                "extension": extension,
                "size": row["size"],
                "modified": row["modified"],
                "content_hash": content_hash,
                "text_chars": text_chars,
                "has_text": content_hash is not None,
                "file_hash": str(row["file_hash"] or "").strip() or None,
                "status": status,
                "error": str(row["extraction_error"] or ""),
                "is_cloud": bool(row["is_cloud"]),
            })

    finally:
        connection.close()

    root, module = _infer_root(records)
    for record in records:
        relative_key = _relative_path_key(record, root, module)
        record["relative_key"] = relative_key
        record["relative_depth"] = (
            relative_key.count("/") + 1
            if relative_key
            else 0
        )

    return {
        "path": str(path),
        "name": path.name,
        "records": records,
        "root": root,
        "path_module": module,
    }


def _absolute_path_key(record):
    filepath = str(record.get("filepath") or "")
    if not filepath:
        return None

    module = _path_module(filepath)
    return module.normcase(module.normpath(filepath)).replace("\\", "/")


def _filename_key(record):
    filename = str(record.get("filename") or "").strip().casefold()
    extension = str(record.get("extension") or "").strip().casefold()

    if not filename:
        return None

    return filename, extension


def _bucket_records(records, available_ids, key_function):
    buckets = defaultdict(list)

    for record in records:
        if record["id"] not in available_ids:
            continue

        key = key_function(record)
        if key is not None:
            buckets[key].append(record)

    return buckets


def _pair_by_key(
    left_records,
    right_records,
    unmatched_left,
    unmatched_right,
    key_function_left,
    key_function_right,
    method,
):
    left_buckets = _bucket_records(
        left_records,
        unmatched_left,
        key_function_left,
    )
    right_buckets = _bucket_records(
        right_records,
        unmatched_right,
        key_function_right,
    )

    pairs = []
    ambiguous = []

    for key in sorted(
        set(left_buckets).intersection(right_buckets),
        key=lambda value: str(value),
    ):
        left_candidates = left_buckets[key]
        right_candidates = right_buckets[key]

        if len(left_candidates) == 1 and len(right_candidates) == 1:
            left = left_candidates[0]
            right = right_candidates[0]
            pairs.append((left, right, method))
            unmatched_left.discard(left["id"])
            unmatched_right.discard(right["id"])
            continue

        ambiguous.append({
            "kind": "ambiguous",
            "summary": (
                "Несколько файлов подходят под один ключ. "
                "Автоматическое сопоставление не выполнялось."
            ),
            "match_method": method,
            "filename": (
                left_candidates[0]["filename"]
                if left_candidates
                else right_candidates[0]["filename"]
            ),
            "left_candidates": left_candidates,
            "right_candidates": right_candidates,
        })

        unmatched_left.difference_update(
            record["id"] for record in left_candidates
        )
        unmatched_right.difference_update(
            record["id"] for record in right_candidates
        )

    return pairs, ambiguous


def _metadata_changes(left, right):
    changes = []

    for field, label in (
        ("filename", "имя"),
        ("extension", "тип"),
        ("size", "размер"),
        ("modified", "дата изменения"),
        ("status", "статус обработки"),
        ("is_cloud", "тип хранения"),
    ):
        left_value = left.get(field)
        right_value = right.get(field)

        if field == "modified":
            if left_value is None and right_value is None:
                continue

            try:
                equal = (
                    left_value is not None
                    and right_value is not None
                    and abs(float(left_value) - float(right_value)) < 1.0
                )
            except (TypeError, ValueError):
                equal = left_value == right_value

            if equal:
                continue

        elif left_value == right_value:
            continue

        changes.append({
            "field": field,
            "label": label,
            "left": left_value,
            "right": right_value,
        })

    return changes


def _classify_pair(left, right, method):
    changes = _metadata_changes(left, right)
    source_hash_changed = bool(
        left.get("file_hash")
        and right.get("file_hash")
        and left["file_hash"] != right["file_hash"]
    )

    if left["has_text"] and right["has_text"]:
        if left["content_hash"] != right["content_hash"]:
            kind = "content_changed"
            summary = "Изменился извлечённый текст"
        elif source_hash_changed:
            kind = "source_changed_text_same"
            summary = (
                "Исходный файл отличается, "
                "но извлечённый текст совпадает"
            )
        elif changes:
            kind = "metadata_changed"
            summary = "Текст совпадает, отличаются метаданные"
        else:
            kind = "identical"
            summary = "Извлечённый текст и метаданные совпадают"
    else:
        if left["status"] != right["status"]:
            kind = "status_changed"
            summary = "Различаются статусы обработки; текста недостаточно"
        elif source_hash_changed:
            kind = "source_changed"
            summary = "Различается хеш исходного файла; текста недостаточно"
        elif changes:
            kind = "metadata_changed"
            summary = "Текст недоступен, отличаются метаданные"
        else:
            kind = "text_unavailable"
            summary = "Нет достаточного извлечённого текста для сравнения"

    confidence = "high"
    if (
        method in ("filename", "content_hash")
        or (
            method == "relative_path"
            and (
                left.get("relative_depth", 0) <= 1
                or right.get("relative_depth", 0) <= 1
            )
        )
    ):
        confidence = "low"

    if method == "filename":
        summary = "Сопоставлено только по имени. " + summary
    elif method == "content_hash":
        summary = "Сопоставлено по совпадению текста; проверьте документ. " + summary

    return {
        "kind": kind,
        "summary": summary,
        "filename": left["filename"] or right["filename"],
        "left": left,
        "right": right,
        "match_method": method,
        "match_confidence": confidence,
        "metadata_changes": changes,
        "source_hash_changed": source_hash_changed,
    }


def compare_databases(left_path, right_path):
    """Compare two ZervDiag databases without changing either source."""
    left_snapshot = _load_snapshot(left_path)
    right_snapshot = _load_snapshot(right_path)

    if Path(left_snapshot["path"]) == Path(right_snapshot["path"]):
        raise ValueError("Выбрана одна и та же база дважды.")

    left_records = left_snapshot["records"]
    right_records = right_snapshot["records"]

    unmatched_left = {record["id"] for record in left_records}
    unmatched_right = {record["id"] for record in right_records}

    pairs = []
    ambiguous_items = []

    def add_pairing(result):
        found_pairs, found_ambiguous = result
        pairs.extend(found_pairs)
        ambiguous_items.extend(found_ambiguous)

    # 1. Полный путь — самое надёжное сопоставление.
    add_pairing(_pair_by_key(
        left_records,
        right_records,
        unmatched_left,
        unmatched_right,
        _absolute_path_key,
        _absolute_path_key,
        "absolute_path",
    ))

    # 2. Относительный путь позволяет сравнить библиотеки,
    # перенесённые на другой диск или компьютер.
    left_root = left_snapshot["root"]
    right_root = right_snapshot["root"]
    left_module = left_snapshot["path_module"]
    right_module = right_snapshot["path_module"]

    if left_root and right_root:
        add_pairing(_pair_by_key(
            left_records,
            right_records,
            unmatched_left,
            unmatched_right,
            lambda record: record.get("relative_key"),
            lambda record: record.get("relative_key"),
            "relative_path",
        ))

    # 3. Если file_hash заполнен в обеих базах, используем его
    # для обнаружения переименованных/перемещённых файлов.
    add_pairing(_pair_by_key(
        left_records,
        right_records,
        unmatched_left,
        unmatched_right,
        lambda record: (
            (record["extension"], record["file_hash"])
            if record.get("file_hash")
            else None
        ),
        lambda record: (
            (record["extension"], record["file_hash"])
            if record.get("file_hash")
            else None
        ),
        "file_hash",
    ))

    # 4. Уникальный хеш извлечённого текста помогает найти файл,
    # который переименовали, но содержимое которого не менялось.
    add_pairing(_pair_by_key(
        left_records,
        right_records,
        unmatched_left,
        unmatched_right,
        lambda record: (
            (record["extension"], record["content_hash"])
            if record.get("content_hash")
            else None
        ),
        lambda record: (
            (record["extension"], record["content_hash"])
            if record.get("content_hash")
            else None
        ),
        "content_hash",
    ))

    # 5. Последний резервный способ — уникальное имя и расширение.
    # Такие совпадения помечаются низкой уверенностью.
    add_pairing(_pair_by_key(
        left_records,
        right_records,
        unmatched_left,
        unmatched_right,
        _filename_key,
        _filename_key,
        "filename",
    ))

    items = [
        _classify_pair(left, right, method)
        for left, right, method in pairs
    ]

    left_by_id = {
        record["id"]: record
        for record in left_records
    }
    right_by_id = {
        record["id"]: record
        for record in right_records
    }

    for file_id in sorted(unmatched_left):
        record = left_by_id[file_id]
        items.append({
            "kind": "only_left",
            "summary": "Документ есть только в первой базе",
            "filename": record["filename"],
            "left": record,
            "right": None,
            "match_method": None,
            "match_confidence": None,
            "metadata_changes": [],
        })

    for file_id in sorted(unmatched_right):
        record = right_by_id[file_id]
        items.append({
            "kind": "only_right",
            "summary": "Документ есть только во второй базе",
            "filename": record["filename"],
            "left": None,
            "right": record,
            "match_method": None,
            "match_confidence": None,
            "metadata_changes": [],
        })

    items.extend(ambiguous_items)

    priority = {
        "content_changed": 0,
        "source_changed_text_same": 1,
        "source_changed": 2,
        "status_changed": 3,
        "metadata_changed": 4,
        "only_left": 5,
        "only_right": 6,
        "ambiguous": 7,
        "text_unavailable": 8,
        "identical": 9,
    }
    items.sort(
        key=lambda item: (
            priority.get(item["kind"], 99),
            str(item.get("filename") or "").casefold(),
        )
    )

    counts = Counter(item["kind"] for item in items)
    matched = len(pairs)

    summary = {
        "left_total": len(left_records),
        "right_total": len(right_records),
        "matched": matched,
        "identical": counts["identical"],
        "content_changed": counts["content_changed"],
        "source_changed_text_same": counts["source_changed_text_same"],
        "source_changed": counts["source_changed"],
        "metadata_changed": counts["metadata_changed"],
        "status_changed": counts["status_changed"],
        "text_unavailable": counts["text_unavailable"],
        "only_left": counts["only_left"],
        "only_right": counts["only_right"],
        "ambiguous": counts["ambiguous"],
    }

    return {
        "left_path": left_snapshot["path"],
        "right_path": right_snapshot["path"],
        "left_name": left_snapshot["name"],
        "right_name": right_snapshot["name"],
        "left_root": left_root,
        "right_root": right_root,
        "summary": summary,
        "items": items,
    }


def _read_content(database_path, file_id):
    connection = _readonly_connection(
        database_path,
        check=False,
    )

    try:
        columns = {
            row["name"]
            for row in connection.execute(
                "PRAGMA table_info(files)"
            ).fetchall()
        }

        if "content" not in columns:
            return ""

        status_expression = (
            '"extraction_status"'
            if "extraction_status" in columns
            else "'ok'"
        )
        row = connection.execute(
            "SELECT content, "
            + status_expression
            + " AS extraction_status FROM files WHERE id = ?",
            (int(file_id),),
        ).fetchone()

        if not row or str(row["extraction_status"] or "") != "ok":
            return ""

        return _normalise_text(row["content"])

    finally:
        connection.close()


def load_content_diff(
    left_database,
    right_database,
    left_id,
    right_id,
    left_name="Первая база",
    right_name="Вторая база",
):
    """Load and render one selected text difference on demand."""
    left_text = _read_content(left_database, left_id)
    right_text = _read_content(right_database, right_id)

    if (
        len(left_text.strip()) < MIN_COMPARE_TEXT_CHARS
        or len(right_text.strip()) < MIN_COMPARE_TEXT_CHARS
    ):
        return (
            "Недостаточно извлечённого текста хотя бы в одной базе. "
            "Сравнить содержимое документа невозможно."
        )

    if left_text == right_text:
        return "Извлечённый текст совпадает."

    diff_lines = difflib.unified_diff(
        left_text.splitlines(),
        right_text.splitlines(),
        fromfile=left_name,
        tofile=right_name,
        lineterm="",
        n=3,
    )
    result = "\n".join(diff_lines)

    if len(result) > MAX_DIFF_CHARS:
        result = (
            result[:MAX_DIFF_CHARS]
            + "\n\n... Разница обрезана для отображения."
        )

    return result or "Извлечённый текст совпадает."
