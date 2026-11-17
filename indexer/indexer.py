from pathlib import Path

from database.db import (
    create_database,
    get_connection,
)
from indexer.scanner import (
    scan_folder,
    is_extractable_extension,
)
from indexer.single_file import load_and_index_file


DOC_REPLACEMENT_MIN = 50
DOC_REPLACEMENT_RATIO = 0.05


def _doc_content_is_damaged(
    content,
    extension,
):
    """
    Старые .DOC раньше могли быть сохранены в SQLite с массовыми U+FFFD
    из-за неверного декодирования LibreOffice TXT как UTF-8.

    Считаем повреждёнными только явно испорченные .DOC, чтобы не
    переизвлекать документы из-за единичных допустимых replacement chars.
    """
    if (
        (extension or "").lower()
        != ".doc"
    ):
        return False

    text = content or ""

    if not text:
        return False

    replacement_count = text.count(
        "\ufffd"
    )

    if replacement_count < DOC_REPLACEMENT_MIN:
        return False

    return (
        replacement_count
        / max(1, len(text))
        >= DOC_REPLACEMENT_RATIO
    )


def _needs_extraction(
    content,
    status,
    extractable,
    extension="",
):
    if not extractable:
        return False

    if status in {
        "ocr_pending",
        "ocr_processing",
    }:
        return False

    if (
        status == "ok"
        and content
        and len(
            content.strip()
        ) >= 10
    ):
        if _doc_content_is_damaged(
            content,
            extension,
        ):
            return True

        return False

    if status == "error":
        return False

    return True


def _set_unsupported(
    conn,
    file_id,
):
    conn.execute(
        """
        UPDATE files
        SET
            extraction_status = 'unsupported',
            extraction_error = NULL
        WHERE id = ?
          AND (
                content IS NULL
                OR length(trim(content)) < 10
              )
        """,
        (file_id,),
    )


def index_folder(
    folder,
    progress_callback=None,
    stop_callback=None,
):
    """
    Быстрый проход всей базы.

    Важно для error:
    - изменение только modified НЕ запускает повтор;
    - если изменился size, error сбрасывается в pending
      и файл обрабатывается снова.

    Это защищает от повторного OCR после OneDrive
    hydrate/release, которые могут менять метаданные.
    """

    create_database()

    files = scan_folder(
        folder
    )

    added = 0
    updated = 0
    skipped = 0
    deleted = 0

    total = len(
        files
    )

    current_paths = set()

    for number, file in enumerate(
        files,
        start=1,
    ):
        if (
            stop_callback
            and stop_callback()
        ):
            print(
                "INDEXING_STOP_REQUESTED",
                flush=True,
            )
            break

        filepath = file[
            "filepath"
        ]

        current_paths.add(
            filepath
        )

        scanned_cloud = bool(
            file.get(
                "is_cloud",
                False,
            )
        )

        extractable = bool(
            file.get(
                "extractable",
                is_extractable_extension(
                    file.get(
                        "extension",
                        "",
                    )
                ),
            )
        )

        conn = get_connection()

        try:
            existing = conn.execute(
                """
                SELECT
                    id,
                    size,
                    modified,
                    content,
                    is_cloud,
                    extraction_status,
                    ocr_page,
                    ocr_total_pages
                FROM files
                WHERE filepath = ?
                """,
                (filepath,),
            ).fetchone()

            if existing:
                (
                    file_id,
                    old_size,
                    old_modified,
                    old_content,
                    old_cloud,
                    old_status,
                    old_ocr_page,
                    old_ocr_total,
                ) = existing

                cloud = (
                    bool(old_cloud)
                    or scanned_cloud
                )

                size_changed = (
                    old_size
                    != file["size"]
                )

                modified_changed = (
                    old_modified
                    != file["modified"]
                )

                metadata_changed = (
                    size_changed
                    or modified_changed
                )

                # -------------------------------------------------
                # V11 ERROR LOCK
                #
                # OneDrive hydrate/release может менять modified.
                # Поэтому уже известный error не повторяем только
                # из-за другой даты/времени.
                #
                # Если изменился размер — считаем, что файл реально
                # изменился, сбрасываем ошибку и пробуем снова.
                # -------------------------------------------------
                if (
                    old_status == "error"
                    and not size_changed
                ):
                    if (
                        metadata_changed
                        or bool(old_cloud) != cloud
                    ):
                        conn.execute(
                            """
                            UPDATE files
                            SET
                                filename = ?,
                                extension = ?,
                                size = ?,
                                modified = ?,
                                is_cloud = ?
                            WHERE id = ?
                            """,
                            (
                                file["filename"],
                                file["extension"],
                                file["size"],
                                file["modified"],
                                int(cloud),
                                file_id,
                            ),
                        )

                        conn.commit()

                    content = old_content
                    status = "error"
                    should_extract = False

                elif metadata_changed:
                    new_status = (
                        "pending"
                        if extractable
                        else "unsupported"
                    )

                    conn.execute(
                        """
                        UPDATE files
                        SET
                            filename = ?,
                            extension = ?,
                            size = ?,
                            modified = ?,
                            content = NULL,
                            is_cloud = ?,
                            extraction_status = ?,
                            extraction_error = NULL,
                            ocr_page = 0,
                            ocr_total_pages = 0,
                            ocr_updated = NULL
                        WHERE id = ?
                        """,
                        (
                            file["filename"],
                            file["extension"],
                            file["size"],
                            file["modified"],
                            int(cloud),
                            new_status,
                            file_id,
                        ),
                    )

                    conn.commit()

                    content = None
                    status = new_status
                    updated += 1

                    should_extract = (
                        _needs_extraction(
                            content,
                            status,
                            extractable,
                            file.get(
                                "extension",
                                "",
                            ),
                        )
                    )

                else:
                    if (
                        bool(old_cloud)
                        != cloud
                    ):
                        conn.execute(
                            """
                            UPDATE files
                            SET is_cloud = ?
                            WHERE id = ?
                            """,
                            (
                                int(cloud),
                                file_id,
                            ),
                        )

                    if not extractable:
                        _set_unsupported(
                            conn,
                            file_id,
                        )

                    conn.commit()

                    content = old_content
                    status = (
                        old_status
                        if extractable
                        else (
                            "ok"
                            if old_content
                            and len(
                                old_content.strip()
                            ) >= 10
                            else "unsupported"
                        )
                    )

                    should_extract = (
                        _needs_extraction(
                            content,
                            status,
                            extractable,
                            file.get(
                                "extension",
                                "",
                            ),
                        )
                    )

            else:
                cloud = scanned_cloud

                initial_status = (
                    "pending"
                    if extractable
                    else "unsupported"
                )

                cursor = conn.execute(
                    """
                    INSERT INTO files
                    (
                        filename,
                        filepath,
                        extension,
                        size,
                        modified,
                        content,
                        is_cloud,
                        extraction_status,
                        extraction_error,
                        ocr_page,
                        ocr_total_pages
                    )
                    VALUES (
                        ?, ?, ?, ?, ?,
                        NULL, ?, ?, NULL, 0, 0
                    )
                    """,
                    (
                        file["filename"],
                        filepath,
                        file["extension"],
                        file["size"],
                        file["modified"],
                        int(cloud),
                        initial_status,
                    ),
                )

                file_id = (
                    cursor.lastrowid
                )

                conn.commit()

                added += 1
                should_extract = (
                    extractable
                )

        finally:
            conn.close()

        if progress_callback:
            progress_callback(
                number,
                total,
                file["filename"],
                cloud,
            )

        if not should_extract:
            skipped += 1
            continue

        print(
            f"ID: {file_id}",
            flush=True,
        )
        print(
            f"FILE: {filepath}",
            flush=True,
        )
        print(
            f"BEFORE CLOUD: "
            f"{int(cloud)}",
            flush=True,
        )

        success = load_and_index_file(
            file_id=file_id,
            filepath=filepath,
            is_cloud=cloud,
            stop_callback=stop_callback,
        )

        if (
            success
            and existing
        ):
            updated += 1

    try:
        root_folder = Path(
            folder
        ).resolve()

        conn = get_connection()

        try:
            database_paths = {
                row[0]
                for row in conn.execute(
                    "SELECT filepath FROM files"
                ).fetchall()
            }

            missing_paths = []

            for db_path in database_paths:
                try:
                    resolved = Path(
                        db_path
                    ).resolve()

                    if (
                        resolved.is_relative_to(
                            root_folder
                        )
                        and db_path
                        not in current_paths
                        and not Path(
                            db_path
                        ).exists()
                    ):
                        missing_paths.append(
                            db_path
                        )

                except (
                    OSError,
                    ValueError,
                ):
                    pass

            for filepath in missing_paths:
                conn.execute(
                    """
                    DELETE FROM files
                    WHERE filepath = ?
                    """,
                    (filepath,),
                )

                deleted += 1

            conn.commit()

        finally:
            conn.close()

    except Exception as error:
        print(
            f"DELETE_SYNC_WARNING: "
            f"{error}",
            flush=True,
        )

    return (
        added,
        updated,
        skipped,
        deleted,
        total,
    )
