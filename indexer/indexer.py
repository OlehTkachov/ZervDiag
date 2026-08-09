from database.db import get_connection
from indexer.scanner import scan_folder
from readers.document_reader import read_document


def index_folder(folder):
    files = scan_folder(folder)

    conn = get_connection()
    cursor = conn.cursor()

    added = 0
    updated = 0

    for file in files:
        try:
            content = read_document(file["filepath"])

            cursor.execute(
                "SELECT id FROM files WHERE filepath = ?",
                (file["filepath"],)
            )

            existing = cursor.fetchone()

            if existing:
                cursor.execute("""
                    UPDATE files
                    SET filename = ?,
                        extension = ?,
                        size = ?,
                        modified = ?,
                        content = ?
                    WHERE filepath = ?
                """, (
                    file["filename"],
                    file["extension"],
                    file["size"],
                    file["modified"],
                    content,
                    file["filepath"],
                ))
                updated += 1
            else:
                cursor.execute("""
                    INSERT INTO files
                    (
                        filename,
                        filepath,
                        extension,
                        size,
                        modified,
                        content
                    )
                    VALUES (?, ?, ?, ?, ?, ?)
                """, (
                    file["filename"],
                    file["filepath"],
                    file["extension"],
                    file["size"],
                    file["modified"],
                    content,
                ))
                added += 1

        except Exception as error:
            print(
                f"Ошибка обработки {file['filepath']}: {error}"
            )

    conn.commit()
    conn.close()

    return added, updated, len(files)