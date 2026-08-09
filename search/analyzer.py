from PySide6.QtCore import QThread, Signal

from database.db import get_connection
from readers.document_reader import read_document


class DocumentAnalyzer(QThread):

    progress = Signal(
        int,
        int,
        str
    )

    finished = Signal(
        int,
        int
    )

    error = Signal(str)

    def __init__(
        self,
        files
    ):
        super().__init__()

        self.files = files

    def run(self):

        conn = None

        try:

            conn = get_connection()

            cursor = conn.cursor()

            total = len(
                self.files
            )

            analyzed = 0

            for number, file in enumerate(
                self.files,
                start=1
            ):

                filepath = file["filepath"]
                file_id = file["id"]
                filename = file["filename"]

                self.progress.emit(
                    number,
                    total,
                    filename
                )

                try:

                    content = read_document(
                        filepath
                    )

                    if content:

                        cursor.execute("""
                            UPDATE files
                            SET content = ?
                            WHERE id = ?
                        """, (
                            content,
                            file_id
                        ))

                        analyzed += 1

                except Exception as error:

                    print(
                        f"Ошибка анализа "
                        f"{filepath}: {error}"
                    )

            conn.commit()

            self.finished.emit(
                analyzed,
                total
            )

        except Exception as error:

            self.error.emit(
                str(error)
            )

        finally:

            if conn:

                conn.close()