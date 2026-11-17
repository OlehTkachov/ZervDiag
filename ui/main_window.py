import os
from pathlib import Path

from PySide6.QtCore import (
    QSettings,
    QThread,
    Signal,
)
from PySide6.QtWidgets import (
    QComboBox,
    QFileDialog,
    QHeaderView,
    QInputDialog,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from database.db import (
    create_database,
    get_ocr_pending_count,
    get_status_counts,
)
from database.libraries import (
    add_library as register_library,
    ensure_library_registry,
    get_active_library,
    get_active_library_id,
    get_libraries,
    remove_library,
    set_active_library,
    update_library_folder,
)
from indexer.indexer import (
    index_folder,
)
from indexer.ocr_queue import (
    process_ocr_queue,
)
from search.duplicates import (
    find_duplicates,
)
from search.search import (
    search_files,
)


class SearchWorker(QThread):
    finished_results = Signal(
        list
    )
    error = Signal(
        str
    )

    def __init__(
        self,
        query,
        parent=None,
    ):
        super().__init__(
            parent
        )

        self.query = query

    def run(self):
        try:
            self.finished_results.emit(
                search_files(
                    self.query
                )
            )

        except Exception as error:
            self.error.emit(
                str(error)
            )


class IndexWorker(QThread):
    progress = Signal(
        int,
        int,
        str,
        bool,
    )

    finished_index = Signal(
        int,
        int,
        int,
        int,
        int,
        bool,
    )

    error = Signal(
        str
    )

    def __init__(
        self,
        folder,
        parent=None,
    ):
        super().__init__(
            parent
        )

        self.folder = folder
        self._stop_requested = False

    def request_stop(
        self,
    ):
        self._stop_requested = True

    def should_stop(
        self,
    ):
        return (
            self._stop_requested
        )

    def report_progress(
        self,
        current,
        total,
        filename,
        cloud,
    ):
        self.progress.emit(
            current,
            total,
            filename,
            cloud,
        )

    def run(
        self,
    ):
        try:
            result = index_folder(
                self.folder,
                progress_callback=self.report_progress,
                stop_callback=self.should_stop,
            )

            self.finished_index.emit(
                *result,
                self._stop_requested,
            )

        except Exception as error:
            self.error.emit(
                str(error)
            )


class OCRWorker(QThread):
    progress = Signal(
        int,
        int,
        str,
        int,
        int,
    )

    finished_ocr = Signal(
        int,
        int,
        int,
        bool,
    )

    error = Signal(
        str
    )

    def __init__(
        self,
        parent=None,
    ):
        super().__init__(
            parent
        )

        self._stop_requested = False

    def request_stop(
        self,
    ):
        self._stop_requested = True

    def should_stop(
        self,
    ):
        return (
            self._stop_requested
        )

    def report_progress(
        self,
        file_number,
        file_total,
        filename,
        page_number,
        page_total,
    ):
        self.progress.emit(
            file_number,
            file_total,
            filename,
            page_number,
            page_total,
        )

    def run(
        self,
    ):
        try:
            result = (
                process_ocr_queue(
                    progress_callback=self.report_progress,
                    stop_callback=self.should_stop,
                )
            )

            self.finished_ocr.emit(
                *result
            )

        except Exception as error:
            self.error.emit(
                str(error)
            )


class DuplicateWorker(QThread):
    finished_results = Signal(
        list
    )
    error = Signal(
        str
    )

    def run(
        self,
    ):
        try:
            self.finished_results.emit(
                find_duplicates()
            )

        except Exception as error:
            self.error.emit(
                str(error)
            )


class MainWindow(QMainWindow):
    def __init__(
        self,
    ):
        super().__init__()

        self.setWindowTitle(
            "ZervDiag"
        )

        self.resize(
            1600,
            850,
        )

        self.index_worker = None
        self.ocr_worker = None
        self.search_worker = None
        self.duplicate_worker = None

        self.current_results = []

        self.settings = QSettings(
            "ZervDiag",
            "ZervDiag",
        )

        ensure_library_registry(
            legacy_folder=str(
                self.settings.value("documentation_folder", "") or ""
            )
        )
        self._active_library_id = get_active_library_id()
        create_database()

        self.create_ui()
        self.load_settings()
        self.update_ocr_button()

    def create_ui(
        self,
    ):
        central = QWidget()

        self.setCentralWidget(
            central
        )

        main_layout = QHBoxLayout(
            central
        )

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

        self.btn_ocr = QPushButton(
            "OCR очередь"
        )

        self.btn_folder = QPushButton(
            "Выбрать папку"
        )

        self.btn_duplicates = QPushButton(
            "Клоны"
        )

        self.btn_compare_databases = QPushButton(
            "Сравнить библиотеки"
        )
        # Keep the established attribute name for the branding layer.
        self.btn_compare_libraries = self.btn_compare_databases

        self.btn_settings = QPushButton(
            "Настройки"
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
            self.btn_ocr
        )
        left_panel.addWidget(
            self.btn_folder
        )
        left_panel.addWidget(
            self.btn_duplicates
        )
        left_panel.addWidget(
            self.btn_compare_databases
        )
        left_panel.addWidget(
            self.btn_settings
        )
        left_panel.addStretch()

        right_panel = QVBoxLayout()

        library_layout = QHBoxLayout()
        library_layout.addWidget(QLabel("Библиотека:"))
        self.library_combo = QComboBox()
        self.library_combo.setMinimumWidth(240)
        library_layout.addWidget(self.library_combo, 1)
        self.btn_add_library = QPushButton("Добавить библиотеку")
        self.btn_add_library.setToolTip(
            "Создать отдельную библиотеку для другой папки документов"
        )
        library_layout.addWidget(self.btn_add_library)
        right_panel.addLayout(library_layout)

        right_panel.addWidget(
            QLabel(
                "Папка активной библиотеки:"
            )
        )

        self.folder_label = QLabel(
            "Папка не выбрана"
        )

        self.folder_label.setWordWrap(
            True
        )

        right_panel.addWidget(
            self.folder_label
        )

        search_layout = QHBoxLayout()

        self.search_input = QLineEdit()

        self.search_input.setPlaceholderText(
            "КС 55724, ОНК160, E10, Terex AC35L..."
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

        self.results = QTableWidget()

        self.results.setColumnCount(
            5
        )

        self.results.setHorizontalHeaderLabels(
            [
                "Файл",
                "Тип",
                "Статус",
                "Фрагмент",
                "Полный путь",
            ]
        )

        header = (
            self.results
            .horizontalHeader()
        )

        header.setSectionResizeMode(
            0,
            QHeaderView.ResizeToContents,
        )

        header.setSectionResizeMode(
            1,
            QHeaderView.ResizeToContents,
        )

        header.setSectionResizeMode(
            2,
            QHeaderView.ResizeToContents,
        )

        header.setSectionResizeMode(
            3,
            QHeaderView.Stretch,
        )

        header.setSectionResizeMode(
            4,
            QHeaderView.Stretch,
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
            1,
        )

        main_layout.addLayout(
            right_panel,
            7,
        )

        self.btn_folder.clicked.connect(
            self.select_folder
        )

        self.library_combo.currentIndexChanged.connect(
            self.change_library
        )
        self.btn_add_library.clicked.connect(
            self.add_library
        )

        self.btn_index.clicked.connect(
            self.toggle_indexing
        )

        self.btn_ocr.clicked.connect(
            self.toggle_ocr
        )

        self.search_button.clicked.connect(
            self.perform_search
        )

        self.btn_search.clicked.connect(
            self.perform_search
        )

        self.btn_duplicates.clicked.connect(
            self.start_duplicate_search
        )

        self.btn_compare_databases.clicked.connect(
            self.open_database_comparison
        )

        self.search_input.returnPressed.connect(
            self.perform_search
        )

        self.results.cellDoubleClicked.connect(
            self.open_file
        )

    def load_settings(
        self,
    ):
        self._refresh_library_selector()

    def _database_workers_running(self):
        return any(
            worker is not None and worker.isRunning()
            for worker in (
                self.index_worker,
                self.ocr_worker,
                self.search_worker,
                self.duplicate_worker,
            )
        )

    def _refresh_library_selector(self):
        libraries = get_libraries()
        active_id = get_active_library_id()
        self.library_combo.blockSignals(True)
        self.library_combo.clear()
        for library in libraries:
            self.library_combo.addItem(
                library["name"],
                library["id"],
            )
        selected = self.library_combo.findData(active_id)
        if selected >= 0:
            self.library_combo.setCurrentIndex(selected)
        self.library_combo.blockSignals(False)
        self._active_library_id = active_id
        self._update_library_folder_label()

    def _update_library_folder_label(self):
        library = get_active_library()
        folder = library.get("source_folder") or ""
        self.folder_label.setText(
            folder or "Папка не выбрана — выберите её или добавьте библиотеку"
        )
        self.folder_label.setToolTip(
            f"База библиотеки: {library['database_path']}"
        )
        self.library_combo.setToolTip(
            f"Активная библиотека: {library['name']}"
        )

    def change_library(self, _index=None):
        library_id = self.library_combo.currentData()
        if not library_id or library_id == self._active_library_id:
            return

        if self._database_workers_running():
            self.library_combo.blockSignals(True)
            selected = self.library_combo.findData(self._active_library_id)
            if selected >= 0:
                self.library_combo.setCurrentIndex(selected)
            self.library_combo.blockSignals(False)
            QMessageBox.information(
                self,
                "Смена библиотеки",
                "Сначала дождитесь завершения поиска, индексации, OCR или поиска клонов.",
            )
            return

        previous_id = self._active_library_id
        try:
            set_active_library(library_id)
            create_database()
        except Exception as error:
            if previous_id:
                set_active_library(previous_id)
            self._refresh_library_selector()
            QMessageBox.critical(
                self,
                "Не удалось открыть библиотеку",
                f"{type(error).__name__}: {error}",
            )
            return

        self._active_library_id = library_id
        self.results.setRowCount(0)
        self.current_results = []
        self._update_library_folder_label()
        ocr_count = self.update_ocr_button()
        library = get_active_library()
        self.status.setText(
            f"Активна библиотека «{library['name']}». "
            f"OCR ожидают: {ocr_count}."
        )

    def add_library(self):
        if self._database_workers_running():
            QMessageBox.information(
                self,
                "Добавление библиотеки",
                "Сначала дождитесь завершения текущей операции с базой.",
            )
            return

        active = get_active_library()
        start_folder = active.get("source_folder") or str(Path.home())
        folder = QFileDialog.getExistingDirectory(
            self,
            "Выберите папку новой библиотеки",
            start_folder,
        )
        if not folder:
            return

        suggested_name = Path(folder).name or "Новая библиотека"
        name, accepted = QInputDialog.getText(
            self,
            "Новая библиотека",
            "Название библиотеки:",
            QLineEdit.EchoMode.Normal,
            suggested_name,
        )
        if not accepted:
            return

        library = None
        try:
            library = register_library(name, folder)
            create_database(database_path=library["database_path"])
            set_active_library(library["id"])
        except Exception as error:
            if library is not None:
                try:
                    remove_library(library["id"], delete_database=True)
                except Exception:
                    pass
            QMessageBox.critical(
                self,
                "Не удалось добавить библиотеку",
                f"{type(error).__name__}: {error}",
            )
            return

        self._active_library_id = library["id"]
        self._refresh_library_selector()
        self.results.setRowCount(0)
        self.current_results = []
        self.update_ocr_button()
        self.status.setText(
            f"Создана отдельная библиотека «{library['name']}». "
            "Теперь можно запустить её индексацию."
        )

    def update_ocr_button(
        self,
    ):
        try:
            count = (
                get_ocr_pending_count()
            )

        except Exception:
            count = 0

        self.btn_ocr.setText(
            f"OCR очередь ({count})"
        )

        return count

    def select_folder(
        self,
    ):
        library = get_active_library()
        current_folder = library.get("source_folder") or ""
        folder = QFileDialog.getExistingDirectory(
            self,
            "Выберите папку активной библиотеки",
            current_folder if os.path.isdir(current_folder) else str(Path.home()),
        )
        if not folder:
            return

        if current_folder and os.path.normcase(os.path.abspath(current_folder)) != os.path.normcase(os.path.abspath(folder)):
            answer = QMessageBox.question(
                self,
                "Изменение папки библиотеки",
                "В этой библиотеке уже могут храниться записи из прежней папки. "
                "Смена корневой папки не переносит и не удаляет старые записи.\n\n"
                "Если это вторая независимая коллекция документов, нажмите «Нет» "
                "и используйте кнопку «Добавить библиотеку».\n\n"
                "Всё равно изменить папку текущей библиотеки?",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.No,
            )
            if answer != QMessageBox.StandardButton.Yes:
                return

        try:
            update_library_folder(self._active_library_id, folder)
        except Exception as error:
            QMessageBox.warning(
                self,
                "Папка не сохранена",
                f"{type(error).__name__}: {error}",
            )
            return

        self._update_library_folder_label()
        self.status.setText(
            "Папка активной библиотеки сохранена."
        )

    # ---------------------------
    # Быстрая индексация
    # ---------------------------

    def toggle_indexing(
        self,
    ):
        if (
            self.index_worker
            and self.index_worker.isRunning()
        ):
            self.index_worker.request_stop()

            self.btn_index.setEnabled(
                False
            )

            self.status.setText(
                "Остановка индексации "
                "после текущего файла..."
            )

            return

        self.start_indexing()

    def start_indexing(
        self,
    ):
        library = get_active_library()
        folder = library.get("source_folder") or ""

        if not folder:
            self.status.setText(
                "У активной библиотеки не выбрана папка. Выберите папку или добавьте библиотеку."
            )
            return

        if not os.path.exists(
            folder
        ):
            self.status.setText(
                "Папка не найдена"
            )
            return

        if (
            self.ocr_worker
            and self.ocr_worker.isRunning()
        ):
            self.status.setText(
                "Сначала остановите OCR"
            )
            return

        self.btn_index.setText(
            "Остановить индексацию"
        )

        self.btn_folder.setEnabled(
            False
        )

        self.btn_ocr.setEnabled(
            False
        )

        self.btn_duplicates.setEnabled(
            False
        )
        self.library_combo.setEnabled(False)
        self.btn_add_library.setEnabled(False)

        self.status.setText(
            f"Индексация библиотеки «{library['name']}»: 0%"
        )

        self.index_worker = IndexWorker(
            folder,
            self,
        )

        self.index_worker.progress.connect(
            self.indexing_progress
        )

        self.index_worker.finished_index.connect(
            self.indexing_finished
        )

        self.index_worker.error.connect(
            self.indexing_error
        )

        self.index_worker.start()

    def indexing_progress(
        self,
        current,
        total,
        filename,
        cloud,
    ):
        percent = (
            current
            * 100.0
            / total
            if total
            else 100.0
        )

        state = (
            "облачный"
            if cloud
            else "локальный"
        )

        self.status.setText(
            f"Быстрая индексация: "
            f"{percent:.1f}% — "
            f"{current} / {total}\n"
            f"{filename}\n"
            f"Статус: {state}"
        )

    def indexing_finished(
        self,
        added,
        updated,
        skipped,
        deleted,
        total,
        stopped,
    ):
        self._index_controls_ready()

        prefix = (
            "Индексация остановлена."
            if stopped
            else "Быстрая индексация завершена."
        )

        ocr_count = (
            self.update_ocr_button()
        )

        counts = get_status_counts()

        self.status.setText(
            f"{prefix} "
            f"Всего: {total}. "
            f"Новых: {added}. "
            f"Обработано/изменено: {updated}. "
            f"Пропущено: {skipped}. "
            f"Удалено: {deleted}. "
            f"OCR ожидают: {ocr_count}. "
            f"OK: {counts.get('ok', 0)}."
        )

        if self.index_worker:
            self.index_worker.deleteLater()
            self.index_worker = None

    def indexing_error(
        self,
        message,
    ):
        self._index_controls_ready()

        self.status.setText(
            f"Ошибка индексации: "
            f"{message}"
        )

        if self.index_worker:
            self.index_worker.deleteLater()
            self.index_worker = None

    def _index_controls_ready(
        self,
    ):
        self.btn_index.setEnabled(
            True
        )

        self.btn_index.setText(
            "Индексация"
        )

        self.btn_folder.setEnabled(
            True
        )

        self.btn_ocr.setEnabled(
            True
        )

        self.btn_duplicates.setEnabled(
            True
        )
        self.library_combo.setEnabled(True)
        self.btn_add_library.setEnabled(True)

    # ---------------------------
    # OCR очередь
    # ---------------------------

    def toggle_ocr(
        self,
    ):
        if (
            self.ocr_worker
            and self.ocr_worker.isRunning()
        ):
            self.ocr_worker.request_stop()

            self.btn_ocr.setEnabled(
                False
            )

            self.status.setText(
                "Остановка OCR после "
                "текущей страницы..."
            )

            return

        self.start_ocr()

    def start_ocr(
        self,
    ):
        if (
            self.index_worker
            and self.index_worker.isRunning()
        ):
            self.status.setText(
                "Сначала остановите индексацию"
            )
            return

        count = (
            self.update_ocr_button()
        )

        if count <= 0:
            self.status.setText(
                "OCR очередь пуста"
            )
            return

        self.btn_ocr.setText(
            "Остановить OCR"
        )

        self.btn_index.setEnabled(
            False
        )

        self.btn_folder.setEnabled(
            False
        )

        self.btn_duplicates.setEnabled(
            False
        )
        self.library_combo.setEnabled(False)
        self.btn_add_library.setEnabled(False)

        self.status.setText(
            f"OCR очередь: "
            f"{count} файлов"
        )

        self.ocr_worker = OCRWorker(
            self
        )

        self.ocr_worker.progress.connect(
            self.ocr_progress
        )

        self.ocr_worker.finished_ocr.connect(
            self.ocr_finished
        )

        self.ocr_worker.error.connect(
            self.ocr_error
        )

        self.ocr_worker.start()

    def ocr_progress(
        self,
        file_number,
        file_total,
        filename,
        page_number,
        page_total,
    ):
        page_percent = (
            page_number
            * 100.0
            / page_total
            if page_total
            else 0.0
        )

        self.status.setText(
            f"OCR: файл "
            f"{file_number}/{file_total}\n"
            f"{filename}\n"
            f"Страница "
            f"{page_number}/{page_total} "
            f"({page_percent:.1f}%)"
        )

    def ocr_finished(
        self,
        processed,
        errors,
        total,
        stopped,
    ):
        self._ocr_controls_ready()

        remaining = (
            self.update_ocr_button()
        )

        prefix = (
            "OCR остановлен."
            if stopped
            else "OCR очередь завершена."
        )

        self.status.setText(
            f"{prefix} "
            f"Готово: {processed}. "
            f"Ошибок: {errors}. "
            f"Было в очереди: {total}. "
            f"Осталось: {remaining}."
        )

        if self.ocr_worker:
            self.ocr_worker.deleteLater()
            self.ocr_worker = None

    def ocr_error(
        self,
        message,
    ):
        self._ocr_controls_ready()

        self.update_ocr_button()

        self.status.setText(
            f"Ошибка OCR: "
            f"{message}"
        )

        if self.ocr_worker:
            self.ocr_worker.deleteLater()
            self.ocr_worker = None

    def _ocr_controls_ready(
        self,
    ):
        self.btn_ocr.setEnabled(
            True
        )

        self.btn_index.setEnabled(
            True
        )

        self.btn_folder.setEnabled(
            True
        )

        self.btn_duplicates.setEnabled(
            True
        )
        self.library_combo.setEnabled(True)
        self.btn_add_library.setEnabled(True)

    # ---------------------------
    # Поиск
    # ---------------------------

    def perform_search(
        self,
    ):
        query = (
            self.search_input
            .text()
            .strip()
        )

        if not query:
            self.status.setText(
                "Введите запрос"
            )
            return

        if (
            self.search_worker
            and self.search_worker.isRunning()
        ):
            return

        self.status.setText(
            f"Поиск: {query}"
        )

        self.search_button.setEnabled(
            False
        )

        self.btn_search.setEnabled(
            False
        )

        self.search_worker = SearchWorker(
            query,
            self,
        )

        self.search_worker.finished_results.connect(
            self.search_finished
        )

        self.search_worker.error.connect(
            self.search_error
        )

        self.search_worker.start()

    def search_finished(
        self,
        results,
    ):
        self.current_results = (
            results
        )

        self.results.setRowCount(
            0
        )

        cloud_count = 0

        for row, result in enumerate(
            results
        ):
            self.results.insertRow(
                row
            )

            self.results.setItem(
                row,
                0,
                QTableWidgetItem(
                    result.filename
                ),
            )

            self.results.setItem(
                row,
                1,
                QTableWidgetItem(
                    result.extension
                ),
            )

            if result.is_cloud:
                state = "☁ Облако"
                cloud_count += 1
            else:
                state = "💾 Локальный"

            self.results.setItem(
                row,
                2,
                QTableWidgetItem(
                    state
                ),
            )

            self.results.setItem(
                row,
                3,
                QTableWidgetItem(
                    result.snippet
                ),
            )

            self.results.setItem(
                row,
                4,
                QTableWidgetItem(
                    result.filepath
                ),
            )

        self.search_button.setEnabled(
            True
        )

        self.btn_search.setEnabled(
            True
        )

        self.status.setText(
            f"Найдено: "
            f"{len(results)}. "
            f"Cloud: {cloud_count}."
        )

        if self.search_worker:
            self.search_worker.deleteLater()
            self.search_worker = None

    def search_error(
        self,
        message,
    ):
        self.search_button.setEnabled(
            True
        )

        self.btn_search.setEnabled(
            True
        )

        self.status.setText(
            f"Ошибка поиска: "
            f"{message}"
        )

        if self.search_worker:
            self.search_worker.deleteLater()
            self.search_worker = None

    # ---------------------------
    # Клоны
    # ---------------------------

    def start_duplicate_search(
        self,
    ):
        if (
            self.duplicate_worker
            and self.duplicate_worker.isRunning()
        ):
            return

        self.btn_duplicates.setEnabled(
            False
        )

        self.status.setText(
            "Поиск клонов..."
        )

        self.duplicate_worker = DuplicateWorker(
            self
        )

        self.duplicate_worker.finished_results.connect(
            self.duplicates_finished
        )

        self.duplicate_worker.error.connect(
            self.duplicates_error
        )

        self.duplicate_worker.start()

    def duplicates_finished(
        self,
        duplicates,
    ):
        self.results.setRowCount(
            0
        )

        row = 0
        total_files = 0

        for group in duplicates:
            group_files = group[
                "files"
            ]

            total_files += len(
                group_files
            )

            for number, file in enumerate(
                group_files,
                start=1,
            ):
                self.results.insertRow(
                    row
                )

                name = (
                    f"КЛОН "
                    f"{file['filename']}"
                    if number == 1
                    else
                    f"  → "
                    f"{file['filename']}"
                )

                values = [
                    name,
                    "Клон",
                    "100%",
                    "Идентичное содержимое",
                    file["filepath"],
                ]

                for column, value in enumerate(
                    values
                ):
                    self.results.setItem(
                        row,
                        column,
                        QTableWidgetItem(
                            value
                        ),
                    )

                row += 1

        self.btn_duplicates.setEnabled(
            True
        )

        if duplicates:
            self.status.setText(
                f"Групп клонов: "
                f"{len(duplicates)}. "
                f"Файлов: {total_files}."
            )

        else:
            self.status.setText(
                "Клонов не найдено."
            )

        if self.duplicate_worker:
            self.duplicate_worker.deleteLater()
            self.duplicate_worker = None

    def duplicates_error(
        self,
        message,
    ):
        self.btn_duplicates.setEnabled(
            True
        )

        self.status.setText(
            f"Ошибка поиска клонов: "
            f"{message}"
        )

        if self.duplicate_worker:
            self.duplicate_worker.deleteLater()
            self.duplicate_worker = None

    def open_database_comparison(self):
        if (
            self.index_worker
            and self.index_worker.isRunning()
        ) or (
            self.ocr_worker
            and self.ocr_worker.isRunning()
        ):
            self.status.setText(
                "Остановите индексацию и OCR перед сравнением баз."
            )
            return

        from ui.library_compare_dialog import LibraryCompareDialog

        dialog = LibraryCompareDialog(self)
        dialog.exec()

    def open_file(
        self,
        row,
        column,
    ):
        filepath_item = (
            self.results.item(
                row,
                4,
            )
        )

        if filepath_item is None:
            return

        filepath = (
            filepath_item.text()
        )

        if os.path.exists(
            filepath
        ):
            os.startfile(
                filepath
            )

        else:
            self.status.setText(
                "Файл не найден"
            )
