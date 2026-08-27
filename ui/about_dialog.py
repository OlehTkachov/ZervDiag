from PySide6.QtCore import Qt
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import (
    QDialog,
    QDialogButtonBox,
    QHBoxLayout,
    QLabel,
    QVBoxLayout,
)

from app_paths import DB_PATH, resource_path
from app_version import (
    APP_VERSION,
    COPYRIGHT,
    PRODUCT_EDITION,
    PRODUCT_NAME,
    PRODUCT_TAGLINE,
)
from i18n.settings import get_language


ABOUT_TEXT = {
    "ru": {
        "title": "О программе ZervDiag",
        "edition": "Техническая документация · поиск · OCR · локальный ИИ",
        "description": (
            "ZervDiag — рабочая диагностическая среда для сервисной документации. "
            "Программа индексирует техническую библиотеку, находит сведения по "
            "моделям, кодам ошибок и узлам, распознаёт сканы и подготавливает "
            "локальный контекст для ИИ-помощника."
        ),
        "privacy": (
            "Поиск выполняется по локальной SQLite-базе. Исходные документы "
            "не отправляются модели автоматически: ИИ получает только "
            "подготовленный ZervDiag контекст согласно выбранному провайдеру."
        ),
        "database": "Рабочая база данных",
        "version": "Версия",
        "channel": "Канал",
        "close": "Закрыть",
    },
    "uk": {
        "title": "Про програму ZervDiag",
        "edition": "Технічна документація · пошук · OCR · локальний ШІ",
        "description": (
            "ZervDiag — робоче діагностичне середовище для сервісної документації. "
            "Програма індексує технічну бібліотеку, знаходить відомості за "
            "моделями, кодами помилок і вузлами, розпізнає скани та готує "
            "локальний контекст для ШІ-помічника."
        ),
        "privacy": (
            "Пошук виконується за локальною SQLite-базою. Вихідні документи "
            "не надсилаються моделі автоматично: ШІ отримує лише підготовлений "
            "ZervDiag контекст відповідно до обраного провайдера."
        ),
        "database": "Робоча база даних",
        "version": "Версія",
        "channel": "Канал",
        "close": "Закрити",
    },
    "en": {
        "title": "About ZervDiag",
        "edition": "Technical documentation · search · OCR · local AI",
        "description": (
            "ZervDiag is a diagnostic workspace for service documentation. "
            "It indexes technical libraries, retrieves information by model, "
            "fault code and component, recognizes scanned material and prepares "
            "local context for the AI assistant."
        ),
        "privacy": (
            "Search runs against the local SQLite database. Source documents are "
            "not sent to a model automatically: AI receives only the context "
            "prepared by ZervDiag according to the selected provider."
        ),
        "database": "Working database",
        "version": "Version",
        "channel": "Channel",
        "close": "Close",
    },
}


def _text(settings):
    language = get_language(settings)
    return ABOUT_TEXT.get(language, ABOUT_TEXT["ru"])


class AboutDialog(QDialog):
    def __init__(self, settings, parent=None):
        super().__init__(parent)
        text = _text(settings)

        self.setWindowTitle(text["title"])
        self.setModal(True)
        self.resize(620, 470)
        self.setObjectName("aboutDialog")

        root = QVBoxLayout(self)
        root.setContentsMargins(28, 28, 28, 24)
        root.setSpacing(14)

        hero = QHBoxLayout()
        hero.setSpacing(18)

        icon_label = QLabel()
        icon = QIcon(str(resource_path("assets", "zervdiag.ico")))
        if not icon.isNull():
            icon_label.setPixmap(icon.pixmap(84, 84))
        icon_label.setFixedSize(84, 84)
        icon_label.setAlignment(Qt.AlignCenter)
        hero.addWidget(icon_label)

        hero_text = QVBoxLayout()
        product = QLabel(PRODUCT_NAME)
        product.setObjectName("aboutProduct")
        tagline = QLabel(PRODUCT_TAGLINE)
        tagline.setObjectName("aboutTagline")
        edition = QLabel(text["edition"])
        edition.setObjectName("aboutEdition")
        hero_text.addWidget(product)
        hero_text.addWidget(tagline)
        hero_text.addWidget(edition)
        hero_text.addStretch(1)
        hero.addLayout(hero_text, 1)
        root.addLayout(hero)

        description = QLabel(text["description"])
        description.setWordWrap(True)
        description.setObjectName("aboutBody")
        root.addWidget(description)

        privacy = QLabel(text["privacy"])
        privacy.setWordWrap(True)
        privacy.setObjectName("aboutNote")
        root.addWidget(privacy)

        details = QLabel(
            f"<b>{text['version']}:</b> {APP_VERSION}<br>"
            f"<b>{text['channel']}:</b> {PRODUCT_EDITION}<br>"
            f"<b>{text['database']}:</b><br>{DB_PATH}"
        )
        details.setWordWrap(True)
        details.setTextInteractionFlags(Qt.TextSelectableByMouse)
        details.setObjectName("aboutDetails")
        root.addWidget(details)

        root.addStretch(1)

        footer = QLabel(COPYRIGHT)
        footer.setObjectName("aboutCopyright")
        root.addWidget(footer)

        buttons = QDialogButtonBox(QDialogButtonBox.Close)
        buttons.button(QDialogButtonBox.Close).setText(text["close"])
        buttons.rejected.connect(self.reject)
        root.addWidget(buttons)
