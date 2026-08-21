import sys

from PySide6.QtWidgets import QApplication

from app_version import APP_VERSION
from beta_runtime import (
    configure_black_box_paths,
    configure_packaged_scheduler,
)


# In an installed build black-box logs must be redirected before the logger is
# initialized. Source mode is intentionally left unchanged.
configure_black_box_paths()

from diagnostics.black_box import (  # noqa: E402
    install_black_box,
    install_runtime_event_logging,
)


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("ZervDiag")
    app.setApplicationVersion(APP_VERSION)
    app.setOrganizationName("ZervDiag")

    install_black_box(app)

    # Импортируем остальную программу после установки аварийных hooks:
    # если ошибка случится уже на этапе загрузки UI-модулей, traceback
    # всё равно попадёт в crash.log.
    from ui.first_run import ensure_database_ready

    if not ensure_database_ready():
        return

    from ui.main_window import MainWindow
    from ui.database_status import install_database_status
    from ui.table_resizing import install_manual_table_resizing
    from ui.ui_enhancements import install_ui_enhancements
    from ui.auto_indexing import install_auto_index_settings
    from ui.localization import install_localization
    from scheduler import windows_task
    from ui.assistant_integration import install_ai_assistant
    from ui.component_status import install_beta_component_check

    configure_packaged_scheduler(windows_task)

    window = MainWindow()
    install_database_status(window)
    install_manual_table_resizing(window)
    install_ui_enhancements(window)
    install_auto_index_settings(window)
    install_localization(window)
    windows_task.install_windows_scheduler(window)
    install_ai_assistant(window)
    install_runtime_event_logging(window)
    install_beta_component_check(window)

    # Запускаем развёрнутым окном: весь рабочий экран,
    # но с обычной рамкой Windows и панелью задач.
    window.showMaximized()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
