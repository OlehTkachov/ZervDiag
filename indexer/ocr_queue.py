import time
from pathlib import Path

from database.db import (
    create_database,
    get_connection,
)
from onedrive.hydrate import (
    hydrate_file,
    dehydrate_file,
)
from readers.ocr_reader import (
    ocr_document_pages,
)


MIN_CONTENT_CHARS = 10

OCR_EXTENSIONS = (
    ".pdf",
    ".jpg",
    ".jpeg",
    ".png",
    ".tif",
    ".tiff",
)


def get_pending_ocr_files():
    create_database()

    conn = get_connection()

    try:
        return conn.execute(
            """
            SELECT
                id,
                filename,
                filepath,
                is_cloud,
                ocr_page,
                ocr_total_pages,
                extension,
                COALESCE(size, 0)
            FROM files
            WHERE extension IN (
                    '.pdf',
                    '.jpg',
                    '.jpeg',
                    '.png',
                    '.tif',
                    '.tiff'
                  )
              AND extraction_status IN (
                    'ocr_pending',
                    'ocr_processing'
                  )
            ORDER BY
                CASE
                    WHEN extension = '.pdf'
                         AND ocr_total_pages > 0
                    THEN ocr_total_pages * 500000
                    ELSE COALESCE(size, 0)
                END,
                id
            """
        ).fetchall()

    finally:
        conn.close()


def _set_status(
    file_id,
    status,
    error=None,
):
    conn = get_connection()

    try:
        conn.execute(
            """
            UPDATE files
            SET
                extraction_status = ?,
                extraction_error = ?
            WHERE id = ?
            """,
            (
                status,
                (
                    str(error)[:2000]
                    if error
                    else None
                ),
                file_id,
            ),
        )

        conn.commit()

    finally:
        conn.close()


def _refresh_file_metadata(
    file_id,
    filepath,
):
    """
    OCR может hydrate/dehydrate OneDrive placeholder.
    После release OneDrive/Cloud Files может изменить metadata файла,
    поэтому сохраняем фактические size/modified уже после release.

    Иначе следующая обычная индексация может принять собственный
    hydrate/dehydrate за изменение документа и снова поставить его в OCR.
    """

    try:
        stat = Path(
            filepath
        ).stat()

    except OSError as error:
        print(
            f"OCR METADATA REFRESH WARNING: "
            f"{filepath}: {error}",
            flush=True,
        )
        return

    conn = get_connection()

    try:
        conn.execute(
            """
            UPDATE files
            SET
                size = ?,
                modified = ?
            WHERE id = ?
            """,
            (
                int(stat.st_size),
                float(stat.st_mtime),
                file_id,
            ),
        )

        conn.commit()

    finally:
        conn.close()


def _remember_total_pages(
    file_id,
    total_pages,
):
    conn = get_connection()

    try:
        conn.execute(
            """
            UPDATE files
            SET ocr_total_pages = ?
            WHERE id = ?
            """,
            (
                int(total_pages or 0),
                file_id,
            ),
        )

        conn.commit()

    finally:
        conn.close()


def _save_page(
    file_id,
    page_number,
    total_pages,
    text,
):
    """
    Сохраняет OCR сразу после каждой страницы.

    ocr_page = последняя полностью сохранённая страница.
    """

    page_text = (
        text or ""
    )

    separator = (
        "\n"
        if page_text
        else ""
    )

    conn = get_connection()

    try:
        conn.execute(
            """
            UPDATE files
            SET
                content =
                    COALESCE(content, '')
                    || ?,
                ocr_page = ?,
                ocr_total_pages = ?,
                ocr_updated = ?,
                extraction_status = 'ocr_processing',
                extraction_error = NULL
            WHERE id = ?
            """,
            (
                separator
                + page_text,
                page_number,
                total_pages,
                time.time(),
                file_id,
            ),
        )

        conn.commit()

    finally:
        conn.close()


def _finish_file(
    file_id,
):
    conn = get_connection()

    try:
        row = conn.execute(
            """
            SELECT
                length(
                    trim(
                        COALESCE(
                            content,
                            ''
                        )
                    )
                )
            FROM files
            WHERE id = ?
            """,
            (file_id,),
        ).fetchone()

        chars = (
            int(
                row[0] or 0
            )
            if row
            else 0
        )

        if (
            chars
            >= MIN_CONTENT_CHARS
        ):
            status = "ok"
            error = None

        else:
            status = "error"
            error = (
                "OCR не извлёк "
                "достаточно текста"
            )

        conn.execute(
            """
            UPDATE files
            SET
                extraction_status = ?,
                extraction_error = ?,
                ocr_updated = ?
            WHERE id = ?
            """,
            (
                status,
                error,
                time.time(),
                file_id,
            ),
        )

        conn.commit()

        return (
            status == "ok"
        )

    finally:
        conn.close()


def process_ocr_file(
    row,
    file_number,
    file_total,
    progress_callback=None,
    stop_callback=None,
):
    (
        file_id,
        filename,
        filepath,
        is_cloud,
        ocr_page,
        ocr_total_pages,
        extension,
        file_size,
    ) = row

    is_cloud = bool(
        is_cloud
    )

    start_page = int(
        ocr_page or 0
    )

    hydrated = False
    total_known = False

    try:
        if (
            stop_callback
            and stop_callback()
        ):
            return "stopped"

        _set_status(
            file_id,
            "ocr_processing",
        )

        if is_cloud:
            print(
                f"OCR Hydrating: "
                f"{filepath}",
                flush=True,
            )

            hydrate_file(
                filepath
            )

            hydrated = True

        print(
            f"OCR START: "
            f"{filename} "
            f"[{extension}] "
            f"from page "
            f"{start_page + 1}",
            flush=True,
        )

        def report_page(
            page_number,
            total_pages,
        ):
            nonlocal total_known

            if not total_known:
                _remember_total_pages(
                    file_id,
                    total_pages,
                )
                total_known = True

            print(
                f"OCR page "
                f"{page_number}/"
                f"{total_pages}: "
                f"{filepath}",
                flush=True,
            )

            if progress_callback:
                progress_callback(
                    file_number,
                    file_total,
                    filename,
                    page_number,
                    total_pages,
                )

        def save_page(
            page_number,
            total_pages,
            text,
        ):
            _save_page(
                file_id,
                page_number,
                total_pages,
                text,
            )

        try:
            ocr_document_pages(
                filepath,
                start_page=start_page,
                page_callback=save_page,
                progress_callback=report_page,
                stop_callback=stop_callback,
            )

        except InterruptedError:
            _set_status(
                file_id,
                "ocr_pending",
            )

            print(
                f"OCR STOPPED: "
                f"{filename}",
                flush=True,
            )

            return "stopped"

        success = _finish_file(
            file_id
        )

        print(
            f"OCR FINISHED: "
            f"{filename} "
            f"({'OK' if success else 'ERROR'})",
            flush=True,
        )

        return (
            "ok"
            if success
            else "error"
        )

    except Exception as error:
        _set_status(
            file_id,
            "error",
            error,
        )

        print(
            f"OCR ERROR: "
            f"{filepath}: {error}",
            flush=True,
        )

        return "error"

    finally:
        if hydrated:
            print(
                f"OCR Releasing to OneDrive: "
                f"{filepath}",
                flush=True,
            )

            try:
                dehydrate_file(
                    filepath
                )

                print(
                    f"OCR Released: "
                    f"{filepath}",
                    flush=True,
                )

            except Exception as error:
                print(
                    f"OCR RELEASE WARNING: "
                    f"{filepath}: {error}",
                    flush=True,
                )

            _refresh_file_metadata(
                file_id,
                filepath,
            )


def process_ocr_queue(
    progress_callback=None,
    stop_callback=None,
):
    rows = get_pending_ocr_files()

    total = len(
        rows
    )

    processed = 0
    errors = 0

    for file_number, row in enumerate(
        rows,
        start=1,
    ):
        if (
            stop_callback
            and stop_callback()
        ):
            return (
                processed,
                errors,
                total,
                True,
            )

        result = process_ocr_file(
            row,
            file_number,
            total,
            progress_callback=progress_callback,
            stop_callback=stop_callback,
        )

        if result == "stopped":
            return (
                processed,
                errors,
                total,
                True,
            )

        if result == "ok":
            processed += 1

        elif result == "error":
            errors += 1

    return (
        processed,
        errors,
        total,
        False,
    )
