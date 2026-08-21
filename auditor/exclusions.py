import json
import os
from pathlib import Path


STATE_PATH = (
    Path(__file__).resolve().parent.parent
    / "data"
    / "library_auditor_exclusions.json"
)


def _key(path):
    value = os.path.normpath(str(path or "")).replace("\\", "/").rstrip("/")
    return value.casefold()


class ExclusionStore:
    """Persistent user-approved files and folders for Library Auditor."""

    def __init__(self, path=STATE_PATH):
        self.path = Path(path)
        self.files = {}
        self.folders = {}
        self.reload()

    def reload(self):
        self.files = {}
        self.folders = {}

        if not self.path.is_file():
            return

        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except Exception:
            return

        for value in data.get("files", []):
            key = _key(value)
            if key:
                self.files[key] = str(value)

        for value in data.get("folders", []):
            key = _key(value)
            if key:
                self.folders[key] = str(value)

    def _save(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "version": 1,
            "files": sorted(self.files.values(), key=str.casefold),
            "folders": sorted(self.folders.values(), key=str.casefold),
        }

        temporary = self.path.with_suffix(self.path.suffix + ".tmp")
        temporary.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        temporary.replace(self.path)

    def add_file(self, filepath):
        value = os.path.normpath(str(filepath))
        key = _key(value)
        if key:
            self.files[key] = value
            self._save()

    def add_folder(self, folder):
        value = os.path.normpath(str(folder))
        key = _key(value)
        if key:
            self.folders[key] = value
            self._save()

    def match(self, filepath):
        """Return ('file'|'folder', stored_path) when filepath is excluded."""
        file_key = _key(filepath)
        if not file_key:
            return None

        if file_key in self.files:
            return "file", self.files[file_key]

        matches = []
        for folder_key, original in self.folders.items():
            if file_key == folder_key or file_key.startswith(folder_key + "/"):
                matches.append((len(folder_key), original))

        if not matches:
            return None

        _length, original = max(matches, key=lambda item: item[0])
        return "folder", original

    def remove(self, kind, path):
        key = _key(path)

        if kind == "file":
            removed = self.files.pop(key, None)
        elif kind == "folder":
            removed = self.folders.pop(key, None)
        else:
            removed = None

        if removed is not None:
            self._save()
            return True

        return False

    def rule_counts(self):
        return len(self.files), len(self.folders)
