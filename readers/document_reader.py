from pathlib import Path
import shutil
import subprocess
import tempfile

from pypdf import PdfReader
from docx import Document
from openpyxl import load_workbook


def read_pdf(filepath):
    text = []

    try:
        reader = PdfReader(filepath)

        for page in reader.pages:
            page_text = page.extract_text()

            if page_text:
                text.append(page_text)

    except Exception:
        return ""

    return "\n".join(text)


def read_docx(filepath):
    text = []

    try:
        document = Document(filepath)

        for paragraph in document.paragraphs:
            if paragraph.text:
                text.append(paragraph.text)

        for table in document.tables:
            for row in table.rows:
                for cell in row.cells:
                    if cell.text:
                        text.append(cell.text)

    except Exception:
        return ""

    return "\n".join(text)


def read_xlsx(filepath):
    text = []

    try:
        workbook = load_workbook(
            filepath,
            read_only=True,
            data_only=True
        )

        for sheet in workbook.worksheets:
            text.append(f"\n[{sheet.title}]")

            for row in sheet.iter_rows(values_only=True):
                values = []

                for value in row:
                    if value is not None:
                        values.append(str(value))

                if values:
                    text.append(" | ".join(values))

        workbook.close()

    except Exception:
        return ""

    return "\n".join(text)


def find_libreoffice():
    """Находит LibreOffice в системе."""

    soffice = shutil.which("soffice")

    if soffice:
        return soffice

    possible_paths = [
        Path(r"C:\Program Files\LibreOffice\program\soffice.exe"),
        Path(r"C:\Program Files (x86)\LibreOffice\program\soffice.exe"),
    ]

    for path in possible_paths:
        if path.exists():
            return str(path)

    return None


def convert_with_libreoffice(filepath):
    """
    Конвертирует .doc/.xls во временный .docx/.xlsx
    с помощью LibreOffice.
    """

    soffice = find_libreoffice()

    if not soffice:
        return None

    source = Path(filepath)

    converted_dir = Path(
        tempfile.mkdtemp(prefix="zervdiag_convert_")
    )

    try:
        result = subprocess.run(
            [
                soffice,
                "--headless",
                "--convert-to",
                "docx" if source.suffix.lower() == ".doc" else "xlsx",
                "--outdir",
                str(converted_dir),
                str(source),
            ],
            capture_output=True,
            text=True,
            timeout=120,
        )

        if result.returncode != 0:
            return None

        converted = converted_dir / (
            source.stem
            + (".docx" if source.suffix.lower() == ".doc" else ".xlsx")
        )

        if not converted.exists():
            return None

        return converted

    except Exception:
        return None


def read_doc(filepath):
    converted = convert_with_libreoffice(filepath)

    if not converted:
        return ""

    try:
        return read_docx(converted)

    finally:
        shutil.rmtree(
            converted.parent,
            ignore_errors=True
        )


def read_xls(filepath):
    converted = convert_with_libreoffice(filepath)

    if not converted:
        return ""

    try:
        return read_xlsx(converted)

    finally:
        shutil.rmtree(
            converted.parent,
            ignore_errors=True
        )


def read_document(filepath):
    extension = Path(filepath).suffix.lower()

    if extension == ".pdf":
        return read_pdf(filepath)

    if extension == ".docx":
        return read_docx(filepath)

    if extension == ".doc":
        return read_doc(filepath)

    if extension == ".xlsx":
        return read_xlsx(filepath)

    if extension == ".xls":
        return read_xls(filepath)

    return ""