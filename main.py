import sys

from PySide6.QtWidgets import QApplication

from ui.main_window import MainWindow
from ui.database_status import install_database_status


def main():
    app = QApplication(sys.argv)

    window = MainWindow()
    install_database_status(window)
    window.show()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
