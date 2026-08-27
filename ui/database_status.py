import os

from PySide6.QtCore import QTimer
from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
)

from database.status import (
    get_database_summary,
    get_status_files,
)


STATUS_NAMES = {
    "ok": "OK",
    "ocr_pending": "OCR ожидает",
    "ocr_processing": "OCR выполняется",
    "error": "Ошибка",
    "pending": "Ожидает обработки",
    "processing": "Обрабатывается",
    "unsupported": "Не поддерживается",
}


class DatabaseStatusDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)

        self.setWindowTitle("ZervDiag — статистика базы")
        self.resize(1450, 760)

        layout = QVBoxLayout(self)

        self.summary = QLabel("Загрузка статистики...")
        self.summary.setStyleSheet(
            "font-size: 15px; font-weight: bold; padding: 8px;"
        )
        self.summary.setWordWrap(True)
        layout.addWidget(self.summary)

        controls = QHBoxLayout()
        controls.addWidget(QLabel("Показать:"))

        self.filter_box = QComboBox()
        self.filter_box.addItem("Все статусы", "all")
        self.filter_box.addItem("OK", "ok")
        self.filter_box.addItem("OCR очередь", "ocr")
        self.filter_box.addItem("Ошибки", "error")
        self.filter_box.addItem("Ожидают обработки", "pending")
        self.filter_box.addItem("Не поддерживаются", "unsupported")
        controls.addWidget(self.filter_box)

        self.btn_refresh = QPushButton("Обновить")
        controls.addWidget(self.btn_refresh)
        controls.addStretch()

        self.shown_label = QLabel("")
        controls.addWidget(self.shown_label)
        layout.addLayout(controls)

        self.table = QTableWidget()
        self.table.setColumnCount(8)
        self.table.setHorizontalHeaderLabels(
            [
                "Файл",
                "Тип",
                "Статус",
                "Хранение",
                "OCR",
                "Текст",
                "Ошибка",
                "Полный путь",
            ]
        )
        self.table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.table.setSelectionBehavior(QTableWidget.SelectRows)

        header = self.table.horizontalHeader()
        for column in range(6):
            header.setSectionResizeMode(
                column,
                QHeaderView.ResizeToContents,
            )
        header.setSectionResizeMode(6, QHeaderView.Stretch)
        header.setSectionResizeMode(7, QHeaderView.Stretch)

        layout.addWidget(self.table)

        self.filter_box.currentIndexChanged.connect(self.refresh_table)
        self.btn_refresh.clicked.connect(self.refresh_all)
        self.table.cellDoubleClicked.connect(self.open_file)

        self.timer = QTimer(self)
        self.timer.timeout.connect(self.refresh_summary)
        self.timer.start(5000)

        self.refresh_all()

    def refresh_all(self):
        self.refresh_summary()
        self.refresh_table()

    def refresh_summary(self):
        try:
            stats = get_database_summary()
            self.summary.setText(
                f"Всего: {stats['total']:,}   |   "
                f"OK: {stats['ok']:,}   |   "
                f"OCR: {stats['ocr']:,}   |   "
                f"Ошибки: {stats['error']:,}   |   "
                f"Ожидают обработки: {stats['pending']:,}   |   "
                f"Не поддерживаются: {stats['unsupported']:,}"
            )
        except Exception as error:
            self.summary.setText(
                f"Ошибка чтения статистики: {error}"
            )

    def refresh_table(self):
        filter_name = self.filter_box.currentData() or "all"

        try:
            rows = get_status_files(
                filter_name=filter_name,
                limit=5000,
            )
        except Exception as error:
            self.shown_label.setText(f"Ошибка: {error}")
            return

        self.table.setRowCount(0)

        for row_index, row in enumerate(rows):
            (
                file_id,
                filename,
                extension,
                status,
                error_text,
                filepath,
                is_cloud,
                ocr_page,
                ocr_total_pages,
                text_chars,
            ) = row

            self.table.insertRow(row_index)

            storage = "☁ Облако" if is_cloud else "💾 Локальный"
            ocr_text = (
                f"{ocr_page}/{ocr_total_pages}"
                if ocr_total_pages
                else ""
            )

            values = [
                filename or "",
                extension or "",
                STATUS_NAMES.get(status, status),
                storage,
                ocr_text,
                str(int(text_chars or 0)),
                error_text or "",
                filepath or "",
            ]

            for column, value in enumerate(values):
                item = QTableWidgetItem(value)
                if column == 0:
                    item.setData(0x0100, file_id)
                self.table.setItem(row_index, column, item)

        suffix = " (лимит 5000)" if len(rows) >= 5000 else ""
        self.shown_label.setText(
            f"Показано: {len(rows)}{suffix}"
        )

    def open_file(self, row, column):
        item = self.table.item(row, 7)
        if item is None:
            return

        filepath = item.text()
        if os.path.exists(filepath):
            os.startfile(filepath)


def install_database_status(main_window):
    """
    Добавляет кнопку в существующий MainWindow без изменения
    большого ui/main_window.py.
    """
    central = main_window.centralWidget()
    if central is None or central.layout() is None:
        raise RuntimeError("MainWindow layout not found")

    main_layout = central.layout()
    left_item = main_layout.itemAt(0)
    left_layout = left_item.layout() if left_item else None

    if left_layout is None:
        raise RuntimeError("Left panel layout not found")

    button = QPushButton("Статистика базы")
    main_window.btn_db_status = button

    # Перед кнопкой "Настройки".
    insert_index = max(0, left_layout.count() - 2)
    left_layout.insertWidget(insert_index, button)

    def open_status():
        dialog = getattr(
            main_window,
            "_database_status_dialog",
            None,
        )

        if dialog is None:
            dialog = DatabaseStatusDialog(main_window)
            main_window._database_status_dialog = dialog

        dialog.refresh_all()
        dialog.show()
        dialog.raise_()
        dialog.activateWindow()

    button.clicked.connect(open_status)
