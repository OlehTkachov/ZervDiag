from types import MethodType

from PySide6.QtCore import QTimer

from search.web_search import open_web_search
from ui.search_not_found import (
    ACTION_EDIT,
    ACTION_WEB,
    show_search_not_found,
)


def install_ui_enhancements(main_window):
    """
    V14 UI-слой поверх стабильного MainWindow V13.

    Намеренно не переписывает большой ui/main_window.py:
    - Enter всегда обрабатывается предсказуемо;
    - повторный запрос во время поиска ставится следующим;
    - нулевой результат показывает заметный диалог;
    - из диалога можно открыть интернет-поиск;
    - кнопка OCR получает понятное пользователю название.
    """
    if getattr(
        main_window,
        "_v14_ui_enhancements_installed",
        False,
    ):
        return

    main_window._v14_ui_enhancements_installed = True
    main_window._v14_active_query = ""
    main_window._v14_queued_query = None

    original_perform_search = main_window.perform_search
    original_search_finished = main_window.search_finished
    original_search_error = main_window.search_error
    original_update_ocr_button = main_window.update_ocr_button
    original_start_ocr = main_window.start_ocr

    def enhanced_update_ocr_button(self):
        count = original_update_ocr_button()

        worker_running = bool(
            self.ocr_worker
            and self.ocr_worker.isRunning()
        )

        if not worker_running:
            self.btn_ocr.setText(
                f"Распознать сканы ({count})"
            )

        return count

    def enhanced_start_ocr(self):
        original_start_ocr()

        if (
            self.ocr_worker
            and self.ocr_worker.isRunning()
        ):
            self.btn_ocr.setText(
                "Остановить распознавание"
            )

    def enhanced_perform_search(self, *args):
        query = (
            self.search_input
            .text()
            .strip()
        )

        if not query:
            original_perform_search()
            return

        if (
            self.search_worker
            and self.search_worker.isRunning()
        ):
            if query != self._v14_active_query:
                self._v14_queued_query = query
                self.status.setText(
                    "Текущий поиск ещё выполняется. "
                    f"Следующий запрос: {query}"
                )
            else:
                self.status.setText(
                    f"Поиск «{query}» выполняется..."
                )

            return

        self._v14_active_query = query
        self._v14_queued_query = None

        original_perform_search()

    def _start_queued_search(self, query):
        query = (query or "").strip()

        if not query:
            return

        self.search_input.setText(query)
        self._v14_active_query = ""

        QTimer.singleShot(
            0,
            self.perform_search,
        )

    def enhanced_search_finished(self, results):
        query = (
            self._v14_active_query
            or self.search_input.text().strip()
        )

        queued_query = self._v14_queued_query
        self._v14_queued_query = None

        original_search_finished(results)

        if (
            queued_query
            and queued_query != query
        ):
            _start_queued_search(
                self,
                queued_query,
            )
            return

        self._v14_active_query = ""

        if results:
            return

        self.status.setText(
            f'По запросу «{query}» документы не найдены.'
        )

        action = show_search_not_found(
            self,
            query,
        )

        if action == ACTION_WEB:
            opened = open_web_search(query)

            if opened:
                self.status.setText(
                    f'Локально ничего не найдено. '
                    f'Открыт интернет-поиск: «{query}».'
                )
            else:
                self.status.setText(
                    "Не удалось открыть системный браузер."
                )

        elif action == ACTION_EDIT:
            self.search_input.setFocus()
            self.search_input.selectAll()

    def enhanced_search_error(self, message):
        queued_query = self._v14_queued_query
        self._v14_queued_query = None
        self._v14_active_query = ""

        original_search_error(message)

        if queued_query:
            _start_queued_search(
                self,
                queued_query,
            )

    main_window.update_ocr_button = MethodType(
        enhanced_update_ocr_button,
        main_window,
    )

    main_window.start_ocr = MethodType(
        enhanced_start_ocr,
        main_window,
    )

    main_window.perform_search = MethodType(
        enhanced_perform_search,
        main_window,
    )

    main_window.search_finished = MethodType(
        enhanced_search_finished,
        main_window,
    )

    main_window.search_error = MethodType(
        enhanced_search_error,
        main_window,
    )

    # Старые connections созданы внутри MainWindow.create_ui().
    # Переподключаем только три поисковых сигнала к V14-обработчику.
    for signal in (
        main_window.search_input.returnPressed,
        main_window.search_button.clicked,
        main_window.btn_search.clicked,
    ):
        try:
            signal.disconnect()
        except RuntimeError:
            pass

        signal.connect(
            main_window.perform_search
        )

    main_window.search_button.setDefault(True)
    main_window.search_button.setAutoDefault(True)

    main_window.btn_ocr.setToolTip(
        "Извлечь текст из отсканированных PDF "
        "и изображений, чтобы они участвовали в поиске."
    )

    main_window.update_ocr_button()
