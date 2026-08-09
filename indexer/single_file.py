from database.db import get_connection
from readers.document_reader import read_document
from onedrive.hydrate import hydrate_file


def load_and_index_file(file_id, filepath, is_cloud=False):
    """
    Загружает документ, при необходимости hydrate cloud-файл,
    читает его и сохраняет текст в БД.

    Возвращает True при успешном чтении.
    """

    filepath = str(filepath)

    # Cloud-файл сначала принудительно делаем локальным.
    if is_cloud:
        if not hydrate_file(filepath):
            return False

    content = read_document(filepath)

    if not content:
        return False

    conn = get_connection()

    try:
        conn.execute(
            """
            UPDATE files
            SET
                content = ?,
                is_cloud = 0
            WHERE id = ?
            """,
            (content, file_id)
        )

        conn.commit()

    finally:
        conn.close()

    return True


def index_single_file(file_id, filepath):
    """
    Совместимость со старым кодом.
    Используется для cloud-файлов.
    """

    return load_and_index_file(
        file_id,
        filepath,
        is_cloud=True
    )
