import os
import subprocess
import sys
from pathlib import Path

from PySide6.QtCore import QSettings, QThread, Signal, Qt
from PySide6.QtWidgets import (
    QApplication,
    QComboBox,
    QFileDialog,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QTabWidget,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from auditor.engine import (
    duplicate_name_groups,
    export_records_csv,
    extension_stats,
    folder_stats,
    load_audit_records,
    move_to_quarantine,
    summarize,
)


CATEGORY_NAMES = {
    "useful": "Полезный",
    "review": "Проверить",
    "suspicious": "Подозрительный",
}


class LoadWorker(QThread):
    loaded = Signal(list)
    failed = Signal(str)

    def run(self):
        try:
            self.loaded.emit(load_audit_records())
        except Exception as error:
            self.failed.emit(str(error))


class AuditorWindow(QMainWindow):
    def __init__(self):
        super().__init__()

        self.setWindowTitle("ZervDiag Library Auditor")
        self.resize(1500, 850)

        self.records = []
        self.filtered_records = []
        self.load_worker = None

        self.settings = QSettings("ZervDiag", "ZervDiag")

        self._build_ui()
        self._start_load()

    def _build_ui(self):
        central = QWidget()
        self.setCentralWidget(central)

        layout = QVBoxLayout(central)

        title = QLabel("ZervDiag Library Auditor")
        title.setStyleSheet(
            "font-size: 24px; font-weight: bold; padding: 6px;"
        )
        layout.addWidget(title)

        subtitle = QLabel(
            "Безопасная ревизия библиотеки. "
            "Утилита ничего не удаляет автоматически."
        )
        subtitle.setWordWrap(True)
        layout.addWidget(subtitle)

        self.summary_label = QLabel("Загрузка базы...")
        self.summary_label.setStyleSheet(
            "font-size: 15px; font-weight: bold; padding: 6px;"
        )
        layout.addWidget(self.summary_label)

        self.tabs = QTabWidget()
        layout.addWidget(self.tabs)

        self._build_files_tab()
        self._build_group_tab()
        self._build_duplicates_tab()

        self.status = QLabel("Готово")
        self.status.setWordWrap(True)
        layout.addWidget(self.status)

    def _build_files_tab(self):
        page = QWidget()
        layout = QVBoxLayout(page)

        controls = QHBoxLayout()
        controls.addWidget(QLabel("Показать:"))

        self.category_filter = QComboBox()
        self.category_filter.addItem("Все файлы", "all")
        self.category_filter.addItem("Подозрительные", "suspicious")
        self.category_filter.addItem("Нужно проверить", "review")
        self.category_filter.addItem("Вероятно полезные", "useful")
        controls.addWidget(self.category_filter)

        controls.addWidget(QLabel("Фильтр:"))

        self.text_filter = QLineEdit()
        self.text_filter.setPlaceholderText(
            "Имя файла, папка, расширение, причина..."
        )
        controls.addWidget(self.text_filter, 1)

        self.btn_refresh = QPushButton("Пересчитать")
        self.btn_export = QPushButton("Экспорт CSV")
        self.btn_open = QPushButton("Открыть")
        self.btn_folder = QPushButton("Показать в папке")
        self.btn_quarantine = QPushButton("В карантин...")

        controls.addWidget(self.btn_refresh)
        controls.addWidget(self.btn_export)
        controls.addWidget(self.btn_open)
        controls.addWidget(self.btn_folder)
        controls.addWidget(self.btn_quarantine)

        layout.addLayout(controls)

        self.files_table = QTableWidget()
        self.files_table.setColumnCount(9)
        self.files_table.setHorizontalHeaderLabels(
            [
                "Оценка",
                "Категория",
                "Файл",
                "Тип",
                "Размер",
                "Статус",
                "Текст",
                "Почему",
                "Полный путь",
            ]
        )
        self.files_table.setSelectionBehavior(QTableWidget.SelectRows)
        self.files_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.files_table.setSortingEnabled(True)

        header = self.files_table.horizontalHeader()
        header.setSectionResizeMode(QHeaderView.Interactive)
        header.resizeSection(0, 75)
        header.resizeSection(1, 120)
        header.resizeSection(2, 300)
        header.resizeSection(3, 80)
        header.resizeSection(4, 100)
        header.resizeSection(5, 120)
        header.resizeSection(6, 90)
        header.resizeSection(7, 400)
        header.resizeSection(8, 650)

        layout.addWidget(self.files_table)

        self.category_filter.currentIndexChanged.connect(
            self._apply_file_filter
        )
        self.text_filter.textChanged.connect(self._apply_file_filter)
        self.btn_refresh.clicked.connect(self._start_load)
        self.btn_export.clicked.connect(self._export_csv)
        self.btn_open.clicked.connect(self._open_selected)
        self.btn_folder.clicked.connect(self._show_selected_in_folder)
        self.btn_quarantine.clicked.connect(self._quarantine_selected)
        self.files_table.cellDoubleClicked.connect(
            lambda _row, _column: self._open_selected()
        )

        self.tabs.addTab(page, "Файлы")

    def _build_group_tab(self):
        page = QWidget()
        layout = QVBoxLayout(page)

        controls = QHBoxLayout()
        controls.addWidget(QLabel("Группировка:"))

        self.group_mode = QComboBox()
        self.group_mode.addItem("По папкам", "folders")
        self.group_mode.addItem("По расширениям", "extensions")
        controls.addWidget(self.group_mode)

        self.btn_choose_root = QPushButton("Корень библиотеки...")
        controls.addWidget(self.btn_choose_root)

        self.root_label = QLabel("")
        self.root_label.setTextInteractionFlags(Qt.TextSelectableByMouse)
        controls.addWidget(self.root_label, 1)

        layout.addLayout(controls)

        self.group_table = QTableWidget()
        self.group_table.setColumnCount(5)
        self.group_table.setHorizontalHeaderLabels(
            [
                "Группа",
                "Всего",
                "Подозрительных",
                "Проверить",
                "Полезных",
            ]
        )
        self.group_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.group_table.setSelectionBehavior(QTableWidget.SelectRows)
        self.group_table.setSortingEnabled(True)

        header = self.group_table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.Stretch)
        for column in range(1, 5):
            header.setSectionResizeMode(
                column,
                QHeaderView.ResizeToContents,
            )

        layout.addWidget(self.group_table)

        self.group_mode.currentIndexChanged.connect(self._refresh_groups)
        self.btn_choose_root.clicked.connect(self._choose_library_root)

        self.tabs.addTab(page, "Папки и типы")

    def _build_duplicates_tab(self):
        page = QWidget()
        layout = QVBoxLayout(page)

        info = QLabel(
            "Здесь показаны одинаковые имена файлов. "
            "Это ещё не доказательство точного клона; "
            "точные клоны ZervDiag проверяет по содержимому отдельно."
        )
        info.setWordWrap(True)
        layout.addWidget(info)

        self.duplicates_table = QTableWidget()
        self.duplicates_table.setColumnCount(3)
        self.duplicates_table.setHorizontalHeaderLabels(
            ["Имя", "Количество", "Папки"]
        )
        self.duplicates_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.duplicates_table.setSelectionBehavior(QTableWidget.SelectRows)

        header = self.duplicates_table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(1, QHeaderView.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.Stretch)

        layout.addWidget(self.duplicates_table)

        self.tabs.addTab(page, "Повторяющиеся имена")

    def _start_load(self):
        if self.load_worker and self.load_worker.isRunning():
            return

        self._set_busy(True)
        self.status.setText(
            "Анализ базы... Файлы не открываются и не скачиваются."
        )

        self.load_worker = LoadWorker(self)
        self.load_worker.loaded.connect(self._load_finished)
        self.load_worker.failed.connect(self._load_failed)
        self.load_worker.start()

    def _set_busy(self, busy):
        enabled = not busy
        self.btn_refresh.setEnabled(enabled)
        self.btn_export.setEnabled(enabled)
        self.btn_open.setEnabled(enabled)
        self.btn_folder.setEnabled(enabled)
        self.btn_quarantine.setEnabled(enabled)

    def _load_finished(self, records):
        self.records = records

        counts = summarize(records)
        self.summary_label.setText(
            f"Всего: {counts['total']:,}   |   "
            f"Вероятно полезные: {counts['useful']:,}   |   "
            f"Проверить: {counts['review']:,}   |   "
            f"Подозрительные: {counts['suspicious']:,}"
        )

        current_root = self.settings.value(
            "documentation_folder",
            "",
        )
        self.root_label.setText(current_root or "Корень не выбран")

        self._apply_file_filter()
        self._refresh_groups()
        self._refresh_duplicates()

        self._set_busy(False)
        self.status.setText(
            "Готово. Сначала смотрите «Подозрительные», "
            "затем «Нужно проверить»."
        )

        if self.load_worker:
            self.load_worker.deleteLater()
            self.load_worker = None

    def _load_failed(self, message):
        self._set_busy(False)
        self.status.setText(f"Ошибка анализа: {message}")
        QMessageBox.critical(
            self,
            "Ошибка",
            f"Не удалось прочитать базу:\n\n{message}",
        )

        if self.load_worker:
            self.load_worker.deleteLater()
            self.load_worker = None

    def _apply_file_filter(self):
        category = self.category_filter.currentData() or "all"
        text = self.text_filter.text().strip().casefold()
        result = []

        for record in self.records:
            if category != "all" and record.category != category:
                continue

            if text:
                haystack = " ".join(
                    [
                        record.filename,
                        record.filepath,
                        record.extension,
                        record.extraction_status,
                        " ".join(record.reasons),
                    ]
                ).casefold()

                if text not in haystack:
                    continue

            result.append(record)

        result.sort(
            key=lambda record: (
                record.score,
                record.filename.casefold(),
            )
        )

        self.filtered_records = result
        self._fill_files_table(result)
        self.status.setText(f"Показано файлов: {len(result):,}")

    def _fill_files_table(self, records):
        self.files_table.setSortingEnabled(False)
        self.files_table.setRowCount(len(records))

        for row, record in enumerate(records):
            values = [
                str(record.score),
                CATEGORY_NAMES.get(record.category, record.category),
                record.filename,
                record.extension,
                self._format_size(record.size),
                record.extraction_status,
                f"{record.content_chars:,}",
                " | ".join(record.reasons),
                record.filepath,
            ]

            for column, value in enumerate(values):
                item = QTableWidgetItem(value)

                if column == 0:
                    item.setData(Qt.UserRole, record.file_id)
                    item.setData(Qt.EditRole, record.score)

                self.files_table.setItem(row, column, item)

        self.files_table.setSortingEnabled(True)

    @staticmethod
    def _format_size(size):
        size = int(size or 0)

        if size < 1024:
            return f"{size} Б"
        if size < 1024 * 1024:
            return f"{size / 1024:.1f} КБ"
        if size < 1024 * 1024 * 1024:
            return f"{size / (1024 * 1024):.1f} МБ"
        return f"{size / (1024 * 1024 * 1024):.2f} ГБ"

    def _record_by_id(self, file_id):
        for record in self.records:
            if record.file_id == file_id:
                return record
        return None

    def _selected_records(self):
        selection = self.files_table.selectionModel().selectedRows()
        result = []

        for model_index in selection:
            item = self.files_table.item(model_index.row(), 0)
            if not item:
                continue

            file_id = item.data(Qt.UserRole)
            record = self._record_by_id(file_id)

            if record:
                result.append(record)

        return result

    def _open_selected(self):
        records = self._selected_records()

        if not records:
            self.status.setText("Выберите файл")
            return

        path = records[0].filepath

        if os.path.isfile(path):
            os.startfile(path)
        else:
            QMessageBox.information(
                self,
                "Файл недоступен",
                "Файл сейчас недоступен локально.\n\n"
                f"{path}",
            )

    def _show_selected_in_folder(self):
        records = self._selected_records()

        if not records:
            self.status.setText("Выберите файл")
            return

        path = Path(records[0].filepath)

        if path.exists():
            subprocess.Popen(["explorer", f"/select,{path}"])
        elif path.parent.exists():
            os.startfile(str(path.parent))
        else:
            QMessageBox.information(
                self,
                "Папка недоступна",
                f"Папка не найдена:\n\n{path.parent}",
            )

    def _export_csv(self):
        if not self.filtered_records:
            QMessageBox.information(
                self,
                "Экспорт",
                "Сейчас нет строк для экспорта.",
            )
            return

        filename, _ = QFileDialog.getSaveFileName(
            self,
            "Сохранить отчёт",
            "zervdiag_library_audit.csv",
            "CSV (*.csv)",
        )

        if not filename:
            return

        try:
            export_records_csv(self.filtered_records, filename)
        except Exception as error:
            QMessageBox.critical(self, "Ошибка экспорта", str(error))
            return

        self.status.setText(
            f"Экспортировано строк: {len(self.filtered_records):,}"
        )

    def _choose_library_root(self):
        current = self.settings.value("documentation_folder", "")

        folder = QFileDialog.getExistingDirectory(
            self,
            "Корень библиотеки",
            current if current and os.path.isdir(current) else "",
        )

        if not folder:
            return

        self.settings.setValue("documentation_folder", folder)
        self.root_label.setText(folder)
        self._refresh_groups()

    def _refresh_groups(self):
        if not self.records:
            self.group_table.setRowCount(0)
            return

        mode = self.group_mode.currentData() or "folders"

        if mode == "extensions":
            stats = extension_stats(self.records)
        else:
            root = self.settings.value("documentation_folder", "")
            stats = folder_stats(
                self.records,
                root=root or None,
                max_depth=2,
            )

        self.group_table.setSortingEnabled(False)
        self.group_table.setRowCount(len(stats))

        for row, stat in enumerate(stats):
            values = [
                stat.name,
                stat.total,
                stat.suspicious,
                stat.review,
                stat.useful,
            ]

            for column, value in enumerate(values):
                item = QTableWidgetItem(str(value))
                if column > 0:
                    item.setData(Qt.EditRole, int(value))
                self.group_table.setItem(row, column, item)

        self.group_table.setSortingEnabled(True)

    def _refresh_duplicates(self):
        groups = duplicate_name_groups(self.records)
        self.duplicates_table.setRowCount(len(groups))

        for row, group in enumerate(groups):
            folders = sorted(
                {
                    str(Path(record.filepath).parent)
                    for record in group
                }
            )

            values = [
                group[0].filename,
                str(len(group)),
                " | ".join(folders),
            ]

            for column, value in enumerate(values):
                self.duplicates_table.setItem(
                    row,
                    column,
                    QTableWidgetItem(value),
                )

    def _quarantine_selected(self):
        records = self._selected_records()

        if not records:
            QMessageBox.information(
                self,
                "Карантин",
                "Сначала выберите один или несколько файлов.",
            )
            return

        library_root = self.settings.value("documentation_folder", "")

        if not library_root or not os.path.isdir(library_root):
            library_root = QFileDialog.getExistingDirectory(
                self,
                "Укажите корень библиотеки",
                "",
            )

            if not library_root:
                return

            self.settings.setValue("documentation_folder", library_root)
            self.root_label.setText(library_root)

        quarantine_root = QFileDialog.getExistingDirectory(
            self,
            "Выберите папку карантина",
            str(Path(library_root).parent),
        )

        if not quarantine_root:
            return

        answer = QMessageBox.question(
            self,
            "Переместить в карантин?",
            f"Выбрано файлов: {len(records)}.\n\n"
            "Файлы НЕ будут удалены — они будут перемещены "
            "в выбранную папку с сохранением структуры.\n\n"
            "Облачные файлы автоматически перемещаться не будут.\n\n"
            "Продолжить?",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )

        if answer != QMessageBox.Yes:
            return

        log_path = str(
            Path(quarantine_root) / "zervdiag_quarantine_log.csv"
        )

        moved, skipped, errors = move_to_quarantine(
            records,
            library_root,
            quarantine_root,
            log_path=log_path,
        )

        QMessageBox.information(
            self,
            "Карантин",
            f"Перемещено: {len(moved)}\n"
            f"Пропущено: {len(skipped)}\n"
            f"Ошибок: {len(errors)}\n\n"
            f"Журнал:\n{log_path}\n\n"
            "После ревизии запустите обычную индексацию ZervDiag, "
            "чтобы база увидела перемещения.",
        )

        self.status.setText(
            f"Карантин: перемещено {len(moved)}, "
            f"пропущено {len(skipped)}, ошибок {len(errors)}."
        )


def main():
    app = QApplication(sys.argv)
    window = AuditorWindow()
    window.showMaximized()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
