import csv
import re
import shutil
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path

from database.db import get_connection


TECHNICAL_EXTENSIONS = {
    ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".txt",
    ".rtf", ".csv", ".xml", ".dwg", ".dxf", ".jpg", ".jpeg",
    ".png", ".tif", ".tiff", ".bmp",
}

MEDIA_OR_BINARY_EXTENSIONS = {
    ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".mp3", ".wav",
    ".exe", ".msi", ".dll", ".iso",
}

ARCHIVE_EXTENSIONS = {".zip", ".rar", ".7z", ".tar", ".gz"}

TEMP_NAME_RE = re.compile(
    r"(^~\$)|(\.tmp$)|(\.temp$)|(\.bak$)|"
    r"(^thumbs\.db$)|(^desktop\.ini$)|"
    r"(\btemp\b)|(\btmp\b)|(\bbackup\b)|(\bold\b)|"
    r"(копия)|(резервн)",
    flags=re.IGNORECASE,
)

TECH_CODE_RE = re.compile(
    r"(?<![0-9A-Za-zА-Яа-яЁё])"
    r"(?:[A-Za-zА-Яа-яЁё]{1,6}[-_ /.]?\d{2,6}"
    r"(?:[-_ /.]?[A-Za-zА-Яа-яЁё]{0,3})?)"
    r"(?![0-9A-Za-zА-Яа-яЁё])",
    flags=re.IGNORECASE,
)

TECH_WORD_RE = re.compile(
    r"\b(?:manual|service|repair|maintenance|hydraulic|electrical|"
    r"wiring|schematic|diagram|parts|catalog|error|fault|"
    r"руководств|ремонт|сервис|гидравл|электр|схем|запчаст|"
    r"каталог|ошибк|діагност|сервіс|гідравл|електр)\w*",
    flags=re.IGNORECASE,
)


@dataclass(frozen=True)
class AuditRecord:
    file_id: int
    filename: str
    filepath: str
    extension: str
    size: int
    extraction_status: str
    extraction_error: str
    is_cloud: bool
    content_chars: int
    score: int
    category: str
    reasons: tuple[str, ...]
    parent_folder: str


@dataclass(frozen=True)
class GroupStat:
    name: str
    total: int
    suspicious: int
    review: int
    useful: int


def _category_from_score(score):
    if score >= 75:
        return "useful"
    if score >= 45:
        return "review"
    return "suspicious"


def _score_row(filename, filepath, extension, size, extraction_status, content):
    score = 50
    reasons = []

    filename = filename or ""
    filepath = filepath or ""
    extension = (extension or "").lower()
    content = content or ""
    content_chars = len(content.strip())
    combined_meta = f"{filename}\n{filepath}"

    if extension in TECHNICAL_EXTENSIONS:
        score += 12
        reasons.append("технический формат")
    elif extension in MEDIA_OR_BINARY_EXTENSIONS:
        score -= 22
        reasons.append("медиа/исполняемый формат")
    elif extension in ARCHIVE_EXTENSIONS:
        score -= 5
        reasons.append("архив — проверить содержимое")
    elif extension:
        score -= 8
        reasons.append("нетипичный формат")

    if TECH_CODE_RE.search(combined_meta):
        score += 15
        reasons.append("технический код в имени/пути")

    if TECH_WORD_RE.search(combined_meta):
        score += 8
        reasons.append("технические слова в имени/пути")

    if content_chars >= 10000:
        score += 12
        reasons.append("много извлечённого текста")
    elif content_chars >= 1000:
        score += 8
        reasons.append("есть извлечённый текст")
    elif 0 < content_chars < 50:
        score -= 6
        reasons.append("очень мало текста")

    if TECH_CODE_RE.search(content[:20000]):
        score += 8
        reasons.append("технический код в тексте")

    if TECH_WORD_RE.search(content[:20000]):
        score += 6
        reasons.append("технический текст")

    if extraction_status == "ok":
        score += 5
    elif extraction_status == "unsupported":
        score -= 8
        reasons.append("формат не поддерживается индексатором")
    elif extraction_status == "error":
        score -= 4
        reasons.append("ошибка извлечения")
    elif extraction_status in {"ocr_pending", "ocr_processing"}:
        reasons.append("OCR ещё не завершён")

    if size <= 0:
        score -= 25
        reasons.append("нулевой размер")
    elif size < 1024:
        score -= 15
        reasons.append("меньше 1 КБ")
    elif size < 4096:
        score -= 5
        reasons.append("очень маленький файл")

    if TEMP_NAME_RE.search(filename):
        score -= 28
        reasons.append("признаки временной/резервной копии")

    score = max(0, min(100, score))
    return score, _category_from_score(score), tuple(reasons), content_chars


def load_audit_records():
    conn = get_connection()
    try:
        rows = conn.execute(
            """
            SELECT
                id,
                filename,
                filepath,
                extension,
                COALESCE(size, 0),
                COALESCE(extraction_status, 'pending'),
                COALESCE(extraction_error, ''),
                COALESCE(is_cloud, 0),
                COALESCE(substr(content, 1, 20000), ''),
                length(trim(COALESCE(content, '')))
            FROM files
            ORDER BY filepath
            """
        ).fetchall()
    finally:
        conn.close()

    records = []

    for row in rows:
        (
            file_id,
            filename,
            filepath,
            extension,
            size,
            extraction_status,
            extraction_error,
            is_cloud,
            content_sample,
            content_chars,
        ) = row

        score, category, reasons, _sample_chars = _score_row(
            filename,
            filepath,
            extension,
            int(size or 0),
            extraction_status,
            content_sample,
        )

        records.append(
            AuditRecord(
                file_id=int(file_id),
                filename=filename or "",
                filepath=filepath or "",
                extension=(extension or "").lower(),
                size=int(size or 0),
                extraction_status=extraction_status or "pending",
                extraction_error=extraction_error or "",
                is_cloud=bool(is_cloud),
                content_chars=int(content_chars or 0),
                score=score,
                category=category,
                reasons=reasons,
                parent_folder=str(Path(filepath).parent) if filepath else "",
            )
        )

    return records


def summarize(records):
    counts = Counter(record.category for record in records)
    return {
        "total": len(records),
        "useful": counts.get("useful", 0),
        "review": counts.get("review", 0),
        "suspicious": counts.get("suspicious", 0),
    }


def _build_group_stats(groups):
    result = []

    for name, items in groups.items():
        counts = Counter(item.category for item in items)
        result.append(
            GroupStat(
                name=name,
                total=len(items),
                suspicious=counts.get("suspicious", 0),
                review=counts.get("review", 0),
                useful=counts.get("useful", 0),
            )
        )

    return result


def folder_stats(records, root=None, max_depth=2):
    root_path = Path(root).resolve() if root else None
    groups = defaultdict(list)

    for record in records:
        folder = Path(record.filepath).parent

        if root_path:
            try:
                relative = folder.resolve().relative_to(root_path)
                parts = relative.parts[:max_depth]
                key = str(Path(*parts)) if parts else "."
            except Exception:
                key = str(folder)
        else:
            parts = folder.parts
            key = str(Path(*parts[-max_depth:])) if parts else ""

        groups[key].append(record)

    result = _build_group_stats(groups)
    result.sort(
        key=lambda item: (
            -item.suspicious,
            -item.review,
            -item.total,
            item.name.lower(),
        )
    )
    return result


def extension_stats(records):
    groups = defaultdict(list)

    for record in records:
        groups[record.extension or "(без расширения)"].append(record)

    result = _build_group_stats(groups)
    result.sort(
        key=lambda item: (
            -item.total,
            -item.suspicious,
            item.name.lower(),
        )
    )
    return result


def duplicate_name_groups(records):
    groups = defaultdict(list)

    for record in records:
        key = record.filename.strip().casefold()
        if key:
            groups[key].append(record)

    result = [items for items in groups.values() if len(items) > 1]
    result.sort(
        key=lambda items: (
            -len(items),
            items[0].filename.casefold(),
        )
    )
    return result


def export_records_csv(records, destination):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)

    with destination.open("w", encoding="utf-8-sig", newline="") as file:
        writer = csv.writer(file, delimiter=";")
        writer.writerow(
            [
                "score",
                "category",
                "filename",
                "extension",
                "size",
                "status",
                "cloud",
                "content_chars",
                "reasons",
                "filepath",
            ]
        )

        for record in records:
            writer.writerow(
                [
                    record.score,
                    record.category,
                    record.filename,
                    record.extension,
                    record.size,
                    record.extraction_status,
                    int(record.is_cloud),
                    record.content_chars,
                    " | ".join(record.reasons),
                    record.filepath,
                ]
            )


def _safe_relative_path(filepath, library_root):
    source = Path(filepath).resolve()
    root = Path(library_root).resolve()

    try:
        return source.relative_to(root)
    except ValueError:
        drive = source.drive.replace(":", "") or "unknown_drive"
        parts = [
            part
            for part in source.parts
            if part not in {source.anchor, source.drive, "\\", "/"}
        ]
        return Path("_outside_root", drive, *parts)


def move_to_quarantine(records, library_root, quarantine_root, log_path=None):
    quarantine_root = Path(quarantine_root)
    moved = []
    skipped = []
    errors = []

    for record in records:
        source = Path(record.filepath)

        if record.is_cloud:
            skipped.append((record, "облачный файл — не перемещён автоматически"))
            continue

        if not source.is_file():
            skipped.append((record, "файл отсутствует локально"))
            continue

        relative = _safe_relative_path(source, library_root)
        destination = quarantine_root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)

        if destination.exists():
            stem = destination.stem
            suffix = destination.suffix
            counter = 2

            while destination.exists():
                destination = destination.with_name(f"{stem}__{counter}{suffix}")
                counter += 1

        try:
            shutil.move(str(source), str(destination))
            moved.append((record, str(destination)))
        except Exception as error:
            errors.append((record, str(error)))

    if log_path and (moved or skipped or errors):
        path = Path(log_path)
        path.parent.mkdir(parents=True, exist_ok=True)
        new_file = not path.exists()

        with path.open("a", encoding="utf-8-sig", newline="") as file:
            writer = csv.writer(file, delimiter=";")

            if new_file:
                writer.writerow(
                    ["result", "filename", "source", "destination_or_reason"]
                )

            for record, destination in moved:
                writer.writerow(
                    ["moved", record.filename, record.filepath, destination]
                )

            for record, reason in skipped:
                writer.writerow(
                    ["skipped", record.filename, record.filepath, reason]
                )

            for record, error in errors:
                writer.writerow(
                    ["error", record.filename, record.filepath, error]
                )

    return moved, skipped, errors
