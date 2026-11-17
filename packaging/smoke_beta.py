import os
import sqlite3
import sys
import tempfile
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

os.environ.setdefault(
    "QT_QPA_PLATFORM",
    "offscreen",
)

from PySide6.QtCore import QSettings  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

import database.db as database_db  # noqa: E402
import database.libraries as library_manager  # noqa: E402
from database.transfer import quick_check  # noqa: E402
from database.compare import compare_databases, load_content_diff  # noqa: E402
from ui.about_dialog import AboutDialog  # noqa: E402
from ui.database_compare_dialog import DatabaseCompareDialog  # noqa: E402
from ui.library_compare_dialog import LibraryCompareDialog  # noqa: E402
from ui.branding import install_branding  # noqa: E402
from ui.main_window import MainWindow  # noqa: E402
from ui.settings_dialog import SettingsDialog  # noqa: E402


def _create_compare_database(path, rows):
    connection = sqlite3.connect(path)
    try:
        connection.execute(
            """
            CREATE TABLE files (
                id INTEGER PRIMARY KEY,
                filename TEXT NOT NULL,
                filepath TEXT NOT NULL UNIQUE,
                extension TEXT,
                size INTEGER,
                modified REAL,
                content TEXT,
                file_hash TEXT,
                is_cloud INTEGER DEFAULT 0,
                extraction_status TEXT DEFAULT 'pending',
                extraction_error TEXT
            )
            """
        )
        connection.executemany(
            """
            INSERT INTO files (
                id, filename, filepath, extension, size, modified,
                content, extraction_status
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            rows,
        )
        connection.commit()
    finally:
        connection.close()


def _run_database_compare_smoke(root):
    left_db = root / "library_a.db"
    right_db = root / "library_b.db"

    _create_compare_database(
        left_db,
        [
            (
                1,
                "ONK-160.pdf",
                r"C:\LibraryA\manuals\crane\ONK-160.pdf",
                ".pdf",
                100,
                1000.0,
                "E10 controller error\nCause: sensor A\n",
                "ok",
            ),
            (
                2,
                "HC4900.pdf",
                r"C:\LibraryA\manuals\crane\HC4900.pdf",
                ".pdf",
                200,
                1000.0,
                "Hirschmann HC4900 technical manual\n",
                "ok",
            ),
            (
                3,
                "OLD.pdf",
                r"C:\LibraryA\manuals\archive\OLD.pdf",
                ".pdf",
                300,
                1000.0,
                "Old archived document content\n",
                "ok",
            ),
        ],
    )

    _create_compare_database(
        right_db,
        [
            (
                11,
                "ONK-160.pdf",
                r"D:\LibraryB\manuals\crane\ONK-160.pdf",
                ".pdf",
                105,
                2000.0,
                "E10 controller error\nCause: sensor B\n",
                "ok",
            ),
            (
                12,
                "HC4900.pdf",
                r"D:\LibraryB\manuals\crane\HC4900.pdf",
                ".pdf",
                200,
                1000.0,
                "Hirschmann HC4900 technical manual\n",
                "ok",
            ),
            (
                13,
                "NEW.pdf",
                r"D:\LibraryB\manuals\archive\NEW.pdf",
                ".pdf",
                400,
                2000.0,
                "New document content\n",
                "ok",
            ),
        ],
    )

    result = compare_databases(left_db, right_db)
    summary = result["summary"]

    expected = {
        "content_changed": 1,
        "identical": 1,
        "only_left": 1,
        "only_right": 1,
    }

    for key, expected_count in expected.items():
        if summary.get(key) != expected_count:
            raise RuntimeError(
                "Database comparison smoke failed: "
                f"{key}={summary.get(key)}, expected {expected_count}"
            )

    changed = next(
        item
        for item in result["items"]
        if item["kind"] == "content_changed"
    )

    diff = load_content_diff(
        left_db,
        right_db,
        changed["left"]["id"],
        changed["right"]["id"],
        left_name=changed["left"]["filename"],
        right_name=changed["right"]["filename"],
    )

    if "sensor A" not in diff or "sensor B" not in diff:
        raise RuntimeError(
            "Database comparison text diff did not preserve both versions"
        )

    for path in (left_db, right_db):
        ok, message = quick_check(path)
        if not ok:
            raise RuntimeError(
                f"Database comparison changed/corrupted {path.name}: {message}"
            )


def _run_multiple_library_smoke(root):
    """Prove that two registered folders use isolated SQLite databases."""
    old_registry = library_manager.REGISTRY_PATH
    old_libraries_dir = library_manager.LIBRARIES_DIR
    old_default_db = library_manager.DEFAULT_DB_PATH
    old_data_dir = database_db.DATA_DIR

    try:
        test_data = root / "multi_library_data"
        library_manager.REGISTRY_PATH = test_data / "libraries.json"
        library_manager.LIBRARIES_DIR = test_data / "libraries"
        library_manager.DEFAULT_DB_PATH = test_data / "zervdiag.db"
        database_db.DATA_DIR = test_data

        legacy_folder = root / "legacy_library"
        folder_a = root / "documents_a"
        folder_b = root / "documents_b"
        legacy_folder.mkdir()
        folder_a.mkdir()
        folder_b.mkdir()

        # Bootstrap must keep the historical database as the default library.
        _create_compare_database(
            library_manager.DEFAULT_DB_PATH,
            [
                (
                    1,
                    "legacy.txt",
                    str(legacy_folder / "legacy.txt"),
                    ".txt",
                    10,
                    1.0,
                    "Legacy database content\n",
                    "ok",
                )
            ],
        )
        library_manager.ensure_library_registry(
            legacy_folder=str(legacy_folder)
        )
        if library_manager.get_active_database_path() != library_manager.DEFAULT_DB_PATH.resolve():
            raise RuntimeError("Legacy database was not kept as the default library")

        legacy_connection = sqlite3.connect(library_manager.DEFAULT_DB_PATH)
        try:
            if legacy_connection.execute("SELECT COUNT(*) FROM files").fetchone()[0] != 1:
                raise RuntimeError("Legacy library data was not preserved")
        finally:
            legacy_connection.close()

        library_a = library_manager.add_library("Библиотека A", str(folder_a))
        library_b = library_manager.add_library("Библиотека B", str(folder_b))
        database_db.create_database(library_a["database_path"])
        database_db.create_database(library_b["database_path"])

        library_manager.set_active_library(library_a["id"])
        connection = database_db.get_connection()
        try:
            connection.execute(
                """
                INSERT INTO files (
                    filename, filepath, extension, size, modified,
                    content, extraction_status
                )
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    "A-only.txt",
                    str(folder_a / "A-only.txt"),
                    ".txt",
                    12,
                    2.0,
                    "Document belonging only to library A\n",
                    "ok",
                ),
            )
            connection.commit()
        finally:
            connection.close()

        library_manager.set_active_library(library_b["id"])
        connection = database_db.get_connection()
        try:
            connection.execute(
                """
                INSERT INTO files (
                    filename, filepath, extension, size, modified,
                    content, extraction_status
                )
                VALUES (?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    "B-only.txt",
                    str(folder_b / "B-only.txt"),
                    ".txt",
                    13,
                    3.0,
                    "Document belonging only to library B\n",
                    "ok",
                ),
            )
            connection.commit()
            if connection.execute("SELECT COUNT(*) FROM files").fetchone()[0] != 1:
                raise RuntimeError("Library B contains records from another library")
        finally:
            connection.close()

        library_manager.set_active_library(library_a["id"])
        connection = database_db.get_connection()
        try:
            if connection.execute("SELECT COUNT(*) FROM files").fetchone()[0] != 1:
                raise RuntimeError("Library A contains records from another library")
        finally:
            connection.close()

        comparison = compare_databases(
            library_a["database_path"],
            library_b["database_path"],
        )
        summary = comparison["summary"]
        if summary.get("only_left") != 1 or summary.get("only_right") != 1:
            raise RuntimeError(
                "Separate library comparison did not isolate A-only and B-only documents"
            )
    finally:
        library_manager.REGISTRY_PATH = old_registry
        library_manager.LIBRARIES_DIR = old_libraries_dir
        library_manager.DEFAULT_DB_PATH = old_default_db
        database_db.DATA_DIR = old_data_dir


def main():
    app = QApplication.instance() or QApplication([])

    with tempfile.TemporaryDirectory(
        prefix="zervdiag_beta_smoke_"
    ) as directory:
        root = Path(directory)
        test_db = root / "smoke.db"

        conn = sqlite3.connect(test_db)
        try:
            conn.execute(
                """
                CREATE TABLE files (
                    id INTEGER PRIMARY KEY,
                    filename TEXT,
                    filepath TEXT
                )
                """
            )
            conn.execute(
                "INSERT INTO files (filename, filepath) VALUES (?, ?)",
                ("smoke.txt", r"C:\smoke\smoke.txt"),
            )
            conn.commit()
        finally:
            conn.close()

        ok, message = quick_check(test_db)
        if not ok:
            raise RuntimeError(
                "Database transfer QUICK_CHECK smoke failed: "
                f"{message}"
            )

        _run_database_compare_smoke(root)
        _run_multiple_library_smoke(root)

        settings = QSettings(
            str(root / "settings.ini"),
            QSettings.Format.IniFormat,
        )
        dialog = SettingsDialog(settings)

        if not dialog.import_database_button.text().strip():
            raise RuntimeError(
                "Import database button is missing"
            )

        if not dialog.export_database_button.text().strip():
            raise RuntimeError(
                "Export database button is missing"
            )

        if dialog.restart_requested:
            raise RuntimeError(
                "Settings dialog unexpectedly requests restart"
            )

        dialog.close()

        compare_dialog = DatabaseCompareDialog()
        if not compare_dialog.compare_button.text().strip():
            raise RuntimeError(
                "Database comparison action is missing"
            )
        compare_dialog.close()

        library_compare_dialog = LibraryCompareDialog()
        if not library_compare_dialog.compare_button.text().strip():
            raise RuntimeError(
                "Library comparison action is missing"
            )
        library_compare_dialog.close()

        about = AboutDialog(settings)
        if "ZervDiag" not in about.windowTitle():
            raise RuntimeError(
                "About dialog branding is missing"
            )
        about.close()

        main_window = MainWindow()
        install_branding(main_window)

        if not hasattr(main_window, "btn_about"):
            raise RuntimeError(
                "Commercial About button is missing"
            )

        if not hasattr(main_window, "btn_compare_databases"):
            raise RuntimeError(
                "Library comparison navigation button is missing"
            )
        if not hasattr(main_window, "library_combo") or not hasattr(main_window, "btn_add_library"):
            raise RuntimeError(
                "Multiple-library controls are missing"
            )

        if main_window.btn_about.property("role") != "about":
            raise RuntimeError(
                "About button branding role is missing"
            )

        if main_window.search_button.property("role") != "primary":
            raise RuntimeError(
                "Primary search action branding is missing"
            )

        if main_window.btn_compare_databases.property("role") != "nav":
            raise RuntimeError(
                "Database comparison navigation branding is missing"
            )

        style = main_window.styleSheet()
        if "#00BCD4" not in style or "#0B1F33" not in style:
            raise RuntimeError(
                "Approved dark/cyan commercial stylesheet was not applied"
            )

        main_window.close()

    app.processEvents()
    print(
        "BETA DATABASE + BRAND + MULTI-LIBRARY SMOKE: ok",
        flush=True,
    )


if __name__ == "__main__":
    main()
