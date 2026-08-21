from PySide6.QtWidgets import QMessageBox

from i18n.catalog import tr


ACTION_WEB = "web"
ACTION_EDIT = "edit"
ACTION_CLOSE = "close"


def show_search_not_found(parent, query, language="ru"):
    """Show a localized notice when the local database has no matches."""
    query = (query or "").strip()

    dialog = QMessageBox(parent)
    dialog.setIcon(QMessageBox.Information)
    dialog.setWindowTitle(tr("search.not_found_title", language))
    dialog.setText(
        tr("search.not_found_text", language, query=query)
    )
    dialog.setInformativeText(
        tr("search.not_found_info", language)
    )

    web_button = dialog.addButton(
        tr("search.web", language),
        QMessageBox.ActionRole,
    )
    edit_button = dialog.addButton(
        tr("search.edit_query", language),
        QMessageBox.AcceptRole,
    )
    close_button = dialog.addButton(
        tr("common.close", language),
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
