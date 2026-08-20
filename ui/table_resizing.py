from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QHeaderView


MAIN_WIDTHS_KEY = "table_widths/main_results"
STATUS_WIDTHS_KEY = "table_widths/database_status"


def _parse_widths(value):
    if not value:
        return []

    try:
        return [
            max(40, int(part))
            for part in str(value).split(",")
            if str(part).strip()
        ]
    except (TypeError, ValueError):
        return []


def _save_widths(table, settings, key):
    widths = [
        str(table.columnWidth(column))
        for column in range(table.columnCount())
    ]
    settings.setValue(key, ",".join(widths))


def make_columns_interactive(table, settings, key):
    """Разрешает менять ширину столбцов мышью и запоминает её."""
    if getattr(table, "_zervdiag_manual_widths", False):
        return

    header = table.horizontalHeader()

    # Важно: Stretch/ResizeToContents не дают нормально тянуть
    # границы столбцов вручную. Interactive работает как в Excel.
    header.setStretchLastSection(False)
    header.setMinimumSectionSize(40)
    header.setSectionResizeMode(QHeaderView.Interactive)

    saved = _parse_widths(settings.value(key, ""))
    if saved:
        header.blockSignals(True)
        try:
            for column, width in enumerate(saved[: table.columnCount()]):
                table.setColumnWidth(column, width)
        finally:
            header.blockSignals(False)

    def remember_widths(*_args):
        _save_widths(table, settings, key)

    header.sectionResized.connect(remember_widths)
    table._zervdiag_manual_widths = True
    table._zervdiag_width_handler = remember_widths


def install_manual_table_resizing(main_window):
    """Включает ручную ширину в поиске, клонах и статистике базы."""
    make_columns_interactive(
        main_window.results,
        main_window.settings,
        MAIN_WIDTHS_KEY,
    )

    button = getattr(main_window, "btn_db_status", None)
    if button is None:
        return

    def configure_status_table():
        dialog = getattr(
            main_window,
            "_database_status_dialog",
            None,
        )
        if dialog is None:
            return

        make_columns_interactive(
            dialog.table,
            main_window.settings,
            STATUS_WIDTHS_KEY,
        )

    # Окно статистики создаётся только при первом нажатии кнопки.
    # Поэтому применяем настройку сразу после его создания.
    button.clicked.connect(
        lambda: QTimer.singleShot(0, configure_status_table)
    )
