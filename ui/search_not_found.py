from PySide6.QtWidgets import QMessageBox


ACTION_WEB = "web"
ACTION_EDIT = "edit"
ACTION_CLOSE = "close"


def show_search_not_found(parent, query):
    """
    Показывает заметное сообщение, когда локальная база ничего не нашла.

    Возвращает одно из:
      - ACTION_WEB  — пользователь хочет искать в интернете;
      - ACTION_EDIT — пользователь хочет изменить запрос;
      - ACTION_CLOSE.
    """
    query = (query or "").strip()

    dialog = QMessageBox(parent)
    dialog.setIcon(QMessageBox.Information)
    dialog.setWindowTitle("Документ не найден")
    dialog.setText(
        f'По запросу «{query}» документы в локальной базе не найдены.'
    )
    dialog.setInformativeText(
        "Можно изменить запрос или выполнить поиск в интернете."
    )

    web_button = dialog.addButton(
        "Искать в интернете",
        QMessageBox.ActionRole,
    )
    edit_button = dialog.addButton(
        "Изменить запрос",
        QMessageBox.AcceptRole,
    )
    close_button = dialog.addButton(
        "Закрыть",
        QMessageBox.RejectRole,
    )

    dialog.setDefaultButton(edit_button)
    dialog.exec()

    clicked = dialog.clickedButton()

    if clicked is web_button:
        return ACTION_WEB

    if clicked is edit_button:
        return ACTION_EDIT

    if clicked is close_button:
        return ACTION_CLOSE

    return ACTION_CLOSE
