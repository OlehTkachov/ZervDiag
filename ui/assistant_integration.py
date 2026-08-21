from PySide6.QtWidgets import QPushButton

from ui.assistant_dialog import AssistantDialog


def install_ai_assistant(main_window):
    if getattr(main_window, "_v15_ai_assistant_installed", False):
        return

    main_window._v15_ai_assistant_installed = True

    central = main_window.centralWidget()
    if central is None or central.layout() is None:
        raise RuntimeError("MainWindow layout not found")

    main_layout = central.layout()
    left_item = main_layout.itemAt(0)
    left_layout = left_item.layout() if left_item else None

    if left_layout is None:
        raise RuntimeError("Left panel layout not found")

    button = QPushButton("ZervDiag AI")
    button.setToolTip(
        "Grounded AI: first retrieve local SQLite sources, then ask a model."
    )
    main_window.btn_ai_assistant = button

    search_index = left_layout.indexOf(main_window.btn_search)
    insert_index = search_index + 1 if search_index >= 0 else 2
    left_layout.insertWidget(insert_index, button)

    def open_assistant():
        dialog = AssistantDialog(main_window)
        dialog.exec()

    button.clicked.connect(open_assistant)
