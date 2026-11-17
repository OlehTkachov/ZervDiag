from pathlib import Path

from PySide6.QtCore import Qt, QThread, Signal
from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QFileDialog,
    QGridLayout,
    QGroupBox,
    QHeaderView,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QSplitter,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
)

from app_paths import DB_PATH
from database.compare import (
    compare_databases,
    load_content_diff,
)


KIND_LABELS = {
    "content_changed": "Текст изменён",
    "source_changed_text_same": "Файл изменён, текст совпадает",
    "source_changed": "Исходный файл отличается",
    "metadata_changed": "Метаданные отличаются",
    "status_changed": "Статусы отличаются",
    "text_unavailable": "Недостаточно текста",
    "only_left": "Только в базе A",
    "only_right": "Только в базе B",
    "ambiguous": "Неоднозначное совпадение",
    "identical": "Совпадает",
}

METHOD_LABELS = {
    "absolute_path": "Полный путь",
    "relative_path": "Относительный путь",
    "file_hash": "Хеш файла",
    "content_hash": "Хеш текста",
    "filename": "Только имя (проверить)",
}

STATUS_LABELS = {
    "ok": "OK",
    "pending": "Ожидает",
    "processing": "Обработка",
    "ocr_pending": "Ожидает OCR",
    "ocr_processing": "OCR",
    "error": "Ошибка",
    "unsupported": "Не поддерживается",
}


class DatabaseCompareWorker(QThread):
    result_ready = Signal(dict)
    error = Signal(str)

    def __init__(self, left_path, right_path, parent=None):
        super().__init__(parent)
        self.left_path = left_path
        self.right_path = right_path

    def run(self):
        try:
            self.result_ready.emit(
                compare_databases(
                    self.left_path,
                    self.right_path,
                )
            )
        except Exception as error:
            self.error.emit(
                f"{type(error).__name__}: {error}"
            )


class DatabaseCompareDialog(QDialog):
    """Read-only comparison of two ZervDiag database snapshots."""

    def __init__(self, parent=None):
        super().__init__(parent)

        self.setWindowTitle("ZervDiag — Сравнение баз")
        self.resize(1480, 900)

        self._worker = None
        self._result = None
        self._visible_items = []

        root = QVBoxLayout(self)

        source_group = QGroupBox("Базы для сравнения")
        source_grid = QGridLayout(source_group)

        self.left_path_edit = QLineEdit(
            str(DB_PATH) if DB_PATH.is_file() else ""
        )
        self.left_path_edit.setReadOnly(True)
        self.left_path_edit.setPlaceholderText(
            "Выберите первую базу ZervDiag"
        )

        self.right_path_edit = QLineEdit()
        self.right_path_edit.setReadOnly(True)
        self.right_path_edit.setPlaceholderText(
            "Выберите вторую базу ZervDiag"
        )

        self.left_browse_button = QPushButton("Обзор…")
        self.right_browse_button = QPushButton("Обзор…")

        source_grid.addWidget(QLabel("База A:"), 0, 0)
        source_grid.addWidget(self.left_path_edit, 0, 1)
        source_grid.addWidget(self.left_browse_button, 0, 2)

        source_grid.addWidget(QLabel("База B:"), 1, 0)
        source_grid.addWidget(self.right_path_edit, 1, 1)
        source_grid.addWidget(self.right_browse_button, 1, 2)

        self.compare_button = QPushButton("Сравнить базы")
        self.compare_button.setProperty("role", "primary")
        source_grid.addWidget(self.compare_button, 2, 1)

        root.addWidget(source_group)

        self.summary_label = QLabel(
            "Выберите две базы SQLite для сравнения."
        )
        self.summary_label.setWordWrap(True)
        root.addWidget(self.summary_label)

        filter_layout = QHBoxLayout()
        filter_layout.addWidget(QLabel("Показать:"))

        self.filter_box = QComboBox()
        self.filter_box.addItem("Все результаты", "all")
        self.filter_box.addItem("Изменённый текст", "content_changed")
        self.filter_box.addItem("Только в базе A", "only_left")
        self.filter_box.addItem("Только в базе B", "only_right")
        self.filter_box.addItem("Различия метаданных", "metadata_changed")
        self.filter_box.addItem("Различия статусов", "status_changed")
        self.filter_box.addItem("Недостаточно текста", "text_unavailable")
        self.filter_box.addItem("Неоднозначные совпадения", "ambiguous")
        self.filter_box.addItem("Совпадающие", "identical")
        filter_layout.addWidget(self.filter_box)
        filter_layout.addStretch(1)
        self.shown_label = QLabel("")
        filter_layout.addWidget(self.shown_label)
        root.addLayout(filter_layout)

        splitter = QSplitter(Qt.Orientation.Vertical)

        self.table = QTableWidget()
        self.table.setColumnCount(6)
        self.table.setHorizontalHeaderLabels([
            "Результат",
            "Документ",
            "Способ сопоставления",
            "Статус A",
            "Статус B",
            "Описание",
        ])
        self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(
            QTableWidget.SelectionBehavior.SelectRows
        )
        self.table.setSelectionMode(
            QTableWidget.SelectionMode.SingleSelection
        )
        self.table.setAlternatingRowColors(True)
        self.table.verticalHeader().setVisible(False)

        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(4, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(5, QHeaderView.ResizeMode.Stretch)

        splitter.addWidget(self.table)

        detail_group = QGroupBox("Подробности и различия текста")
        detail_layout = QVBoxLayout(detail_group)

        self.details = QPlainTextEdit()
        self.details.setReadOnly(True)
        self.details.setPlaceholderText(
            "Выберите строку, чтобы увидеть пути, метаданные и текстовые различия."
        )
        detail_layout.addWidget(self.details)
        splitter.addWidget(detail_group)
        splitter.setStretchFactor(0, 3)
        splitter.setStretchFactor(1, 2)

        root.addWidget(splitter, 1)

        bottom = QHBoxLayout()
        self.close_button = QPushButton("Закрыть")
        bottom.addStretch(1)
        bottom.addWidget(self.close_button)
        root.addLayout(bottom)

        self.left_browse_button.clicked.connect(
            lambda: self._choose_database(self.left_path_edit)
        )
        self.right_browse_button.clicked.connect(
            lambda: self._choose_database(self.right_path_edit)
        )
        self.compare_button.clicked.connect(self._start_comparison)
        self.filter_box.currentIndexChanged.connect(self._populate_table)
        self.table.itemSelectionChanged.connect(self._show_selected_detail)
        self.close_button.clicked.connect(self.reject)

    def _choose_database(self, target_edit):
        current = target_edit.text().strip() or str(Path.home())
        filename, _selected_filter = QFileDialog.getOpenFileName(
            self,
            "Выберите базу ZervDiag",
            current,
            "Базы SQLite (*.db *.sqlite *.sqlite3);;Все файлы (*.*)",
        )

        if filename:
            target_edit.setText(filename)

    def _start_comparison(self):
        left_path = self.left_path_edit.text().strip()
        right_path = self.right_path_edit.text().strip()

        if not left_path or not right_path:
            QMessageBox.information(
                self,
                "Сравнение баз",
                "Выберите обе базы ZervDiag.",
            )
            return

        try:
            if Path(left_path).resolve() == Path(right_path).resolve():
                QMessageBox.information(
                    self,
                    "Сравнение баз",
                    "Выбрана одна и та же база дважды.",
                )
                return
        except OSError:
            pass

        self.compare_button.setEnabled(False)
        self.left_browse_button.setEnabled(False)
        self.right_browse_button.setEnabled(False)
        self.summary_label.setText(
            "Проверка баз и сравнение записей…"
        )
        self.details.clear()
        self.table.setRowCount(0)

        self._worker = DatabaseCompareWorker(
            left_path,
            right_path,
            self,
        )
        self._worker.result_ready.connect(self._comparison_finished)
        self._worker.error.connect(self._comparison_error)
        self._worker.finished.connect(self._worker_finished)
        self._worker.start()

    def _worker_finished(self):
        if self._worker is not None:
            self._worker.deleteLater()
            self._worker = None

        self.compare_button.setEnabled(True)
        self.left_browse_button.setEnabled(True)
        self.right_browse_button.setEnabled(True)

    def _comparison_error(self, message):
        self.summary_label.setText("Сравнение не выполнено.")
        self.details.setPlainText(message)
        QMessageBox.critical(
            self,
            "Ошибка сравнения баз",
            message,
        )

    def _comparison_finished(self, result):
        self._result = result
        summary = result["summary"]

        self.summary_label.setText(
            f"База A: {summary['left_total']:,} файлов  |  "
            f"База B: {summary['right_total']:,} файлов\n"
            f"Сопоставлено: {summary['matched']:,}  |  "
            f"Изменён текст: {summary['content_changed']:,}  |  "
            f"Только A: {summary['only_left']:,}  |  "
            f"Только B: {summary['only_right']:,}  |  "
            f"Совпадают: {summary['identical']:,}  |  "
            f"Неоднозначно: {summary['ambiguous']:,}"
        )

        self.filter_box.setCurrentIndex(0)
        self._populate_table()

        if self.table.rowCount():
            self.table.selectRow(0)

    def _populate_table(self):
        if not self._result:
            return

        filter_kind = self.filter_box.currentData() or "all"
        items = self._result["items"]

        if filter_kind == "all":
            visible = list(enumerate(items))
        else:
            visible = [
                (index, item)
                for index, item in enumerate(items)
                if item["kind"] == filter_kind
            ]

        self._visible_items = [
            item
            for _index, item in visible
        ]

        self.table.setRowCount(0)

        for row_index, (source_index, item) in enumerate(visible):
            self.table.insertRow(row_index)

            left = item.get("left")
            right = item.get("right")

            values = [
                KIND_LABELS.get(item["kind"], item["kind"]),
                item.get("filename") or "",
                METHOD_LABELS.get(
                    item.get("match_method"),
                    item.get("match_method") or "—",
                ),
                STATUS_LABELS.get(
                    left.get("status"),
                    left.get("status", "—"),
                ) if left else "—",
                STATUS_LABELS.get(
                    right.get("status"),
                    right.get("status", "—"),
                ) if right else "—",
                item.get("summary", ""),
            ]

            for column, value in enumerate(values):
                cell = QTableWidgetItem(str(value))
                if column == 0:
                    cell.setData(Qt.ItemDataRole.UserRole, source_index)
                self.table.setItem(row_index, column, cell)

        self.shown_label.setText(
            f"Показано: {len(visible)} из {len(items)}"
        )

        self.details.clear()

    def _show_selected_detail(self):
        row_index = self.table.currentRow()
        if row_index < 0:
            return

        first_cell = self.table.item(row_index, 0)
        if first_cell is None or self._result is None:
            return

        source_index = first_cell.data(Qt.ItemDataRole.UserRole)
        if source_index is None:
            return

        item = self._result["items"][int(source_index)]
        lines = [
            f"Результат: {KIND_LABELS.get(item['kind'], item['kind'])}",
            f"Описание: {item.get('summary', '')}",
        ]

        method = item.get("match_method")
        if method:
            lines.append(
                "Сопоставление: "
                + METHOD_LABELS.get(method, method)
            )

        left = item.get("left")
        right = item.get("right")

        if left:
            lines.extend([
                "",
                "=== БАЗА A ===",
                f"Файл: {left['filename']}",
                f"Путь: {left['filepath']}",
                f"Статус: {left['status']}",
                f"Размер: {left['size']}",
                f"Символов извлечено: {left['text_chars']}",
                f"Ошибка: {left['error'] or '—'}",
            ])

        if right:
            lines.extend([
                "",
                "=== БАЗА B ===",
                f"Файл: {right['filename']}",
                f"Путь: {right['filepath']}",
                f"Статус: {right['status']}",
                f"Размер: {right['size']}",
                f"Символов извлечено: {right['text_chars']}",
                f"Ошибка: {right['error'] or '—'}",
            ])

        if item.get("metadata_changes"):
            lines.extend(["", "=== ИЗМЕНЕНИЯ МЕТАДАННЫХ ==="])
            for change in item["metadata_changes"]:
                lines.append(
                    f"{change['label']}: "
                    f"{change['left']!r} → {change['right']!r}"
                )

        if item["kind"] == "ambiguous":
            lines.extend([
                "",
                "=== КАНДИДАТЫ В БАЗЕ A ===",
            ])
            for record in item.get("left_candidates", []):
                lines.append(record["filepath"])

            lines.extend([
                "",
                "=== КАНДИДАТЫ В БАЗЕ B ===",
            ])
            for record in item.get("right_candidates", []):
                lines.append(record["filepath"])

        if left and right:
            diff = load_content_diff(
                self._result["left_path"],
                self._result["right_path"],
                left["id"],
                right["id"],
                left_name=left["filename"],
                right_name=right["filename"],
            )
            lines.extend([
                "",
                "=== РАЗЛИЧИЯ ИЗВЛЕЧЁННОГО ТЕКСТА ===",
                diff,
            ])

        self.details.setPlainText("\n".join(lines))

    def closeEvent(self, event):
        if self._worker is not None and self._worker.isRunning():
            QMessageBox.information(
                self,
                "Сравнение выполняется",
                "Дождитесь завершения сравнения баз.",
            )
            event.ignore()
            return

        event.accept()
