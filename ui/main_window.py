import os

from PySide6.QtWidgets import (
    QMainWindow,
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QPushButton,
    QLineEdit,
    QLabel,
    QTableWidget,
    QTableWidgetItem,
    QHeaderView,
    QFileDialog,
)

from PySide6.QtCore import (
    QThread,
    Signal,
    QSettings,
)

from database.db import create_database
from indexer.indexer import index_folder
from search.search import search_files
from search.analyzer import DocumentAnalyzer
from search.duplicates import find_duplicates


class IndexWorker(QThread):

    progress = Signal(int, int, str, bool)
    finished = Signal(int, int, int, int, int)
    error = Signal(str)

    def __init__(self, folder):
        super().__init__()
        self.folder = folder

    def run(self):

        try:

            result = index_folder(
                self.folder,
                self.report_progress
            )

            self.finished.emit(*result)

        except Exception as error:

            self.error.emit(str(error))

    def report_progress(
        self,
        current,
        total,
        filename,
        cloud
    ):

        self.progress.emit(
            current,
            total,
            filename,
            cloud
        )


class DuplicateWorker(QThread):

    finished = Signal(list)
    error = Signal(str)

    def run(self):

        try:

            duplicates = find_duplicates()

            self.finished.emit(
                duplicates
            )

        except Exception as error:

            self.error.emit(
                str(error)
            )


class MainWindow(QMainWindow):

    def __init__(self):

        super().__init__()

        self.setWindowTitle(
            "ZervDiag"
        )

        self.resize(
            1600,
            850
        )

        self.worker = None
        self.analyzer = None
        self.duplicate_worker = None

        self.current_results = []

        self.settings = QSettings(
            "ZervDiag",
            "ZervDiag"
        )

        create_database()

        self.create_ui()

        self.load_settings()

    def create_ui(self):

        central = QWidget()

        self.setCentralWidget(
            central
        )

        main_layout = QHBoxLayout(
            central
        )

        # Левая панель

        left_panel = QVBoxLayout()

        title = QLabel(
            "ZervDiag"
        )

        title.setStyleSheet(
            "font-size: 24px;"
            "font-weight: bold;"
            "padding: 10px;"
        )

        self.btn_search = QPushButton(
            "Поиск"
        )

        self.btn_index = QPushButton(
            "Индексация"
        )

        self.btn_folder = QPushButton(
            "Выбрать папку"
        )

        self.btn_analyze = QPushButton(
            "Анализировать найденные"
        )

        self.btn_duplicates = QPushButton(
            "Клоны"
        )

        self.btn_settings = QPushButton(
            "Настройки"
        )

        self.btn_analyze.setEnabled(
            False
        )

        left_panel.addWidget(
            title
        )

        left_panel.addWidget(
            self.btn_search
        )

        left_panel.addWidget(
            self.btn_index
        )

        left_panel.addWidget(
            self.btn_folder
        )

        left_panel.addWidget(
            self.btn_analyze
        )

        left_panel.addWidget(
            self.btn_duplicates
        )

        left_panel.addWidget(
            self.btn_settings
        )

        left_panel.addStretch()

        # Правая панель

        right_panel = QVBoxLayout()

        folder_title = QLabel(
            "Папка документации:"
        )

        self.folder_label = QLabel(
            "Папка не выбрана"
        )

        self.folder_label.setWordWrap(
            True
        )

        right_panel.addWidget(
            folder_title
        )

        right_panel.addWidget(
            self.folder_label
        )

        # Поиск

        search_layout = QHBoxLayout()

        self.search_input = QLineEdit()

        self.search_input.setPlaceholderText(
            "КС 55724, ОНК160, Е10..."
        )

        self.search_button = QPushButton(
            "Найти"
        )

        search_layout.addWidget(
            self.search_input
        )

        search_layout.addWidget(
            self.search_button
        )

        right_panel.addLayout(
            search_layout
        )

        # Таблица

        self.results = QTableWidget()

        self.results.setColumnCount(
            5
        )

        self.results.setHorizontalHeaderLabels([
            "Файл",
            "Тип",
            "Статус",
            "Фрагмент",
            "Полный путь",
        ])

        header = self.results.horizontalHeader()

        header.setSectionResizeMode(
            0,
            QHeaderView.ResizeToContents
        )

        header.setSectionResizeMode(
            1,
            QHeaderView.ResizeToContents
        )

        header.setSectionResizeMode(
            2,
            QHeaderView.ResizeToContents
        )

        header.setSectionResizeMode(
            3,
            QHeaderView.Stretch
        )

        header.setSectionResizeMode(
            4,
            QHeaderView.Stretch
        )

        self.results.setSelectionBehavior(
            QTableWidget.SelectRows
        )

        self.results.setEditTriggers(
            QTableWidget.NoEditTriggers
        )

        right_panel.addWidget(
            self.results
        )

        # Статус

        self.status = QLabel(
            "Готово"
        )

        self.status.setWordWrap(
            True
        )

        right_panel.addWidget(
            self.status
        )

        main_layout.addLayout(
            left_panel,
            1
        )

        main_layout.addLayout(
            right_panel,
            7
        )

        # Сигналы

        self.btn_folder.clicked.connect(
            self.select_folder
        )

        self.btn_index.clicked.connect(
            self.start_indexing
        )

        self.search_button.clicked.connect(
            self.perform_search
        )

        self.btn_search.clicked.connect(
            self.perform_search
        )

        self.btn_analyze.clicked.connect(
            self.start_analysis
        )

        self.btn_duplicates.clicked.connect(
            self.start_duplicate_search
        )

        self.search_input.returnPressed.connect(
            self.perform_search
        )

        self.results.cellDoubleClicked.connect(
            self.open_file
        )

    def load_settings(self):

        folder = self.settings.value(
            "documentation_folder",
            ""
        )

        if (
            folder
            and os.path.exists(folder)
        ):

            self.folder_label.setText(
                folder
            )

        else:

            self.folder_label.setText(
                "Папка не выбрана"
            )

    def select_folder(self):

        current_folder = self.settings.value(
            "documentation_folder",
            ""
        )

        if (
            not current_folder
            or not os.path.exists(
                current_folder
            )
        ):

            current_folder = ""

        folder = QFileDialog.getExistingDirectory(
            self,
            "Выберите папку с документацией",
            current_folder
        )

        if not folder:
            return

        self.settings.setValue(
            "documentation_folder",
            folder
        )

        self.folder_label.setText(
            folder
        )

        self.status.setText(
            "Папка сохранена"
        )

    # -------------------------------------------------
    # Индексация
    # -------------------------------------------------

    def start_indexing(self):

        folder = self.settings.value(
            "documentation_folder",
            ""
        )

        if not folder:

            self.status.setText(
                "Сначала выберите папку"
            )

            return

        if not os.path.exists(folder):

            self.status.setText(
                "Папка не найдена"
            )

            return

        self.btn_index.setEnabled(False)
        self.btn_folder.setEnabled(False)
        self.search_button.setEnabled(False)
        self.btn_analyze.setEnabled(False)
        self.btn_duplicates.setEnabled(False)

        self.status.setText(
            "Индексация: 0%"
        )

        self.worker = IndexWorker(
            folder
        )

        self.worker.progress.connect(
            self.indexing_progress
        )

        self.worker.finished.connect(
            self.indexing_finished
        )

        self.worker.error.connect(
            self.indexing_error
        )

        self.worker.start()

    def indexing_progress(
        self,
        current,
        total,
        filename,
        cloud
    ):

        percent = (
            int(current * 100 / total)
            if total
            else 100
        )

        status = (
            "облачный"
            if cloud
            else "локальный"
        )

        self.status.setText(
            f"Индексация: {percent}% — "
            f"{current} / {total}\n"
            f"{filename}\n"
            f"Статус: {status}"
        )

    def indexing_finished(
        self,
        added,
        updated,
        skipped,
        deleted,
        total
    ):

        self.btn_index.setEnabled(True)
        self.btn_folder.setEnabled(True)
        self.search_button.setEnabled(True)
        self.btn_duplicates.setEnabled(True)

        self.status.setText(
            f"Готово. Всего: {total}. "
            f"Новых: {added}. "
            f"Изменено: {updated}. "
            f"Без изменений: {skipped}. "
            f"Удалено: {deleted}."
        )

        self.worker.deleteLater()
        self.worker = None

    def indexing_error(
        self,
        message
    ):

        self.btn_index.setEnabled(True)
        self.btn_folder.setEnabled(True)
        self.search_button.setEnabled(True)
        self.btn_duplicates.setEnabled(True)

        self.status.setText(
            f"Ошибка: {message}"
        )

        self.worker.deleteLater()
        self.worker = None

    # -------------------------------------------------
    # Поиск
    # -------------------------------------------------

    def perform_search(self):

        query = self.search_input.text()

        if not query.strip():

            self.status.setText(
                "Введите запрос"
            )

            return

        self.status.setText(
            "Поиск..."
        )

        results = search_files(
            query
        )

        self.current_results = results

        self.results.setRowCount(0)

        cloud_count = 0

        for row, result in enumerate(
            results
        ):

            self.results.insertRow(row)

            self.results.setItem(
                row,
                0,
                QTableWidgetItem(
                    result.filename
                )
            )

            self.results.setItem(
                row,
                1,
                QTableWidgetItem(
                    result.extension
                )
            )

            if result.is_cloud:

                status = "☁ Облако"

                cloud_count += 1

            else:

                status = "💾 Локальный"

            self.results.setItem(
                row,
                2,
                QTableWidgetItem(
                    status
                )
            )

            self.results.setItem(
                row,
                3,
                QTableWidgetItem(
                    result.snippet
                )
            )

            self.results.setItem(
                row,
                4,
                QTableWidgetItem(
                    result.filepath
                )
            )

        self.btn_analyze.setEnabled(
            cloud_count > 0
        )

        self.status.setText(
            f"Найдено уникальных файлов: "
            f"{len(results)}. "
            f"Облачных: {cloud_count}."
        )

    # -------------------------------------------------
    # Анализ
    # -------------------------------------------------

    def start_analysis(self):

        if not self.current_results:

            self.status.setText(
                "Нет найденных документов"
            )

            return

        files = []
        seen_paths = set()

        for result in self.current_results:

            if not result.is_cloud:
                continue

            path = result.filepath.strip().lower()

            if path in seen_paths:
                continue

            seen_paths.add(path)

            files.append({
                "id": result.file_id,
                "filename": result.filename,
                "filepath": result.filepath,
		"is_cloud": result.is_cloud,
            })

        if not files:

            self.status.setText(
                "Облачных документов нет"
            )

            return

        self.btn_analyze.setEnabled(False)
        self.search_button.setEnabled(False)
        self.btn_index.setEnabled(False)
        self.btn_folder.setEnabled(False)
        self.btn_duplicates.setEnabled(False)

        self.status.setText(
            f"Анализ: 0 / {len(files)}"
        )

        self.analyzer = DocumentAnalyzer(
            files
        )

        self.analyzer.progress.connect(
            self.analysis_progress
        )

        self.analyzer.finished.connect(
            self.analysis_finished
        )

        self.analyzer.error.connect(
            self.analysis_error
        )

        self.analyzer.start()

    def analysis_progress(
        self,
        current,
        total,
        filename
    ):

        percent = (
            int(current * 100 / total)
            if total
            else 100
        )

        self.status.setText(
            f"Анализ: {percent}% — "
            f"{current} / {total}\n"
            f"{filename}"
        )

    def analysis_finished(
        self,
        analyzed,
        total
    ):

        self.btn_analyze.setEnabled(True)
        self.search_button.setEnabled(True)
        self.btn_index.setEnabled(True)
        self.btn_folder.setEnabled(True)
        self.btn_duplicates.setEnabled(True)

        self.status.setText(
            f"Анализ завершён. "
            f"Обработано: {analyzed} / {total}"
        )

        self.analyzer.deleteLater()
        self.analyzer = None

    def analysis_error(
        self,
        message
    ):

        self.btn_analyze.setEnabled(True)
        self.search_button.setEnabled(True)
        self.btn_index.setEnabled(True)
        self.btn_folder.setEnabled(True)
        self.btn_duplicates.setEnabled(True)

        self.status.setText(
            f"Ошибка анализа: {message}"
        )

        if self.analyzer:

            self.analyzer.deleteLater()
            self.analyzer = None

    # -------------------------------------------------
    # Поиск клонов
    # -------------------------------------------------

    def start_duplicate_search(self):

        self.btn_duplicates.setEnabled(
            False
        )

        self.btn_search.setEnabled(
            False
        )

        self.btn_index.setEnabled(
            False
        )

        self.btn_folder.setEnabled(
            False
        )

        self.btn_analyze.setEnabled(
            False
        )

        self.status.setText(
            "Поиск клонов..."
        )

        self.duplicate_worker = DuplicateWorker()

        self.duplicate_worker.finished.connect(
            self.duplicates_finished
        )

        self.duplicate_worker.error.connect(
            self.duplicates_error
        )

        self.duplicate_worker.start()

    def duplicates_finished(
        self,
        duplicates
    ):

        self.results.setRowCount(0)

        row = 0

        total_files = 0

        for group in duplicates:

            files = group["files"]

            total_files += len(files)

            for number, file in enumerate(
                files,
                start=1
            ):

                self.results.insertRow(
                    row
                )

                if number == 1:

                    name = (
                        f"КЛОН {file['filename']}"
                    )

                else:

                    name = (
                        f"  ↳ {file['filename']}"
                    )

                self.results.setItem(
                    row,
                    0,
                    QTableWidgetItem(
                        name
                    )
                )

                self.results.setItem(
                    row,
                    1,
                    QTableWidgetItem(
                        "Клон"
                    )
                )

                self.results.setItem(
                    row,
                    2,
                    QTableWidgetItem(
                        "100%"
                    )
                )

                self.results.setItem(
                    row,
                    3,
                    QTableWidgetItem(
                        "Идентичное содержимое"
                    )
                )

                self.results.setItem(
                    row,
                    4,
                    QTableWidgetItem(
                        file["filepath"]
                    )
                )

                row += 1

        self.btn_duplicates.setEnabled(True)
        self.btn_search.setEnabled(True)
        self.btn_index.setEnabled(True)
        self.btn_folder.setEnabled(True)

        if duplicates:

            self.status.setText(
                f"Найдено групп клонов: "
                f"{len(duplicates)}. "
                f"Файлов: {total_files}."
            )

        else:

            self.status.setText(
                "Клонов не найдено."
            )

        self.duplicate_worker.deleteLater()
        self.duplicate_worker = None

    def duplicates_error(
        self,
        message
    ):

        self.btn_duplicates.setEnabled(True)
        self.btn_search.setEnabled(True)
        self.btn_index.setEnabled(True)
        self.btn_folder.setEnabled(True)

        self.status.setText(
            f"Ошибка поиска клонов: "
            f"{message}"
        )

        if self.duplicate_worker:

            self.duplicate_worker.deleteLater()
            self.duplicate_worker = None

    # -------------------------------------------------
    # Открытие файла
    # -------------------------------------------------

    def open_file(
        self,
        row,
        column
    ):

        filepath_item = self.results.item(
            row,
            4
        )

        if filepath_item is None:
            return

        filepath = filepath_item.text()

        if os.path.exists(filepath):

            os.startfile(
                filepath
            )

        else:

            self.status.setText(
                "Файл не найден"
            )