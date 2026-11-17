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

from database.transfer import quick_check  # noqa: E402
from database.compare import compare_databases, load_content_diff  # noqa: E402
from ui.about_dialog import AboutDialog  # noqa: E402
from ui.database_compare_dialog import DatabaseCompareDialog  # noqa: E402
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
                "Database comparison navigation button is missing"
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
        "BETA DATABASE + BRAND + COMPARE SMOKE: ok",
        flush=True,
    )


if __name__ == "__main__":
    main()
