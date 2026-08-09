from database.db import get_connection
from indexer.single_file import load_and_index_file


def hydrate_and_index(file_id, filepath):
    """
    Принудительно загружает cloud-файл,
    читает его и сохраняет текст в БД.
    """

    return load_and_index_file(
        file_id,
        filepath,
        is_cloud=True
    )


def hydrate_file_by_id(file_id):
    """
    Загружает cloud-файл по ID из базы.
    """

    conn = get_connection()

    try:
        row = conn.execute(
            """
            SELECT filepath, is_cloud
            FROM files
            WHERE id = ?
            """,
            (file_id,)
        ).fetchone()

    finally:
        conn.close()

    if not row:
        return False

    filepath, is_cloud = row

    if not is_cloud:
        return True

    return hydrate_and_index(
        file_id,
        filepath
    )
