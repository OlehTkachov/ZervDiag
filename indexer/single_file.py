from database.db import get_connection
from readers.document_reader import read_document
from onedrive.hydrate import hydrate_file


def index_single_file(file_id, filepath):

    if not hydrate_file(filepath):
        return False

    content = read_document(filepath)

    if not content:
        return False

    conn = get_connection()

    conn.execute(
        """
        UPDATE files
        SET content = ?, is_cloud = 0
        WHERE id = ?
        """,
        (content, file_id)
    )

    conn.commit()
    conn.close()

    return True
