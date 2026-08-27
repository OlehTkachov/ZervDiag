from types import MethodType

from PySide6.QtWidgets import QDialog, QLabel

from i18n.catalog import tr
from i18n.settings import get_language
from ui.auto_indexing import _fmt_dt
from ui.settings_dialog import SettingsDialog


def _folder_title_label(main_window):
    """Return the existing anonymous 'documentation folder' label safely."""
    central = main_window.centralWidget()
    if central is None or central.layout() is None:
        return None

    main_layout = central.layout()
    right_item = main_layout.itemAt(1)
    right_layout = right_item.layout() if right_item else None
    if right_layout is None:
        return None

    item = right_layout.itemAt(0)
    widget = item.widget() if item else None
    return widget if isinstance(widget, QLabel) else None


def apply_main_window_language(main_window):
    language = get_language(main_window.settings)
    main_window._v14_language = language

    main_window.setWindowTitle(tr("app.title", language))
    main_window.btn_search.setText(tr("nav.search", language))

    index_running = bool(
        main_window.index_worker and main_window.index_worker.isRunning()
    )
    main_window.btn_index.setText(
        tr("nav.stop_index", language)
        if index_running
        else tr("nav.index", language)
    )

    ocr_running = bool(
        main_window.ocr_worker and main_window.ocr_worker.isRunning()
    )
    if ocr_running:
        main_window.btn_ocr.setText(tr("nav.stop_recognition", language))
    else:
        main_window.update_ocr_button()

    main_window.btn_folder.setText(tr("nav.choose_folder", language))
    main_window.btn_duplicates.setText(tr("nav.duplicates", language))
    main_window.btn_settings.setText(tr("nav.settings", language))

    if hasattr(main_window, "btn_db_status"):
        main_window.btn_db_status.setText(tr("nav.database_status", language))

    folder_title = _folder_title_label(main_window)
    if folder_title is not None:
        folder_title.setText(tr("search.folder", language))

    folder = main_window.settings.value("documentation_folder", "")
    if not folder:
        main_window.folder_label.setText(tr("search.no_folder", language))

    main_window.search_input.setPlaceholderText(
        tr("search.placeholder", language)
    )
    main_window.search_button.setText(tr("search.find", language))
    main_window.results.setHorizontalHeaderLabels(
        [
            tr("search.file", language),
            tr("search.type", language),
            tr("search.status", language),
            tr("search.snippet", language),
            tr("search.path", language),
        ]
    )

    main_window.btn_ocr.setToolTip(tr("scan.tooltip", language))

    return language


def install_localization(main_window):
    if getattr(main_window, "_v14_localization_installed", False):
        return

    main_window._v14_localization_installed = True

    # Wrap the already-enhanced OCR button updater so it always uses the
    # selected interface language after queue counts change.
    original_update_ocr_button = main_window.update_ocr_button

    def localized_update_ocr_button(self):
        count = original_update_ocr_button()
        language = get_language(self.settings)

        worker_running = bool(self.ocr_worker and self.ocr_worker.isRunning())
        if worker_running:
            self.btn_ocr.setText(tr("nav.stop_recognition", language))
        else:
            self.btn_ocr.setText(
                tr("nav.scan_recognition", language, count=count)
            )

        self.btn_ocr.setToolTip(tr("scan.tooltip", language))
        return count

    main_window.update_ocr_button = MethodType(
        localized_update_ocr_button,
        main_window,
    )

    original_start_ocr = main_window.start_ocr

    def localized_start_ocr(self):
        original_start_ocr()
        if self.ocr_worker and self.ocr_worker.isRunning():
            self.btn_ocr.setText(
                tr("nav.stop_recognition", get_language(self.settings))
            )

    main_window.start_ocr = MethodType(localized_start_ocr, main_window)

    original_index_controls_ready = main_window._index_controls_ready

    def localized_index_controls_ready(self):
        original_index_controls_ready()
        self.btn_index.setText(tr("nav.index", get_language(self.settings)))

    main_window._index_controls_ready = MethodType(
        localized_index_controls_ready,
        main_window,
    )

    original_start_indexing = main_window.start_indexing

    def localized_start_indexing(self):
        original_start_indexing()
        if self.index_worker and self.index_worker.isRunning():
            self.btn_index.setText(
                tr("nav.stop_index", get_language(self.settings))
            )

    main_window.start_indexing = MethodType(localized_start_indexing, main_window)

    def apply_language():
        return apply_main_window_language(main_window)

    main_window._v14_apply_language = apply_language

    # Replace the earlier auto-index settings handler with the unified,
    # localized settings window. The controller itself stays unchanged.
    try:
        main_window.btn_settings.clicked.disconnect()
    except RuntimeError:
        pass

    def open_settings():
        dialog = SettingsDialog(main_window.settings, main_window)
        if dialog.exec() != QDialog.Accepted:
            return

        language = dialog.save()

        # A database import from Settings is staged rather than swapping a
        # live SQLite file. Save the other settings first, then close cleanly;
        # ui.first_run applies the verified database on the next launch.
        if dialog.restart_requested:
            main_window.close()
            return

        controller = getattr(
            main_window,
            "_v14_auto_index_controller",
            None,
        )
        if controller is not None:
            controller.reschedule_from_now()

        apply_main_window_language(main_window)

        if controller is not None:
            config = controller.config()
            if config["enabled"]:
                main_window.status.setText(
                    tr(
                        "settings.enabled_status",
                        language,
                        next_due=_fmt_dt(config["next_due"]),
                    )
                )
            else:
                main_window.status.setText(
                    tr("settings.disabled_status", language)
                )

    main_window.btn_settings.clicked.connect(open_settings)

    apply_main_window_language(main_window)
