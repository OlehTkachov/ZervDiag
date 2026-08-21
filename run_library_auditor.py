import sys
from pathlib import Path

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QApplication,
    QHBoxLayout,
    QLabel,
    QMessageBox,
    QPushButton,
)

from auditor.engine import summarize
from auditor.exclusions import ExclusionStore
from auditor.gui import AuditorWindow


class AuditorWindowWithUserRules(AuditorWindow):
    """Auditor with persistent user-approved files/folders and full-path preview."""

    def __init__(self):
        self.exclusions = ExclusionStore()
        super().__init__()

    def _build_files_tab(self):
        super()._build_files_tab()

        self.category_filter.addItem(
            "Исключённые пользователем",
            "excluded",
        )

        page = self.tabs.widget(self.tabs.count() - 1)

        decision_row = QHBoxLayout()
        self.btn_keep_file = QPushButton("Файл нужен")
        self.btn_keep_file.setToolTip(
            "Исключить выбранный файл из последующих проверок Auditor"
        )
        self.btn_keep_folder = QPushButton("Папка нормальная")
        self.btn_keep_folder.setToolTip(
            "Исключить папку выбранного файла и всё её содержимое "
            "из последующих проверок Auditor"
        )
        self.btn_restore = QPushButton("Вернуть в проверку")
        self.btn_restore.setToolTip(
            "Удалить пользовательское исключение для выбранного файла или папки"
        )

        decision_row.addWidget(QLabel("Решение пользователя:"))
        decision_row.addWidget(self.btn_keep_file)
        decision_row.addWidget(self.btn_keep_folder)
        decision_row.addWidget(self.btn_restore)
        decision_row.addStretch(1)

        page.layout().insertLayout(1, decision_row)

        self.selected_path_label = QLabel("Полный путь: —")
        self.selected_path_label.setWordWrap(True)
        self.selected_path_label.setTextInteractionFlags(
            Qt.TextSelectableByMouse
        )
        self.selected_path_label.setStyleSheet(
            "padding: 5px; font-weight: bold;"
        )
        page.layout().addWidget(self.selected_path_label)

        self.btn_keep_file.clicked.connect(self._mark_files_needed)
        self.btn_keep_folder.clicked.connect(self._mark_folder_normal)
        self.btn_restore.clicked.connect(self._restore_selected_rules)
        self.files_table.itemSelectionChanged.connect(
            self._update_selected_path
        )

    def _set_busy(self, busy):
        super()._set_busy(busy)
        enabled = not busy
        self.btn_keep_file.setEnabled(enabled)
        self.btn_keep_folder.setEnabled(enabled)
        self.btn_restore.setEnabled(enabled)

    def _is_user_excluded(self, record):
        # Программатика остаётся в своей защищённой категории всегда.
        if record.category == "programmatic":
            return False
        return self.exclusions.match(record.filepath) is not None

    def _active_records(self):
        return [
            record
            for record in self.records
            if not self._is_user_excluded(record)
        ]

    def _excluded_records(self):
        return [
            record
            for record in self.records
            if self._is_user_excluded(record)
        ]

    def _load_finished(self, records):
        self.exclusions.reload()
        super()._load_finished(records)
        self._refresh_summary_with_exclusions()

    def _refresh_summary_with_exclusions(self):
        active = self._active_records()
        excluded = self._excluded_records()
        counts = summarize(active)
        file_rules, folder_rules = self.exclusions.rule_counts()

        self.summary_label.setText(
            f"Всего: {len(self.records):,}   |   "
            f"Исключено пользователем: {len(excluded):,} "
            f"({file_rules} файлов / {folder_rules} папок)   |   "
            f"Программатика: {counts['programmatic']:,}   |   "
            f"Вероятно полезные: {counts['useful']:,}   |   "
            f"Проверить: {counts['review']:,}   |   "
            f"Подозрительные: {counts['suspicious']:,}"
        )

    def _apply_file_filter(self):
        category = self.category_filter.currentData() or "all"

        if category != "excluded":
            original = self.records
            self.records = self._active_records()
            try:
                super()._apply_file_filter()
            finally:
                self.records = original
            return

        text = self.text_filter.text().strip().casefold()
        result = []

        for record in self._excluded_records():
            match = self.exclusions.match(record.filepath)
            rule_text = ""
            if match:
                kind, path = match
                rule_text = f"{kind} {path}"

            if text:
                haystack = " ".join(
                    [
                        record.filename,
                        record.filepath,
                        record.extension,
                        record.extraction_status,
                        " ".join(record.reasons),
                        rule_text,
                    ]
                ).casefold()

                if text not in haystack:
                    continue

            result.append(record)

        result.sort(
            key=lambda record: (
                record.filename.casefold(),
                record.filepath.casefold(),
            )
        )

        self.filtered_records = result
        super()._fill_files_table(result)

        for row, record in enumerate(result):
            match = self.exclusions.match(record.filepath)
            if not match:
                continue

            kind, path = match
            reason = (
                f"Исключено пользователем: "
                f"{'файл нужен' if kind == 'file' else 'папка нормальная'}"
            )
            if kind == "folder":
                reason += f" | правило папки: {path}"

            score_item = self.files_table.item(row, 0)
            category_item = self.files_table.item(row, 1)
            reason_item = self.files_table.item(row, 7)

            if score_item:
                score_item.setText("—")
            if category_item:
                category_item.setText("Исключён пользователем")
            if reason_item:
                reason_item.setText(reason)

        self.status.setText(
            f"Показано пользовательских исключений: {len(result):,}"
        )

    def _refresh_groups(self):
        original = self.records
        self.records = self._active_records()
        try:
            super()._refresh_groups()
        finally:
            self.records = original

    def _refresh_duplicates(self):
        original = self.records
        self.records = self._active_records()
        try:
            super()._refresh_duplicates()
        finally:
            self.records = original

    def _update_selected_path(self):
        records = self._selected_records()
        if not records:
            self.selected_path_label.setText("Полный путь: —")
            return

        if len(records) == 1:
            record = records[0]
            text = f"Полный путь: {record.filepath}"
            match = self.exclusions.match(record.filepath)
            if match and record.category != "programmatic":
                kind, path = match
                if kind == "file":
                    text += "   |   Исключение: файл нужен"
                else:
                    text += f"   |   Исключение: папка нормальная ({path})"
            self.selected_path_label.setText(text)
        else:
            self.selected_path_label.setText(
                f"Выбрано файлов: {len(records)} | "
                f"Первый: {records[0].filepath}"
            )

    def _mark_files_needed(self):
        records = [
            record
            for record in self._selected_records()
            if record.category != "programmatic"
        ]

        if not records:
            QMessageBox.information(
                self,
                "Файл нужен",
                "Выберите один или несколько обычных файлов.",
            )
            return

        answer = QMessageBox.question(
            self,
            "Пометить как нужные?",
            f"Файлов: {len(records)}.\n\n"
            "Они останутся в библиотеке и в основной базе ZervDiag, "
            "но Library Auditor больше не будет учитывать их при ревизии.\n\n"
            "Продолжить?",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.Yes,
        )

        if answer != QMessageBox.Yes:
            return

        for record in records:
            self.exclusions.add_file(record.filepath)

        self._start_load()

    def _mark_folder_normal(self):
        records = self._selected_records()

        if not records:
            QMessageBox.information(
                self,
                "Папка нормальная",
                "Выберите любой файл из папки, которую нужно исключить.",
            )
            return

        folder = str(Path(records[0].filepath).parent)

        answer = QMessageBox.question(
            self,
            "Папка нормальная?",
            "Library Auditor перестанет учитывать эту папку "
            "И ВСЕ ЕЁ ВЛОЖЕННЫЕ ПАПКИ при последующих пересчётах:\n\n"
            f"{folder}\n\n"
            "Файлы никуда не перемещаются и из основной базы не удаляются.\n\n"
            "Продолжить?",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )

        if answer != QMessageBox.Yes:
            return

        self.exclusions.add_folder(folder)
        self._start_load()

    def _restore_selected_rules(self):
        records = self._selected_records()

        if not records:
            QMessageBox.information(
                self,
                "Вернуть в проверку",
                "Сначала выберите файл из списка исключённых.",
            )
            return

        matches = {}
        for record in records:
            match = self.exclusions.match(record.filepath)
            if match and record.category != "programmatic":
                matches[(match[0], match[1])] = match

        if not matches:
            QMessageBox.information(
                self,
                "Вернуть в проверку",
                "Для выбранных файлов пользовательских исключений нет.",
            )
            return

        lines = []
        for kind, path in list(matches.values())[:8]:
            prefix = "Файл" if kind == "file" else "Папка"
            lines.append(f"{prefix}: {path}")

        if len(matches) > 8:
            lines.append(f"... и ещё {len(matches) - 8}")

        answer = QMessageBox.question(
            self,
            "Удалить исключение?",
            "Эти правила будут удалены, и соответствующие файлы "
            "снова попадут в ревизию:\n\n"
            + "\n".join(lines)
            + "\n\nПродолжить?",
            QMessageBox.Yes | QMessageBox.No,
            QMessageBox.No,
        )

        if answer != QMessageBox.Yes:
            return

        for kind, path in matches.values():
            self.exclusions.remove(kind, path)

        self._start_load()

    def _quarantine_selected(self):
        records = self._selected_records()

        if any(self._is_user_excluded(record) for record in records):
            QMessageBox.warning(
                self,
                "Исключено пользователем",
                "Файл или папка помечены как нужные/нормальные. "
                "Auditor не позволит отправить их в карантин, "
                "пока пользовательское исключение не будет снято.",
            )
            return

        super()._quarantine_selected()


def main():
    app = QApplication(sys.argv)
    window = AuditorWindowWithUserRules()
    window.showMaximized()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
