from database.db import get_connection
from indexer.scanner import scan_folder
from readers.document_reader import read_document


def index_folder(folder, progress_callback=None):

    files = scan_folder(folder)

    conn = get_connection()
    cursor = conn.cursor()

    added = 0
    updated = 0
    skipped = 0
    deleted = 0

    total = len(files)

    current_paths = set()

    for number, file in enumerate(files, start=1):

        filepath = file["filepath"]

        current_paths.add(filepath)

        cloud = bool(
            file.get("is_cloud", False)
        )

        if progress_callback:
            progress_callback(
                number,
                total,
                file["filename"],
                cloud
            )

        try:

            cursor.execute(
                """
                SELECT
                    id,
                    size,
                    modified,
                    content,
                    is_cloud
                FROM files
                WHERE filepath = ?
                """,
                (filepath,)
            )

            existing = cursor.fetchone()

            # -------------------------------------------------
            # Файл уже есть в базе
            # -------------------------------------------------

            if existing:

                file_id = existing[0]
                old_size = existing[1]
                old_modified = existing[2]
                old_content = existing[3]
                old_cloud = bool(existing[4])

                # Ничего не изменилось.
                # Не открываем файл вообще.
                if (
                    old_size == file["size"]
                    and old_modified == file["modified"]
                    and old_cloud == cloud
                ):
                    skipped += 1
                    continue

                # -------------------------------------------------
                # Файл изменился
                # -------------------------------------------------

                content = old_content

                # Если файл локальный — можем прочитать его.
                #
                # Если файл облачный — НЕ читаем его.
                # Иначе OneDrive может начать скачивание.
                if not cloud:

                    content = read_document(filepath)

                else:

                    # Если это новый вариант облачного файла,
                    # старое содержимое может быть уже недействительным.
                    content = None

                cursor.execute(
                    """
                    UPDATE files
                    SET
                        filename = ?,
                        extension = ?,
                        size = ?,
                        modified = ?,
                        content = ?,
                        is_cloud = ?
                    WHERE filepath = ?
                    """,
                    (
                        file["filename"],
                        file["extension"],
                        file["size"],
                        file["modified"],
                        content,
                        int(cloud),
                        filepath,
                    )
                )

                updated += 1

            # -------------------------------------------------
            # Новый файл
            # -------------------------------------------------

            else:

                content = None

                # Локальный файл можно читать сразу.
                if not cloud:
                    content = read_document(filepath)

                # Облачный файл специально НЕ читаем.
                # Он будет загружен только при необходимости
                # через анализ найденных документов.

                cursor.execute(
                    """
                    INSERT INTO files
                    (
                        filename,
                        filepath,
                        extension,
                        size,
                        modified,
                        content,
                        is_cloud
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        file["filename"],
                        filepath,
                        file["extension"],
                        file["size"],
                        file["modified"],
                        content,
                        int(cloud),
                    )
                )

                added += 1

        except Exception as error:

            print(
                f"Ошибка обработки "
                f"{filepath}: {error}"
            )

    # -------------------------------------------------
    # Ищем файлы, которые исчезли из папки
    # -------------------------------------------------

    try:

        cursor.execute(
            "SELECT filepath FROM files"
        )

        database_paths = {
            row[0]
            for row in cursor.fetchall()
        }

        missing_paths = (
            database_paths - current_paths
        )

        for filepath in missing_paths:

            cursor.execute(
                """
                DELETE FROM files
                WHERE filepath = ?
                """,
                (filepath,)
            )

            deleted += 1

    except Exception as error:

        print(
            f"Ошибка проверки удалённых файлов: "
            f"{error}"
        )

    conn.commit()
    conn.close()

    return (
        added,
        updated,
        skipped,
        deleted,
        total
    )