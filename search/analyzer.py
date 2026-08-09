from pathlib import Path

from PySide6.QtCore import QThread, Signal

from database.db import get_connection
from readers.document_reader import read_document
from indexer.single_file import load_and_index_file


class DocumentAnalyzer(QThread):

    progress = Signal(int, int, str)
    finished = Signal(int, int)
    error = Signal(str)

    def __init__(self, files):
        super().__init__()

        self.files = files

    def analyze_file(self, file_id, filepath, is_cloud=False):

        filepath = Path(filepath)

        # Файл ещё не загружен локально.
        # Для cloud используем единый механизм.
        if is_cloud:

            success = load_and_index_file(
                file_id,
                filepath,
                is_cloud=True
            )

            if not success:
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

            if row and row[0]:
                return row[0]

            return ""

        # Локальный файл.
        if not filepath.exists():
            return ""

        return read_document(filepath)

    def run(self):

        total = len(self.files)
        analyzed = 0

        try:

            for current, file in enumerate(
                self.files,
                start=1
            ):

                filename = file.get(
                    "filename",
                    Path(
                        file.get(
                            "filepath",
                            ""
                        )
                    ).name
                )

                self.progress.emit(
                    current - 1,
                    total,
                    filename
                )

                file_id = file.get("id")
                filepath = file.get("filepath")

                if not filepath:
                    continue

                is_cloud = bool(
                    file.get(
                        "is_cloud",
                        False
                    )
                )

                content = self.analyze_file(
                    file_id,
                    filepath,
                    is_cloud
                )

                if content:
                    analyzed += 1

                self.progress.emit(
                    current,
                    total,
                    filename
                )

            self.finished.emit(
                analyzed,
                total
            )

        except Exception as error:

            self.error.emit(
                str(error)
            )
