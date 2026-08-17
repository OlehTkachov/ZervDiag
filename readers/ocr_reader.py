import subprocess
import tempfile
from pathlib import Path

import pymupdf


TESSERACT = r"C:\Program Files\Tesseract-OCR\tesseract.exe"


def ocr_pdf(filepath):
    text = []

    try:
        doc = pymupdf.open(filepath)

        for page_number, page in enumerate(doc):
            matrix = pymupdf.Matrix(300 / 72, 300 / 72).prerotate(180)

            pix = page.get_pixmap(
                matrix=matrix,
                alpha=False
            )

            with tempfile.TemporaryDirectory() as temp_dir:
                image_path = Path(temp_dir) / "page.png"

                pix.save(str(image_path))

                result = subprocess.run(
                    [
                        TESSERACT,
                        str(image_path),
                        "stdout",
                        "-l",
                        "rus+eng",
                        "--psm",
                        "11",
                    ],
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                )

                if result.returncode == 0 and result.stdout:
                    text.append(result.stdout)

        doc.close()

        return "\n".join(text)

    except Exception as error:
        print(f"OCR_ERROR: {filepath}: {error}")
        return ""

def ocr_image(filepath):
    try:
        result = subprocess.run(
            [
                TESSERACT,
                str(filepath),
                "stdout",
                "-l",
                "rus+eng",
                "--psm",
                "11",
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=120,
        )

        if result.returncode != 0:
            print(f"OCR_IMAGE_ERROR: {filepath}: {result.stderr}")
            return ""

        return result.stdout or ""

    except Exception as error:
        print(f"OCR_IMAGE_ERROR: {filepath}: {error}")
        return ""