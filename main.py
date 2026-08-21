import sys

from PySide6.QtWidgets import QApplication

from ui.main_window import MainWindow
from ui.database_status import install_database_status
from ui.table_resizing import install_manual_table_resizing
from ui.ui_enhancements import install_ui_enhancements
from ui.auto_indexing import install_auto_index_settings
from ui.localization import install_localization


def main():
    app = QApplication(sys.argv)

    window = MainWindow()
    install_database_status(window)
    install_manual_table_resizing(window)
    install_ui_enhancements(window)
    install_auto_index_settings(window)
    install_localization(window)

    # Запускаем развёрнутым окном: весь рабочий экран,
    # но с обычной рамкой Windows и панелью задач.
    window.showMaximized()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
