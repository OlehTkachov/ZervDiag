from PySide6.QtCore import Qt
from PySide6.QtGui import QCursor, QFont, QIcon
from PySide6.QtWidgets import QApplication, QLabel, QPushButton

from app_paths import resource_path
from app_version import APP_VERSION, PRODUCT_TAGLINE
from ui.about_dialog import AboutDialog


COMMERCIAL_STYLE = r"""
QMainWindow, QDialog {
    background: #0B1F33;
    color: #E6ECF2;
}

QWidget {
    font-family: "Segoe UI";
    font-size: 10pt;
    color: #E6ECF2;
}

QLabel#brandTitle {
    color: #E6ECF2;
    font-size: 29px;
    font-weight: 700;
    padding: 2px 6px 0 6px;
}

QLabel#brandTagline {
    color: #9FB5C7;
    font-size: 9pt;
    padding: 0 6px 2px 6px;
}

QLabel#brandVersion {
    color: #00BCD4;
    font-size: 8pt;
    font-weight: 700;
    padding: 0 6px 14px 6px;
}

QPushButton {
    min-height: 34px;
    border: 1px solid #29465E;
    border-radius: 7px;
    padding: 5px 12px;
    background: #102A41;
    color: #E6ECF2;
    font-weight: 600;
}

QPushButton:hover {
    border-color: #3B6886;
    background: #173A5C;
}

QPushButton:pressed {
    background: #0E2438;
}

QPushButton:disabled {
    color: #667B8D;
    background: #0D2337;
    border-color: #203A50;
}

QPushButton[role="nav"] {
    min-width: 184px;
    min-height: 40px;
    text-align: left;
    padding-left: 14px;
    border-color: transparent;
    background: transparent;
    color: #DCE6EE;
}

QPushButton[role="nav"]:hover {
    background: #12314B;
    border-color: #1B4B68;
    color: #FFFFFF;
}

QPushButton[role="nav"]:pressed {
    background: #173A5C;
    border-color: #00BCD4;
}

QPushButton[role="nav"]:disabled {
    color: #5F7486;
    background: transparent;
    border-color: transparent;
}

QPushButton[role="about"] {
    min-width: 184px;
    text-align: left;
    padding-left: 14px;
    color: #8299AA;
    background: transparent;
    border-color: transparent;
}

QPushButton[role="about"]:hover {
    color: #DDE9F1;
    background: #102B43;
}

QPushButton[role="primary"] {
    min-width: 110px;
    min-height: 40px;
    background: #00BCD4;
    border-color: #21CBE1;
    color: #071A28;
    font-weight: 700;
}

QPushButton[role="primary"]:hover {
    background: #21CBE1;
    border-color: #55D9EA;
}

QPushButton[role="primary"]:pressed {
    background: #00A8BE;
    border-color: #00A8BE;
}

QLineEdit {
    min-height: 40px;
    border: 1px solid #29465E;
    border-radius: 8px;
    padding: 0 12px;
    background: #0F2A41;
    color: #E6ECF2;
    selection-background-color: #00BCD4;
    selection-color: #071A28;
}

QLineEdit:focus {
    border: 2px solid #00BCD4;
    padding: 0 11px;
}

QLineEdit::placeholder {
    color: #71899B;
}

QTableWidget {
    background: #0E263B;
    alternate-background-color: #102B43;
    border: 1px solid #24445B;
    border-radius: 8px;
    gridline-color: #1A384E;
    selection-background-color: #173A5C;
    selection-color: #FFFFFF;
}

QTableWidget::item {
    padding: 3px 6px;
}

QTableWidget::item:selected {
    border-left: 2px solid #00BCD4;
}

QHeaderView::section {
    background: #132F48;
    color: #BFD0DC;
    border: none;
    border-right: 1px solid #203E55;
    border-bottom: 1px solid #29465E;
    padding: 8px 9px;
    font-weight: 700;
}

QLabel#documentationPath {
    background: #0F2A41;
    border: 1px solid #24445B;
    border-radius: 7px;
    padding: 8px 10px;
    color: #AFC1CE;
}

QLabel#statusLabel {
    background: #102B43;
    border: 1px solid #1D3C53;
    border-radius: 7px;
    padding: 8px 10px;
    color: #AFC1CE;
}

QGroupBox {
    font-weight: 700;
    border: 1px solid #24445B;
    border-radius: 8px;
    margin-top: 12px;
    padding-top: 12px;
    background: #0E263B;
}

QGroupBox::title {
    subcontrol-origin: margin;
    left: 12px;
    padding: 0 5px;
    color: #BFD0DC;
}

QComboBox, QSpinBox, QTimeEdit {
    min-height: 30px;
    border: 1px solid #29465E;
    border-radius: 6px;
    background: #0F2A41;
    color: #E6ECF2;
    padding: 2px 7px;
}

QComboBox QAbstractItemView {
    background: #102A41;
    color: #E6ECF2;
    border: 1px solid #29465E;
    selection-background-color: #173A5C;
    selection-color: #FFFFFF;
}

QCheckBox {
    spacing: 7px;
}

QToolTip {
    background: #071A28;
    color: #E6ECF2;
    border: 1px solid #2A526C;
    padding: 5px 7px;
}

QScrollBar:vertical {
    background: #0B1F33;
    width: 12px;
    margin: 0;
}

QScrollBar::handle:vertical {
    background: #31556E;
    min-height: 28px;
    border-radius: 6px;
}

QScrollBar::handle:vertical:hover {
    background: #3F6A87;
}

QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
    height: 0;
}

QScrollBar:horizontal {
    background: #0B1F33;
    height: 12px;
    margin: 0;
}

QScrollBar::handle:horizontal {
    background: #31556E;
    min-width: 28px;
    border-radius: 6px;
}

QDialog#aboutDialog {
    background: #0B1F33;
}

QLabel#aboutProduct {
    font-size: 30px;
    font-weight: 700;
    color: #F4F8FB;
}

QLabel#aboutTagline {
    color: #00BCD4;
    font-size: 11pt;
    font-weight: 700;
}

QLabel#aboutEdition {
    color: #8EA6B7;
}

QLabel#aboutBody {
    background: #0E263B;
    border: 1px solid #24445B;
    border-radius: 8px;
    padding: 12px;
    color: #D5E0E8;
}

QLabel#aboutNote {
    background: #102E47;
    border: 1px solid #1D5873;
    border-radius: 8px;
    padding: 10px 12px;
    color: #AFDCE5;
}

QLabel#aboutDetails, QLabel#aboutCopyright {
    color: #8EA6B7;
}
"""


def _pointing_cursor(widget):
    widget.setCursor(QCursor(Qt.PointingHandCursor))


def install_branding(main_window):
    """Apply the approved ZervDiag visual layer without changing app logic."""
    if getattr(main_window, "_commercial_branding_installed", False):
        return

    main_window._commercial_branding_installed = True
    main_window.setWindowTitle(f"ZervDiag · {APP_VERSION}")

    app = QApplication.instance()
    if app is not None:
        app.setFont(QFont("Segoe UI", 10))

    icon_path = resource_path("assets", "zervdiag.ico")
    if not icon_path.exists():
        icon_path = resource_path("assets", "zervdiag_mark.svg")

    icon = QIcon(str(icon_path)) if icon_path.exists() else QIcon()
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
            title.setText('Zerv<span style="color:#00BCD4">Diag</span>')
            title.setTextFormat(Qt.RichText)
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
