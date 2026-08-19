import subprocess
import tempfile
import time
from pathlib import Path

import pymupdf


TESSERACT = (
    r"C:\Program Files"
    r"\Tesseract-OCR"
    r"\tesseract.exe"
)

OCR_DPI = 250
TESSERACT_PAGE_TIMEOUT = 180


def _check_stop(
    stop_callback,
):
    if (
        stop_callback
        and stop_callback()
    ):
        raise InterruptedError(
            "OCR stopped by user"
        )


def _terminate_process(
    process,
):
    try:
        process.terminate()
        process.communicate(
            timeout=2,
        )

    except Exception:
        try:
            process.kill()
            process.communicate()

        except Exception:
            pass


def _tesseract_image(
    image_path,
    stop_callback=None,
):
    _check_stop(
        stop_callback
    )

    process = subprocess.Popen(
        [
            TESSERACT,
            str(image_path),
            "stdout",
            "-l",
            "rus+eng",
            "--psm",
            "11",
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )

    started = time.monotonic()

    while process.poll() is None:
        if (
            stop_callback
            and stop_callback()
        ):
            _terminate_process(
                process
            )

            raise InterruptedError(
                "OCR stopped by user"
            )

        if (
            time.monotonic()
            - started
            > TESSERACT_PAGE_TIMEOUT
        ):
            _terminate_process(
                process
            )

            raise TimeoutError(
                "Tesseract page timeout"
            )

        time.sleep(
            0.2
        )

    stdout, stderr = (
        process.communicate()
    )

    if process.returncode != 0:
        return ""

    return stdout or ""


def ocr_image(
    filepath,
    stop_callback=None,
):
    try:
        return _tesseract_image(
            filepath,
            stop_callback=stop_callback,
        )

    except InterruptedError:
        raise

    except Exception as error:
        print(
            f"OCR_IMAGE_ERROR: "
            f"{filepath}: {error}",
            flush=True,
        )
        return ""


def ocr_pdf_pages(
    filepath,
    start_page=0,
    page_callback=None,
    progress_callback=None,
    stop_callback=None,
):
    """
    OCR PDF начиная с start_page (0-based).

    После каждой страницы page_callback получает:
        page_number (1-based), total_pages, text

    Поэтому вызывающий код может сохранять прогресс
    в SQLite после КАЖДОЙ страницы.
    """

    doc = pymupdf.open(
        filepath
    )

    try:
        total_pages = len(
            doc
        )

        start_page = max(
            0,
            min(
                int(start_page),
                total_pages,
            ),
        )

        for page_index in range(
            start_page,
            total_pages,
        ):
            _check_stop(
                stop_callback
            )

            page_number = (
                page_index + 1
            )

            if progress_callback:
                progress_callback(
                    page_number,
                    total_pages,
                )

            page = doc[
                page_index
            ]

            matrix = pymupdf.Matrix(
                OCR_DPI / 72,
                OCR_DPI / 72,
            )

            pix = page.get_pixmap(
                matrix=matrix,
                alpha=False,
            )

            with tempfile.TemporaryDirectory() as temp_dir:
                image_path = (
                    Path(temp_dir)
                    / f"page_{page_number}.png"
                )

                pix.save(
                    str(image_path)
                )

                page_text = _tesseract_image(
                    image_path,
                    stop_callback=stop_callback,
                )

            if page_callback:
                page_callback(
                    page_number,
                    total_pages,
                    page_text,
                )

        return total_pages

    finally:
        doc.close()
