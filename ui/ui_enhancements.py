from types import MethodType

from PySide6.QtCore import QTimer

from i18n.catalog import tr
from i18n.settings import get_language
from search.web_search import open_web_search
from ui.search_not_found import (
    ACTION_EDIT,
    ACTION_WEB,
    show_search_not_found,
)


def install_ui_enhancements(main_window):
    """
    V14 UI layer over the stable V13 MainWindow.

    It keeps the tested search implementation intact while improving
    Enter handling, queued searches, no-result feedback and OCR naming.
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
        language = get_language(self.settings)

        worker_running = bool(
            self.ocr_worker
            and self.ocr_worker.isRunning()
        )

        if not worker_running:
            self.btn_ocr.setText(
                tr("nav.scan_recognition", language, count=count)
            )

        return count

    def enhanced_start_ocr(self):
        original_start_ocr()

        if (
            self.ocr_worker
            and self.ocr_worker.isRunning()
        ):
            self.btn_ocr.setText(
                tr("nav.stop_recognition", get_language(self.settings))
            )

    def enhanced_perform_search(self, *args):
        query = (
            self.search_input
            .text()
            .strip()
        )
        language = get_language(self.settings)

        if not query:
            self.status.setText(tr("search.enter_query", language))
            return

        if (
            self.search_worker
            and self.search_worker.isRunning()
        ):
            if query != self._v14_active_query:
                self._v14_queued_query = query
                self.status.setText(
                    tr("search.next", language, query=query)
                )
            else:
                self.status.setText(
                    tr("search.running", language, query=query)
                )

            return

        self._v14_active_query = query
        self._v14_queued_query = None

        original_perform_search()
        self.status.setText(
            tr("search.running", language, query=query)
        )

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
        language = get_language(self.settings)

        if results:
            cloud_count = sum(1 for result in results if result.is_cloud)

            for row, result in enumerate(results):
                item = self.results.item(row, 2)
                if item is not None:
                    item.setText(
                        tr(
                            "storage.cloud" if result.is_cloud else "storage.local",
                            language,
                        )
                    )

            self.status.setText(
                tr(
                    "search.found",
                    language,
                    count=len(results),
                    cloud=cloud_count,
                )
            )
            return

        self.status.setText(
            tr("search.not_found_text", language, query=query)
        )

        action = show_search_not_found(
            self,
            query,
            language=language,
        )

        if action == ACTION_WEB:
            opened = open_web_search(query)

            if opened:
                self.status.setText(
                    tr("search.web_opened", language, query=query)
                )
            else:
                self.status.setText(
                    tr("search.web_failed", language)
                )

        elif action == ACTION_EDIT:
            self.search_input.setFocus()
            self.search_input.selectAll()

    def enhanced_search_error(self, message):
        queued_query = self._v14_queued_query
        self._v14_queued_query = None
        self._v14_active_query = ""

        original_search_error(message)
        self.status.setText(
            tr("search.error", get_language(self.settings), message=message)
        )

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

    # Old connections were created inside MainWindow.create_ui().
    # Reconnect only the three search signals to the V14 handler.
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
        tr("scan.tooltip", get_language(main_window.settings))
    )

    main_window.update_ocr_button()
