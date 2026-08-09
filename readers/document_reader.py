from pathlib import Path

from pypdf import PdfReader
from docx import Document


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


def read_document(filepath):
    extension = Path(filepath).suffix.lower()

    if extension == ".pdf":
        return read_pdf(filepath)

    if extension == ".docx":
        return read_docx(filepath)

    return ""