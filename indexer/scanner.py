from pathlib import Path
import ctypes


# Атрибуты Windows Cloud Files / OneDrive
FILE_ATTRIBUTE_OFFLINE = 0x00001000
FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000
FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000


def get_file_attributes(path):
    """
    Получает атрибуты файла Windows без открытия его содержимого.
    Это важно для OneDrive: чтение файла может вызвать его скачивание.
    """

    attributes = ctypes.windll.kernel32.GetFileAttributesW(str(path))

    if attributes == 0xFFFFFFFF:
        return 0

    return attributes


def is_cloud_file(path):
    """
    Определяет, является ли файл облачным/неполностью локальным.

    GetFileAttributesW не открывает содержимое файла,
    поэтому OneDrive не должен скачивать его только ради проверки.
    """

    attributes = get_file_attributes(path)

    if attributes == 0:
        return False

    return bool(
        attributes
        & (
            FILE_ATTRIBUTE_OFFLINE
            | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
            | FILE_ATTRIBUTE_RECALL_ON_OPEN
        )
    )


def scan_folder(folder):
    folder = Path(folder)

    if not folder.exists():
        return []

    files = []

    for path in folder.rglob("*"):

        if not path.is_file():
            continue

        try:
            stat = path.stat()

            cloud = is_cloud_file(path)

            files.append({
                "filename": path.name,
                "filepath": str(path),
                "extension": path.suffix.lower(),
                "size": stat.st_size,
                "modified": stat.st_mtime,
                "is_cloud": cloud,
            })

        except OSError:
            pass

    return files