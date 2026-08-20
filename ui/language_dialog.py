from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QLabel,
    QVBoxLayout,
)

from i18n.catalog import (
    SUPPORTED_LANGUAGES,
    normalize_language,
)


class LanguageDialog(QDialog):
    """
    Минимальный первый выбор языка.

    Пока V14 не переводит весь старый MainWindow, диалог не включён в main.py.
    Его нужно активировать одновременно с переводом всех UI-строк, чтобы
    пользователь не выбрал English/Українська и не получил наполовину русский UI.
    """

    def __init__(
        self,
        current_language="ru",
        parent=None,
    ):
        super().__init__(parent)

        self.setWindowTitle(
            "Language / Мова / Язык"
        )

        layout = QVBoxLayout(self)

        label = QLabel(
            "Выберите язык / Оберіть мову / Choose language"
        )
        label.setWordWrap(True)
        layout.addWidget(label)

        self.language_box = QComboBox()

        for code, name in SUPPORTED_LANGUAGES.items():
            self.language_box.addItem(
                name,
                code,
            )

        current_language = normalize_language(
            current_language
        )

        for index in range(
            self.language_box.count()
        ):
            if (
                self.language_box.itemData(index)
                == current_language
            ):
                self.language_box.setCurrentIndex(index)
                break

        layout.addWidget(
            self.language_box
        )

        buttons = QDialogButtonBox(
            QDialogButtonBox.Ok
            | QDialogButtonBox.Cancel
        )
        buttons.accepted.connect(
            self.accept
        )
        buttons.rejected.connect(
            self.reject
        )
        layout.addWidget(buttons)

    def selected_language(self):
        return normalize_language(
            self.language_box.currentData()
        )


def choose_language(
    current_language="ru",
    parent=None,
):
    dialog = LanguageDialog(
        current_language=current_language,
        parent=parent,
    )

    if dialog.exec() != QDialog.Accepted:
        return None

    return dialog.selected_language()
