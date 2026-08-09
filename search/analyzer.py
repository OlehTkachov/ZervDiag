from pathlib import Path

from database.db import get_connection
from readers.document_reader import read_document
from indexer.single_file import load_and_index_file


class DocumentAnalyzer:

    def __init__(self):
        pass

    def analyze_file(self, file_id, filepath, is_cloud=False):

        filepath = Path(filepath)

        if not filepath.exists():
            return ""

        # Для cloud-файла используем единый механизм:
        # hydrate -> прочитать -> сохранить в БД.
        if is_cloud:
            if not load_and_index_file(
                file_id,
                filepath,
                is_cloud=True
            ):
                return ""

            conn = get_connection()

            try:
                row = conn.execute(
                    """
                    SELECT content
                    FROM files
                    WHERE id = ?
                    """,
                    (file_id,)
                ).fetchone()

            finally:
                conn.close()

            return row[0] if row and row[0] else ""

        # Локальный файл можно читать непосредственно.
        return read_document(filepath)

    def analyze_files(self, files):

        results = []

        for file in files:

            file_id = file.get("id")
            filepath = file.get("filepath")
            is_cloud = bool(file.get("is_cloud", False))

            content = self.analyze_file(
                file_id,
                filepath,
                is_cloud
            )

            if content:
                results.append({
                    "id": file_id,
                    "filepath": filepath,
                    "content": content
                })

        return results
