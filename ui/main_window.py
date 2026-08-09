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
from PySide6.QtCore import QThread, Signal

from database.db import create_database
from indexer.indexer import index_folder
from search.search import search_files


class IndexWorker(QThread):
    finished = Signal(int, int, int)
    error = Signal(str)

    def __init__(self, folder):
        super().__init__()
        self.folder = folder

    def run(self):
        try:
            result = index_folder(self.folder)
            self.finished.emit(*result)
        except Exception as error:
            self.error.emit(str(error))


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()

        self.setWindowTitle("ZervDiag")
        self.resize(1200, 750)

        self.worker = None

        create_database()
        self.create_ui()

    def create_ui(self):
        central = QWidget()
        self.setCentralWidget(central)

        main_layout = QHBoxLayout(central)

        left_panel = QVBoxLayout()

        title = QLabel("ZervDiag")
        title.setStyleSheet(
            "font-size: 24px; font-weight: bold; padding: 10px;"
        )

        self.btn_search = QPushButton("Поиск")
        self.btn_index = QPushButton("Индексация")
        self.btn_settings = QPushButton("Настройки")

        left_panel.addWidget(title)
        left_panel.addWidget(self.btn_search)
        left_panel.addWidget(self.btn_index)
        left_panel.addWidget(self.btn_settings)
        left_panel.addStretch()

        right_panel = QVBoxLayout()

        search_layout = QHBoxLayout()

        self.search_input = QLineEdit()
        self.search_input.setPlaceholderText(
            "Введите запрос: КС 55724, ОНК160, Е10..."
        )

        self.search_button = QPushButton("Найти")

        search_layout.addWidget(self.search_input)
        search_layout.addWidget(self.search_button)

        right_panel.addLayout(search_layout)

        self.results = QTableWidget()
        self.results.setColumnCount(3)

        self.results.setHorizontalHeaderLabels(
            ["Файл", "Тип", "Путь"]
        )

        self.results.horizontalHeader().setSectionResizeMode(
            0,
            QHeaderView.Stretch
        )

        self.results.horizontalHeader().setSectionResizeMode(
            1,
            QHeaderView.ResizeToContents
        )

        self.results.horizontalHeader().setSectionResizeMode(
            2,
            QHeaderView.Stretch
        )

        self.results.setSelectionBehavior(
            QTableWidget.SelectRows
        )

        self.results.setEditTriggers(
            QTableWidget.NoEditTriggers
        )

        right_panel.addWidget(self.results)

        self.status = QLabel("Готово")
        right_panel.addWidget(self.status)

        main_layout.addLayout(left_panel, 1)
        main_layout.addLayout(right_panel, 4)

        self.btn_index.clicked.connect(
            self.start_indexing
        )

        self.search_button.clicked.connect(
            self.perform_search
        )

        self.search_input.returnPressed.connect(
            self.perform_search
        )

        self.results.cellDoubleClicked.connect(
            self.open_file
        )

    def start_indexing(self):
        folder = QFileDialog.getExistingDirectory(
            self,
            "Выберите папку для индексации"
        )

        if not folder:
            return

        self.btn_index.setEnabled(False)
        self.search_button.setEnabled(False)

        self.status.setText(
            "Индексация выполняется..."
        )

        self.worker = IndexWorker(folder)

        self.worker.finished.connect(
            self.indexing_finished
        )

        self.worker.error.connect(
            self.indexing_error
        )

        self.worker.start()

    def indexing_finished(
        self,
        added,
        updated,
        total
    ):
        self.btn_index.setEnabled(True)
        self.search_button.setEnabled(True)

        self.status.setText(
            f"Готово. Файлов: {total}. "
            f"Добавлено: {added}. "
            f"Обновлено: {updated}."
        )

        self.worker.deleteLater()
        self.worker = None

    def indexing_error(self, message):
        self.btn_index.setEnabled(True)
        self.search_button.setEnabled(True)

        self.status.setText(
            f"Ошибка индексации: {message}"
        )

        self.worker.deleteLater()
        self.worker = None

    def perform_search(self):
        query = self.search_input.text()

        results = search_files(query)

        self.results.setRowCount(0)

        for row, result in enumerate(results):
            self.results.insertRow(row)

            filename, extension, filepath = result

            self.results.setItem(
                row,
                0,
                QTableWidgetItem(filename)
            )

            self.results.setItem(
                row,
                1,
                QTableWidgetItem(extension)
            )

            self.results.setItem(
                row,
                2,
                QTableWidgetItem(filepath)
            )

        self.status.setText(
            f"Найдено: {len(results)}"
        )

    def open_file(self, row, column):
        filepath_item = self.results.item(row, 2)

        if filepath_item is None:
            return

        filepath = filepath_item.text()

        if os.path.exists(filepath):
            os.startfile(filepath)
        else:
            self.status.setText(
                "Файл не найден"
            )
            