import sys

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QApplication, QLabel

from auditor.gui import AuditorWindow


class AuditorWindowWithPathPreview(AuditorWindow):
    """Auditor window with an always-visible full path for the selected file."""

    def _build_files_tab(self):
        super()._build_files_tab()

        page = self.tabs.widget(self.tabs.count() - 1)
        self.selected_path_label = QLabel("Полный путь: —")
        self.selected_path_label.setWordWrap(True)
        self.selected_path_label.setTextInteractionFlags(
            Qt.TextSelectableByMouse
        )
        self.selected_path_label.setStyleSheet(
            "padding: 5px; font-weight: bold;"
        )
        page.layout().addWidget(self.selected_path_label)

        self.files_table.itemSelectionChanged.connect(
            self._update_selected_path
        )

    def _update_selected_path(self):
        records = self._selected_records()
        if not records:
            self.selected_path_label.setText("Полный путь: —")
            return

        if len(records) == 1:
            self.selected_path_label.setText(
                f"Полный путь: {records[0].filepath}"
            )
        else:
            self.selected_path_label.setText(
                f"Выбрано файлов: {len(records)} | "
                f"Первый: {records[0].filepath}"
            )


def main():
    app = QApplication(sys.argv)
    window = AuditorWindowWithPathPreview()
    window.showMaximized()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
