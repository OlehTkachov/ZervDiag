from pathlib import Path

from database.db import get_connection
from onedrive.hydrate import hydrate_file
from readers.document_reader import read_document


def hydrate_and_read(file_id, filepath):
    path = Path(filepath)

    if not path.exists():
        return False

    try:
        hydrate_file(path)

        content = read_document(path)

        if not content:
            return False

        conn = get_connection()
        cursor = conn.cursor()

        cursor.execute(
            """
            UPDATE files
            SET content = ?, is_cloud = 0
            WHERE id = ?
            """,
            (content, file_id),
        )

        conn.commit()
        conn.close()

        return True

    except Exception as e:
        print("Ошибка загрузки документа:", e)
        return False
