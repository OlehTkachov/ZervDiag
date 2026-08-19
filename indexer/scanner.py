from pathlib import Path
import ctypes


# Windows / OneDrive file attributes.
FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400
FILE_ATTRIBUTE_OFFLINE = 0x00001000
FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000
FILE_ATTRIBUTE_PINNED = 0x00080000
FILE_ATTRIBUTE_UNPINNED = 0x00100000
FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000


# Эти форматы ZervDiag умеет читать/конвертировать/OCR.
# ВАЖНО: scan_folder() всё равно возвращает ВСЕ файлы.
EXTRACTABLE_EXTENSIONS = {
    ".pdf",
    ".doc",
    ".docx",
    ".xls",
    ".xlsx",
    ".odt",
    ".ods",
    ".txt",
    ".csv",
    ".json",
    ".jpg",
    ".jpeg",
    ".png",
    ".tif",
    ".tiff",
}


def get_file_attributes(path):
    attributes = ctypes.windll.kernel32.GetFileAttributesW(
        str(path)
    )

    if attributes == 0xFFFFFFFF:
        return 0

    return attributes


def is_cloud_file(path):
    """
    Определяет OneDrive / Files On-Demand файл
    без чтения его содержимого.

    ReparsePoint также считается cloud-managed,
    если файл не закреплён PINNED.
    """

    attributes = get_file_attributes(path)

    if attributes == 0:
        return False

    # "Всегда хранить на этом устройстве":
    # такой файл пользователь явно закрепил локально.
    if attributes & FILE_ATTRIBUTE_PINNED:
        return False

    return bool(
        attributes
        & (
            FILE_ATTRIBUTE_REPARSE_POINT
            | FILE_ATTRIBUTE_OFFLINE
            | FILE_ATTRIBUTE_UNPINNED
            | FILE_ATTRIBUTE_RECALL_ON_OPEN
            | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
        )
    )


def is_extractable_extension(extension):
    return (
        (extension or "").lower()
        in EXTRACTABLE_EXTENSIONS
    )


def scan_folder(folder):
    """
    Сканирует ВСЕ файлы.

    Неподдерживаемые форматы не выбрасываются:
    они попадут в SQLite по метаданным и смогут
    находиться по имени/пути.
    """

    folder = Path(folder)

    if not folder.exists():
        return []

    files = []

    for path in folder.rglob("*"):
        try:
            if not path.is_file():
                continue

            stat = path.stat()
            extension = path.suffix.lower()

            files.append(
                {
                    "filename": path.name,
                    "filepath": str(path),
                    "extension": extension,
                    "size": stat.st_size,
                    "modified": stat.st_mtime,
                    "is_cloud": is_cloud_file(path),
                    "extractable": is_extractable_extension(
                        extension
                    ),
                }
            )

        except OSError:
            pass

    return files
