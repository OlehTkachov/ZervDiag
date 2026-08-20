from dataclasses import asdict, dataclass
from datetime import datetime, timezone


BACKUP_FORMAT = "zervdiag-portable"
BACKUP_VERSION = 1
SUPPORTED_DATABASE_SCHEMA_MIN = 13


@dataclass(frozen=True)
class BackupManifest:
    """
    Контракт переносимой резервной копии.

    Сам backup в будущем будет ZIP-контейнером:
      manifest.json
      zervdiag.db

    current_root нужен настольной версии для восстановления путей,
    но мобильный клиент не обязан иметь доступ к этому каталогу.
    """

    format: str
    backup_version: int
    database_schema: int
    created_utc: str
    app_version: str
    language: str
    current_root: str
    database_filename: str = "zervdiag.db"

    def to_dict(self):
        return asdict(self)

    @classmethod
    def create(
        cls,
        database_schema,
        app_version="development",
        language="ru",
        current_root="",
    ):
        return cls(
            format=BACKUP_FORMAT,
            backup_version=BACKUP_VERSION,
            database_schema=int(database_schema),
            created_utc=datetime.now(timezone.utc).isoformat(),
            app_version=app_version,
            language=language,
            current_root=current_root or "",
        )


def validate_manifest(data):
    """
    Проверяет только совместимость формата, не доверяя имени файла.

    Возвращает (ok, message).
    """
    if not isinstance(data, dict):
        return False, "Manifest is not an object"

    if data.get("format") != BACKUP_FORMAT:
        return False, "Unknown ZervDiag backup format"

    try:
        backup_version = int(
            data.get("backup_version", 0)
        )
        database_schema = int(
            data.get("database_schema", 0)
        )
    except (TypeError, ValueError):
        return False, "Invalid backup version"

    if backup_version > BACKUP_VERSION:
        return (
            False,
            "Backup was created by a newer ZervDiag version",
        )

    if database_schema < SUPPORTED_DATABASE_SCHEMA_MIN:
        return (
            False,
            "Database schema is too old for direct mobile import",
        )

    if not data.get("database_filename"):
        return False, "Database file is not declared"

    return True, "OK"
