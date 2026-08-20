from pathlib import Path

from database.db import get_connection
from readers.document_reader import (
    OCRRequired,
    read_document,
)
from onedrive.hydrate import (
    hydrate_file,
    dehydrate_file,
)


MIN_CONTENT_CHARS = 10

OCR_IMAGE_EXTENSIONS = {
    ".jpg",
    ".jpeg",
    ".png",
    ".tif",
    ".tiff",
}


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


def _queue_ocr(
    file_id,
    required,
):
    preview = (
        required.preview
        if len(
            required.preview.strip()
        ) >= MIN_CONTENT_CHARS
        else None
    )

    conn = get_connection()

    try:
        conn.execute(
            """
            UPDATE files
            SET
                content = ?,
                extraction_status = 'ocr_pending',
                extraction_error = NULL,
                ocr_page = 0,
                ocr_total_pages = ?,
                ocr_updated = NULL
            WHERE id = ?
            """,
            (
                preview,
                required.total_pages,
                file_id,
            ),
        )

        conn.commit()

    finally:
        conn.close()


def load_and_index_file(
    file_id,
    filepath,
    is_cloud=False,
    stop_callback=None,
):
    """
    Быстрая обработка одного файла.

    PDF без текстового слоя ставится в OCR-очередь.
    JPG/PNG/TIF/TIFF ставятся в OCR-очередь СРАЗУ,
    без hydrate и без запуска Tesseract в быстром проходе.
    """

    filepath = str(
        filepath
    )

    extension = Path(
        filepath
    ).suffix.lower()

    hydrated = False

    try:
        _set_status(
            file_id,
            "processing",
        )

        # Изображения не нужно скачивать только ради того,
        # чтобы понять, что им нужен OCR. Сразу ставим в очередь.
        if extension in OCR_IMAGE_EXTENSIONS:
            required = OCRRequired(
                total_pages=0,
                preview="",
            )

            _queue_ocr(
                file_id,
                required,
            )

            print(
                f"OCR_QUEUED_IMAGE: {filepath}",
                flush=True,
            )

            return True

        if is_cloud:
            print(
                f"Hydrating: {filepath}",
                flush=True,
            )

            try:
                hydrate_file(
                    filepath
                )
                hydrated = True

            except Exception as error:
                _set_status(
                    file_id,
                    "error",
                    f"Hydrate error: {error}",
                )

                print(
                    f"HYDRATE_ERROR: "
                    f"{filepath}: {error}",
                    flush=True,
                )

                return False

        if (
            stop_callback
            and stop_callback()
        ):
            _set_status(
                file_id,
                "pending",
            )

            print(
                f"STOPPED_BEFORE_EXTRACTION: "
                f"{filepath}",
                flush=True,
            )

            return False

        print(
            f"Extracting: {filepath}",
            flush=True,
        )

        try:
            content = read_document(
                filepath,
                stop_callback=stop_callback,
            )

        except OCRRequired as required:
            _queue_ocr(
                file_id,
                required,
            )

            pages = (
                str(required.total_pages)
                if required.total_pages
                else "?"
            )

            print(
                f"OCR_QUEUED: "
                f"{filepath} "
                f"({pages} pages)",
                flush=True,
            )

            return True

        except InterruptedError:
            _set_status(
                file_id,
                "pending",
            )

            print(
                f"STOPPED_DURING_EXTRACTION: "
                f"{filepath}",
                flush=True,
            )

            return False

        except Exception as error:
            _set_status(
                file_id,
                "error",
                f"Extraction error: {error}",
            )

            print(
                f"EXTRACTION_ERROR: "
                f"{filepath}: {error}",
                flush=True,
            )

            return False

        content = (
            content or ""
        )

        if (
            len(
                content.strip()
            )
            < MIN_CONTENT_CHARS
        ):
            _set_status(
                file_id,
                "error",
                (
                    "Извлечено слишком мало текста: "
                    f"{len(content.strip())} символов"
                ),
            )

            print(
                f"NO_USABLE_TEXT: "
                f"{filepath} "
                f"({len(content.strip())} chars)",
                flush=True,
            )

            return False

        conn = get_connection()

        try:
            conn.execute(
                """
                UPDATE files
                SET
                    content = ?,
                    extraction_status = 'ok',
                    extraction_error = NULL,
                    ocr_page = 0,
                    ocr_total_pages = 0,
                    ocr_updated = NULL
                WHERE id = ?
                """,
                (
                    content,
                    file_id,
                ),
            )

            conn.commit()

        finally:
            conn.close()

        print(
            f"Indexed: "
            f"{filepath} "
            f"({len(content)} chars)",
            flush=True,
        )

        return True

    finally:
        if hydrated:
            print(
                f"Releasing to OneDrive: "
                f"{filepath}",
                flush=True,
            )

            try:
                if dehydrate_file(
                    filepath
                ):
                    print(
                        f"Released: "
                        f"{filepath}",
                        flush=True,
                    )
                else:
                    print(
                        "WARNING: indexed but "
                        "release failed: "
                        f"{filepath}",
                        flush=True,
                    )

            except Exception as error:
                print(
                    f"WARNING: release error: "
                    f"{filepath}: {error}",
                    flush=True,
                )


def index_single_file(
    file_id,
    filepath,
):
    conn = get_connection()

    try:
        row = conn.execute(
            """
            SELECT is_cloud
            FROM files
            WHERE id = ?
            """,
            (file_id,),
        ).fetchone()

    finally:
        conn.close()

    if not row:
        return False

    is_cloud = bool(
        row[0]
    )

    print(
        f"BEFORE CLOUD: "
        f"{int(is_cloud)}",
        flush=True,
    )

    return load_and_index_file(
        file_id=file_id,
        filepath=filepath,
        is_cloud=is_cloud,
    )
