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
from ui.about_dialog import AboutDialog  # noqa: E402
from ui.branding import install_branding  # noqa: E402
from ui.main_window import MainWindow  # noqa: E402
from ui.settings_dialog import SettingsDialog  # noqa: E402


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

        if main_window.btn_about.property("role") != "about":
            raise RuntimeError(
                "About button branding role is missing"
            )

        if main_window.search_button.property("role") != "primary":
            raise RuntimeError(
                "Primary search action branding is missing"
            )

        if "#F59E0B" not in main_window.styleSheet():
            raise RuntimeError(
                "Commercial stylesheet was not applied"
            )

        main_window.close()

    app.processEvents()
    print(
        "BETA DATABASE + BRAND UI SMOKE: ok",
        flush=True,
    )


if __name__ == "__main__":
    main()
