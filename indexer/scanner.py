from pathlib import Path


def scan_folder(folder):
    folder = Path(folder)

    if not folder.exists():
        return []

    files = []

    for path in folder.rglob("*"):
        if path.is_file():
            try:
                files.append({
                    "filename": path.name,
                    "filepath": str(path),
                    "extension": path.suffix.lower(),
                    "size": path.stat().st_size,
                    "modified": path.stat().st_mtime,
                })
            except OSError:
                pass

    return files