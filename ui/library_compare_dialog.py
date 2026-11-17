from pathlib import Path

from PySide6.QtCore import Qt, QThread, Signal
from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QSplitter,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
)

from database.compare import compare_databases, load_content_diff
from database.libraries import get_active_library_id, get_libraries


KIND_LABELS = {
    "content_changed": "Текст изменён",
    "source_changed_text_same": "Файл изменён, текст совпадает",
    "source_changed": "Исходный файл отличается",
    "metadata_changed": "Метаданные отличаются",
    "status_changed": "Статусы отличаются",
    "text_unavailable": "Недостаточно текста",
    "only_left": "Только в библиотеке A",
    "only_right": "Только в библиотеке B",
    "ambiguous": "Неоднозначное совпадение",
    "identical": "Совпадает",
}

METHOD_LABELS = {
    "absolute_path": "Полный путь",
    "relative_path": "Относительный путь",
    "file_hash": "Хеш исходного файла",
    "content_hash": "Хеш извлечённого текста",
    "filename": "Только имя — проверить",
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


class LibraryCompareWorker(QThread):
    result_ready = Signal(dict)
    error = Signal(str)

    def __init__(self, left_path, right_path, parent=None):
        super().__init__(parent)
        self.left_path = left_path
        self.right_path = right_path

    def run(self):
        try:
            self.result_ready.emit(
                compare_databases(self.left_path, self.right_path)
            )
        except Exception as error:
            self.error.emit(
                f"{type(error).__name__}: {error}"
            )


class LibraryCompareDialog(QDialog):
    """Compare two separately indexed document libraries."""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("ZervDiag — Сравнение библиотек")
        self.resize(1480, 900)

        self._worker = None
        self._result = None
        self._visible_items = []
        self._library_by_id = {}

        root = QVBoxLayout(self)

        source_group = QGroupBox("Выберите две библиотеки документов")
        source_layout = QVBoxLayout(source_group)

        row_a = QHBoxLayout()
        row_a.addWidget(QLabel("Библиотека A:"))
        self.left_library_box = QComboBox()
        self.left_library_box.setMinimumWidth(280)
        row_a.addWidget(self.left_library_box, 1)
        source_layout.addLayout(row_a)

        self.left_folder_label = QLabel("")
        self.left_folder_label.setWordWrap(True)
        source_layout.addWidget(self.left_folder_label)

        row_b = QHBoxLayout()
        row_b.addWidget(QLabel("Библиотека B:"))
        self.right_library_box = QComboBox()
        self.right_library_box.setMinimumWidth(280)
        row_b.addWidget(self.right_library_box, 1)
        source_layout.addLayout(row_b)

        self.right_folder_label = QLabel("")
        self.right_folder_label.setWordWrap(True)
        source_layout.addWidget(self.right_folder_label)

        self.compare_button = QPushButton("Сравнить библиотеки")
        self.compare_button.setProperty("role", "primary")
        source_layout.addWidget(self.compare_button, alignment=Qt.AlignmentFlag.AlignRight)
        root.addWidget(source_group)

        self.note_label = QLabel(
            "Каждая библиотека имеет собственную SQLite-базу и индексируется независимо. "
            "Сравниваются метаданные и извлечённый при индексации/OCR текст. "
            "Оригинальные документы не изменяются. Сопоставление только по имени "
            "помечается как требующее ручной проверки."
        )
        self.note_label.setWordWrap(True)
        root.addWidget(self.note_label)

        self.summary_label = QLabel(
            "Выберите две библиотеки. Если вторая ещё не создана, добавьте её "
            "в главном окне кнопкой «Добавить библиотеку»."
        )
        self.summary_label.setWordWrap(True)
        root.addWidget(self.summary_label)

        filter_layout = QHBoxLayout()
        filter_layout.addWidget(QLabel("Показать:"))
        self.filter_box = QComboBox()
        self.filter_box.addItem("Только различия", "differences")
        self.filter_box.addItem("Все результаты", "all")
        self.filter_box.addItem("Изменённый текст", "content_changed")
        self.filter_box.addItem("Файл изменён, текст совпадает", "source_changed_text_same")
        self.filter_box.addItem("Исходный файл отличается", "source_changed")
        self.filter_box.addItem("Только в библиотеке A", "only_left")
        self.filter_box.addItem("Только в библиотеке B", "only_right")
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
        self.table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        self.table.setSelectionMode(QTableWidget.SelectionMode.SingleSelection)
        self.table.setAlternatingRowColors(True)
        self.table.verticalHeader().setVisible(False)

        header = self.table.horizontalHeader()
        for column in range(5):
            header.setSectionResizeMode(
                column,
                QHeaderView.ResizeMode.ResizeToContents,
            )
        header.setSectionResizeMode(5, QHeaderView.ResizeMode.Stretch)
        splitter.addWidget(self.table)

        detail_group = QGroupBox("Подробности и различия текста")
        detail_layout = QVBoxLayout(detail_group)
        self.details = QPlainTextEdit()
        self.details.setReadOnly(True)
        self.details.setPlaceholderText(
            "Выберите строку, чтобы увидеть пути, метаданные и построчные различия."
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

        self.left_library_box.currentIndexChanged.connect(self._update_library_labels)
        self.right_library_box.currentIndexChanged.connect(self._update_library_labels)
        self.compare_button.clicked.connect(self._start_comparison)
        self.filter_box.currentIndexChanged.connect(self._populate_table)
        self.table.itemSelectionChanged.connect(self._show_selected_detail)
        self.close_button.clicked.connect(self.reject)

        self._load_libraries()

    def _load_libraries(self):
        libraries = get_libraries()
        self._library_by_id = {
            item["id"]: item
            for item in libraries
        }

        self.left_library_box.clear()
        self.right_library_box.clear()
        for library in libraries:
            self.left_library_box.addItem(library["name"], library["id"])
            self.right_library_box.addItem(library["name"], library["id"])

        active_id = get_active_library_id()
        left_index = self.left_library_box.findData(active_id)
        if left_index >= 0:
            self.left_library_box.setCurrentIndex(left_index)

        right_index = next(
            (
                index
                for index in range(self.right_library_box.count())
                if self.right_library_box.itemData(index) != active_id
            ),
            -1,
        )
        if right_index >= 0:
            self.right_library_box.setCurrentIndex(right_index)

        enough_libraries = len(libraries) >= 2
        self.compare_button.setEnabled(enough_libraries)
        if not enough_libraries:
            self.summary_label.setText(
                "Добавьте вторую библиотеку в главном окне. "
                "Каждая папка должна быть зарегистрирована и проиндексирована отдельно."
            )
        self._update_library_labels()

    def _update_library_labels(self, _index=None):
        left_id = self.left_library_box.currentData()
        right_id = self.right_library_box.currentData()
        left = self._library_by_id.get(left_id)
        right = self._library_by_id.get(right_id)

        self.left_folder_label.setText(
            f"Папка: {left.get('source_folder') or 'не выбрана'}"
            if left else "Папка: —"
        )
        self.right_folder_label.setText(
            f"Папка: {right.get('source_folder') or 'не выбрана'}"
            if right else "Папка: —"
        )

    def _start_comparison(self):
        left_id = self.left_library_box.currentData()
        right_id = self.right_library_box.currentData()
        left = self._library_by_id.get(left_id)
        right = self._library_by_id.get(right_id)

        if not left or not right:
            QMessageBox.information(
                self,
                "Сравнение библиотек",
                "Выберите две библиотеки.",
            )
            return

        left_path = Path(left["database_path"]).resolve()
        right_path = Path(right["database_path"]).resolve()
        if left_id == right_id or left_path == right_path:
            QMessageBox.information(
                self,
                "Сравнение библиотек",
                "Выберите две разные библиотеки.",
            )
            return

        if not left_path.is_file() or not right_path.is_file():
            QMessageBox.warning(
                self,
                "Сравнение библиотек",
                "База одной из библиотек ещё не создана. "
                "Сначала откройте эту библиотеку и выполните индексацию.",
            )
            return

        self._comparison_names = {
            "left": left["name"],
            "right": right["name"],
        }
        self.compare_button.setEnabled(False)
        self.left_library_box.setEnabled(False)
        self.right_library_box.setEnabled(False)
        self.summary_label.setText("Проверка библиотек и сравнение документов…")
        self.details.clear()
        self.table.setRowCount(0)

        self._worker = LibraryCompareWorker(
            str(left_path),
            str(right_path),
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
        self.compare_button.setEnabled(
            self.left_library_box.count() >= 2
        )
        self.left_library_box.setEnabled(True)
        self.right_library_box.setEnabled(True)

    def _comparison_error(self, message):
        self.summary_label.setText("Сравнение не выполнено.")
        self.details.setPlainText(message)
        QMessageBox.critical(
            self,
            "Ошибка сравнения библиотек",
            message,
        )

    def _comparison_finished(self, result):
        self._result = result
        summary = result["summary"]
        names = getattr(self, "_comparison_names", {})
        self.summary_label.setText(
            f"A: {names.get('left', result['left_name'])} — {summary['left_total']:,} документов  |  "
            f"B: {names.get('right', result['right_name'])} — {summary['right_total']:,} документов\n"
            f"Сопоставлено: {summary['matched']:,}  |  "
            f"Изменён текст: {summary['content_changed']:,}  |  "
            f"Метаданные: {summary['metadata_changed']:,}  |  "
            f"Только A: {summary['only_left']:,}  |  "
            f"Только B: {summary['only_right']:,}  |  "
            f"Совпадают: {summary['identical']:,}  |  "
            f"Недостаточно текста: {summary['text_unavailable']:,}  |  "
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
        elif filter_kind == "differences":
            visible = [
                (index, item)
                for index, item in enumerate(items)
                if item["kind"] != "identical"
            ]
        else:
            visible = [
                (index, item)
                for index, item in enumerate(items)
                if item["kind"] == filter_kind
            ]

        self.table.setRowCount(0)
        for row_index, (source_index, item) in enumerate(visible):
            self.table.insertRow(row_index)
            left = item.get("left")
            right = item.get("right")
            method = item.get("match_method")
            method_label = METHOD_LABELS.get(method, method or "—")
            if item.get("match_confidence") == "low":
                method_label += " — низкая уверенность"

            values = [
                KIND_LABELS.get(item["kind"], item["kind"]),
                item.get("filename") or "",
                method_label,
                STATUS_LABELS.get(left.get("status"), left.get("status", "—")) if left else "—",
                STATUS_LABELS.get(right.get("status"), right.get("status", "—")) if right else "—",
                item.get("summary", ""),
            ]
            for column, value in enumerate(values):
                cell = QTableWidgetItem(str(value))
                if column == 0:
                    cell.setData(Qt.ItemDataRole.UserRole, source_index)
                self.table.setItem(row_index, column, cell)

        self._visible_items = [item for _index, item in visible]
        self.shown_label.setText(
            f"Показано: {len(visible)} из {len(items)}"
        )
        self.details.clear()

    def _show_selected_detail(self):
        row_index = self.table.currentRow()
        if row_index < 0 or self._result is None:
            return

        first_cell = self.table.item(row_index, 0)
        if first_cell is None:
            return
        source_index = first_cell.data(Qt.ItemDataRole.UserRole)
        if source_index is None:
            return

        item = self._result["items"][int(source_index)]
        names = getattr(self, "_comparison_names", {})
        lines = [
            f"Результат: {KIND_LABELS.get(item['kind'], item['kind'])}",
            f"Описание: {item.get('summary', '')}",
        ]

        method = item.get("match_method")
        if method:
            method_text = METHOD_LABELS.get(method, method)
            if item.get("match_confidence") == "low":
                method_text += " — низкая уверенность"
            lines.append("Сопоставление: " + method_text)

        left = item.get("left")
        right = item.get("right")
        if left:
            lines.extend([
                "",
                f"=== БИБЛИОТЕКА A: {names.get('left', 'A')} ===",
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
                f"=== БИБЛИОТЕКА B: {names.get('right', 'B')} ===",
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
            lines.extend(["", "=== КАНДИДАТЫ В БИБЛИОТЕКЕ A ==="])
            for record in item.get("left_candidates", []):
                lines.append(record["filepath"])
            lines.extend(["", "=== КАНДИДАТЫ В БИБЛИОТЕКЕ B ==="])
            for record in item.get("right_candidates", []):
                lines.append(record["filepath"])

        if left and right:
            diff = load_content_diff(
                self._result["left_path"],
                self._result["right_path"],
                left["id"],
                right["id"],
                left_name=f"{names.get('left', 'A')}: {left['filename']}",
                right_name=f"{names.get('right', 'B')}: {right['filename']}",
            )
            lines.extend(["", "=== РАЗЛИЧИЯ ИЗВЛЕЧЁННОГО ТЕКСТА ===", diff])

        self.details.setPlainText("\n".join(lines))

    def closeEvent(self, event):
        if self._worker is not None and self._worker.isRunning():
            QMessageBox.information(
                self,
                "Сравнение выполняется",
                "Дождитесь завершения сравнения библиотек.",
            )
            event.ignore()
            return
        event.accept()
