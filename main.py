import sys

from PySide6.QtWidgets import QApplication

from diagnostics.black_box import (
    install_black_box,
    install_runtime_event_logging,
)


def main():
    app = QApplication(sys.argv)
    install_black_box(app)

    # Импортируем остальную программу после установки аварийных hooks:
    # если ошибка случится уже на этапе загрузки UI-модулей, traceback
    # всё равно попадёт в data/logs/crash.log.
    from ui.main_window import MainWindow
    from ui.database_status import install_database_status
    from ui.table_resizing import install_manual_table_resizing
    from ui.ui_enhancements import install_ui_enhancements
    from ui.auto_indexing import install_auto_index_settings
    from ui.localization import install_localization
    from scheduler.windows_task import install_windows_scheduler

    window = MainWindow()
    install_database_status(window)
    install_manual_table_resizing(window)
    install_ui_enhancements(window)
    install_auto_index_settings(window)
    install_localization(window)
    install_windows_scheduler(window)
    install_runtime_event_logging(window)

    # Запускаем развёрнутым окном: весь рабочий экран,
    # но с обычной рамкой Windows и панелью задач.
    window.showMaximized()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
