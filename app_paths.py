import os
import sys
from pathlib import Path


APP_NAME = "ZervDiag"
SOURCE_ROOT = Path(__file__).resolve().parent


def is_frozen():
    return bool(getattr(sys, "frozen", False))


def _local_app_data_root():
    value = os.environ.get("LOCALAPPDATA", "").strip()
    if value:
        return Path(value)

    # Windows fallback. Keeping it deterministic also makes diagnostics more
    # useful if LOCALAPPDATA is unexpectedly absent.
    return Path.home() / "AppData" / "Local"


if is_frozen():
    INSTALL_DIR = Path(sys.executable).resolve().parent
    APP_DATA_DIR = _local_app_data_root() / APP_NAME
else:
    # Source/development mode intentionally keeps the historical layout so
    # existing developer databases and OCR runs are not moved unexpectedly.
    INSTALL_DIR = SOURCE_ROOT
    APP_DATA_DIR = SOURCE_ROOT / "data"

DATA_DIR = APP_DATA_DIR
LOG_DIR = DATA_DIR / "logs"
DB_PATH = DATA_DIR / "zervdiag.db"
SCHEDULED_LOG_PATH = DATA_DIR / "scheduled_index.log"
WRITER_LOCK_PATH = DATA_DIR / ".zervdiag_writer.lock"
GUI_LOCK_PATH = DATA_DIR / ".zervdiag_gui.lock"


def ensure_runtime_dirs():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    LOG_DIR.mkdir(parents=True, exist_ok=True)


__all__ = [
    "APP_NAME",
    "SOURCE_ROOT",
    "INSTALL_DIR",
    "APP_DATA_DIR",
    "DATA_DIR",
    "LOG_DIR",
    "DB_PATH",
    "SCHEDULED_LOG_PATH",
    "WRITER_LOCK_PATH",
    "GUI_LOCK_PATH",
    "ensure_runtime_dirs",
    "is_frozen",
]
