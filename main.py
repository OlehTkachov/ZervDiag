import sys

from PySide6.QtWidgets import QApplication

from ui.main_window import MainWindow
from ui.database_status import install_database_status
from ui.table_resizing import install_manual_table_resizing


def main():
    app = QApplication(sys.argv)

    window = MainWindow()
    install_database_status(window)
    install_manual_table_resizing(window)
    window.show()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
