import math
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

# Огромные TIF/схемы не отдаём Tesseract в исходном
# гигантском разрешении. 25 Мп сохраняют больше мелких
# подписей на гидросхемах, но ограничивают память и время.
MAX_RENDER_PIXELS = 25_000_000


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


def _render_matrix(
    page,
):
    """
    250 DPI для обычных страниц, но с ограничением
    итогового растра для очень больших схем/TIF.
    """

    scale = OCR_DPI / 72.0

    width = max(
        1.0,
        page.rect.width * scale,
    )

    height = max(
        1.0,
        page.rect.height * scale,
    )

    pixels = width * height

    limited = False

    if pixels > MAX_RENDER_PIXELS:
        factor = math.sqrt(
            MAX_RENDER_PIXELS / pixels
        )

        scale *= factor
        limited = True

    return (
        pymupdf.Matrix(
            scale,
            scale,
        ),
        limited,
    )


def ocr_document_pages(
    filepath,
    start_page=0,
    page_callback=None,
    progress_callback=None,
    stop_callback=None,
):
    """
    Постраничный OCR для PDF и изображений.

    PyMuPDF открывает PDF/JPG/PNG/TIF/TIFF как документ.
    Для многостраничного TIFF сохраняется тот же механизм
    resume, что и для PDF.

    После каждой полностью готовой страницы page_callback
    получает page_number, total_pages, text.
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

            matrix, limited = (
                _render_matrix(
                    page
                )
            )

            pix = page.get_pixmap(
                matrix=matrix,
                alpha=False,
            )

            if limited:
                print(
                    f"OCR_RENDER_LIMIT: "
                    f"{pix.width}x{pix.height} "
                    f"{filepath}",
                    flush=True,
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


def ocr_pdf_pages(
    filepath,
    start_page=0,
    page_callback=None,
    progress_callback=None,
    stop_callback=None,
):
    """Совместимый alias для старого вызова."""

    return ocr_document_pages(
        filepath,
        start_page=start_page,
        page_callback=page_callback,
        progress_callback=progress_callback,
        stop_callback=stop_callback,
    )


def ocr_image(
    filepath,
    stop_callback=None,
):
    """
    Совместимый вызов OCR одиночного изображения.
    Теперь тоже проходит через ограничение размера.
    """

    parts = []

    def save_page(
        page_number,
        total_pages,
        text,
    ):
        if text:
            parts.append(
                text
            )

    try:
        ocr_document_pages(
            filepath,
            start_page=0,
            page_callback=save_page,
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

    return "\n".join(
        parts
    )
