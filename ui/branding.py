from PySide6.QtCore import Qt
from PySide6.QtGui import QCursor, QFont, QIcon
from PySide6.QtWidgets import QApplication, QLabel, QPushButton

from app_paths import resource_path
from app_version import APP_VERSION, PRODUCT_TAGLINE
from ui.about_dialog import AboutDialog


COMMERCIAL_STYLE = r"""
QMainWindow, QDialog {
    background: #F4F6F8;
    color: #17202A;
}

QWidget {
    font-family: "Segoe UI";
    font-size: 10pt;
    color: #17202A;
}

QLabel#brandTitle {
    color: #111923;
    font-size: 27px;
    font-weight: 700;
    padding: 2px 6px 0 6px;
}

QLabel#brandTagline {
    color: #66717D;
    font-size: 9pt;
    padding: 0 6px 2px 6px;
}

QLabel#brandVersion {
    color: #A66000;
    font-size: 8pt;
    font-weight: 700;
    padding: 0 6px 14px 6px;
}

QPushButton {
    min-height: 34px;
    border: 1px solid #CBD2D9;
    border-radius: 7px;
    padding: 5px 12px;
    background: #FFFFFF;
    color: #202A34;
    font-weight: 600;
}

QPushButton:hover {
    border-color: #9BA7B3;
    background: #F8FAFB;
}

QPushButton:pressed {
    background: #E9EDF1;
}

QPushButton:disabled {
    color: #9AA4AE;
    background: #EEF1F4;
    border-color: #D8DDE2;
}

QPushButton[role="nav"] {
    min-width: 174px;
    min-height: 38px;
    text-align: left;
    padding-left: 14px;
    border-color: #2B3743;
    background: #17212B;
    color: #F2F5F7;
}

QPushButton[role="nav"]:hover {
    background: #22303D;
    border-color: #3B4C5C;
}

QPushButton[role="nav"]:pressed {
    background: #101820;
}

QPushButton[role="about"] {
    min-width: 174px;
    text-align: left;
    padding-left: 14px;
    color: #69747F;
    background: transparent;
    border-color: transparent;
}

QPushButton[role="about"]:hover {
    color: #202A34;
    background: #E8EDF1;
}

QPushButton[role="primary"] {
    min-width: 104px;
    min-height: 40px;
    background: #F59E0B;
    border-color: #D88905;
    color: #111923;
    font-weight: 700;
}

QPushButton[role="primary"]:hover {
    background: #FFAA18;
    border-color: #C97D04;
}

QLineEdit {
    min-height: 40px;
    border: 1px solid #C7CED5;
    border-radius: 8px;
    padding: 0 12px;
    background: #FFFFFF;
    selection-background-color: #F59E0B;
    selection-color: #111923;
}

QLineEdit:focus {
    border: 2px solid #F0A020;
    padding: 0 11px;
}

QTableWidget {
    background: #FFFFFF;
    alternate-background-color: #F7F9FA;
    border: 1px solid #D8DEE4;
    border-radius: 8px;
    gridline-color: #E5E9ED;
    selection-background-color: #FFF1D6;
    selection-color: #17202A;
}

QHeaderView::section {
    background: #E9EDF1;
    color: #26323D;
    border: none;
    border-right: 1px solid #D1D7DD;
    border-bottom: 1px solid #CBD2D9;
    padding: 8px 9px;
    font-weight: 700;
}

QLabel#documentationPath {
    background: #FFFFFF;
    border: 1px solid #D8DEE4;
    border-radius: 7px;
    padding: 8px 10px;
    color: #45515D;
}

QLabel#statusLabel {
    background: #E9EDF1;
    border-radius: 7px;
    padding: 8px 10px;
    color: #44515D;
}

QGroupBox {
    font-weight: 700;
    border: 1px solid #D5DBE1;
    border-radius: 8px;
    margin-top: 12px;
    padding-top: 12px;
    background: #FAFBFC;
}

QGroupBox::title {
    subcontrol-origin: margin;
    left: 12px;
    padding: 0 5px;
}

QComboBox, QSpinBox, QTimeEdit {
    min-height: 30px;
    border: 1px solid #C7CED5;
    border-radius: 6px;
    background: #FFFFFF;
    padding: 2px 7px;
}

QCheckBox {
    spacing: 7px;
}

QDialog#aboutDialog {
    background: #F7F8FA;
}

QLabel#aboutProduct {
    font-size: 30px;
    font-weight: 700;
    color: #111923;
}

QLabel#aboutTagline {
    color: #A66000;
    font-size: 11pt;
    font-weight: 700;
}

QLabel#aboutEdition {
    color: #65717D;
}

QLabel#aboutBody {
    background: #FFFFFF;
    border: 1px solid #DCE1E6;
    border-radius: 8px;
    padding: 12px;
}

QLabel#aboutNote {
    background: #FFF4DD;
    border: 1px solid #F1D29A;
    border-radius: 8px;
    padding: 10px 12px;
    color: #5C430F;
}

QLabel#aboutDetails, QLabel#aboutCopyright {
    color: #66717D;
}
"""


def _pointing_cursor(widget):
    widget.setCursor(QCursor(Qt.PointingHandCursor))


def install_branding(main_window):
    """Apply the commercial visual layer without changing application logic."""
    if getattr(main_window, "_commercial_branding_installed", False):
        return

    main_window._commercial_branding_installed = True

    app = QApplication.instance()
    if app is not None:
        app.setFont(QFont("Segoe UI", 10))

    icon = QIcon(str(resource_path("assets", "zervdiag.ico")))
    if not icon.isNull():
        main_window.setWindowIcon(icon)
        if app is not None:
            app.setWindowIcon(icon)

    central = main_window.centralWidget()
    if central is not None:
        central.setObjectName("zervdiagWorkspace")

    main_layout = central.layout() if central is not None else None
    left_layout = None
    right_layout = None

    if main_layout is not None:
        main_layout.setContentsMargins(18, 18, 18, 16)
        main_layout.setSpacing(22)

        left_item = main_layout.itemAt(0)
        right_item = main_layout.itemAt(1)
        left_layout = left_item.layout() if left_item else None
        right_layout = right_item.layout() if right_item else None

    if left_layout is not None:
        left_layout.setSpacing(8)

        title_item = left_layout.itemAt(0)
        title = title_item.widget() if title_item else None
        if isinstance(title, QLabel):
            title.setObjectName("brandTitle")
            title.setText("ZervDiag")
            title.setStyleSheet("")

            tagline = QLabel(PRODUCT_TAGLINE)
            tagline.setObjectName("brandTagline")
            tagline.setWordWrap(True)
            left_layout.insertWidget(1, tagline)

            version = QLabel(f"BETA · {APP_VERSION}")
            version.setObjectName("brandVersion")
            left_layout.insertWidget(2, version)

        about_button = QPushButton("ⓘ  ZervDiag")
        about_button.setProperty("role", "about")
        _pointing_cursor(about_button)
        left_layout.insertWidget(max(0, left_layout.count() - 1), about_button)
        main_window.btn_about = about_button

        def show_about():
            AboutDialog(main_window.settings, main_window).exec()

        about_button.clicked.connect(show_about)

    for button in (
        getattr(main_window, "btn_search", None),
        getattr(main_window, "btn_index", None),
        getattr(main_window, "btn_ocr", None),
        getattr(main_window, "btn_folder", None),
        getattr(main_window, "btn_duplicates", None),
        getattr(main_window, "btn_settings", None),
        getattr(main_window, "btn_db_status", None),
        getattr(main_window, "btn_ai_assistant", None),
    ):
        if isinstance(button, QPushButton):
            button.setProperty("role", "nav")
            _pointing_cursor(button)

    if hasattr(main_window, "search_button"):
        main_window.search_button.setProperty("role", "primary")
        _pointing_cursor(main_window.search_button)

    if hasattr(main_window, "search_input"):
        main_window.search_input.setClearButtonEnabled(True)

    if hasattr(main_window, "folder_label"):
        main_window.folder_label.setObjectName("documentationPath")
        main_window.folder_label.setTextInteractionFlags(Qt.TextSelectableByMouse)

    if hasattr(main_window, "status"):
        main_window.status.setObjectName("statusLabel")

    if hasattr(main_window, "results"):
        main_window.results.setAlternatingRowColors(True)
        main_window.results.verticalHeader().setVisible(False)
        main_window.results.verticalHeader().setDefaultSectionSize(32)
        main_window.results.setShowGrid(False)

    if right_layout is not None:
        right_layout.setSpacing(10)

    main_window.setStyleSheet(COMMERCIAL_STYLE)
