import sqlite3
from pathlib import Path

from app_paths import DATA_DIR
from database.libraries import get_active_database_path


def get_connection(database_path=None):
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    path = (
        Path(database_path).expanduser().resolve()
        if database_path is not None
        else get_active_database_path()
    )
    path.parent.mkdir(parents=True, exist_ok=True)

    conn = sqlite3.connect(
        path,
        timeout=30,
    )

    conn.execute(
        "PRAGMA journal_mode=WAL"
    )

    conn.execute(
        "PRAGMA foreign_keys=ON"
    )

    return conn


def create_database(database_path=None):
    conn = get_connection(database_path)

    try:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                filename TEXT NOT NULL,
                filepath TEXT NOT NULL UNIQUE,
                extension TEXT,
                size INTEGER,
                modified REAL,
                content TEXT,
                file_hash TEXT,
                is_cloud INTEGER DEFAULT 0,
                extraction_status TEXT DEFAULT 'pending',
                extraction_error TEXT,
                ocr_page INTEGER DEFAULT 0,
                ocr_total_pages INTEGER DEFAULT 0,
                ocr_updated REAL
            )
            """
        )

        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS app_meta (
                key TEXT PRIMARY KEY,
                value TEXT
            )
            """
        )

        # A newly created library has already passed the explicit first-run
        # database choice. Keep that acknowledgement inside this library DB
        # so switching to it does not show the legacy migration prompt again.
        conn.execute(
            """
            INSERT INTO app_meta (key, value)
            VALUES ('beta_database_choice_v2', '1')
            ON CONFLICT(key) DO UPDATE SET value = '1'
            """
        )

        columns = {
            row[1]
            for row in conn.execute(
                "PRAGMA table_info(files)"
            ).fetchall()
        }

        migrations = {
            "content":
                "ALTER TABLE files ADD COLUMN content TEXT",

            "file_hash":
                "ALTER TABLE files ADD COLUMN file_hash TEXT",

            "is_cloud":
                "ALTER TABLE files ADD COLUMN is_cloud INTEGER DEFAULT 0",

            "extraction_status":
                (
                    "ALTER TABLE files ADD COLUMN "
                    "extraction_status TEXT DEFAULT 'pending'"
                ),

            "extraction_error":
                (
                    "ALTER TABLE files ADD COLUMN "
                    "extraction_error TEXT"
                ),

            "ocr_page":
                (
                    "ALTER TABLE files ADD COLUMN "
                    "ocr_page INTEGER DEFAULT 0"
                ),

            "ocr_total_pages":
                (
                    "ALTER TABLE files ADD COLUMN "
                    "ocr_total_pages INTEGER DEFAULT 0"
                ),

            "ocr_updated":
                (
                    "ALTER TABLE files ADD COLUMN "
                    "ocr_updated REAL"
                ),
        }

        for column, sql in migrations.items():
            if column not in columns:
                conn.execute(
                    sql
                )

        conn.execute(
            """
            UPDATE files
            SET ocr_page = 0
            WHERE ocr_page IS NULL
            """
        )

        conn.execute(
            """
            UPDATE files
            SET ocr_total_pages = 0
            WHERE ocr_total_pages IS NULL
            """
        )

        # Если программа была аварийно закрыта во время обычной
        # индексации, transient status=processing не должен оставаться
        # навсегда. При следующем запуске файл снова доступен обработке.
        conn.execute(
            """
            UPDATE files
            SET
                extraction_status = 'pending',
                extraction_error = NULL
            WHERE extraction_status = 'processing'
            """
        )

        # Если программа была закрыта во время OCR,
        # при следующем запуске файл снова доступен очереди.
        conn.execute(
            """
            UPDATE files
            SET extraction_status = 'ocr_pending'
            WHERE extraction_status = 'ocr_processing'
            """
        )

        # V13: один раз переносим старые изображения, которые
        # раньше OCR-ились прямо в быстрой индексации, в общую
        # OCR-очередь. Включаем также старый status=ok с мусорным
        # коротким текстом, иначе следующая migration сделала бы
        # его pending уже после одноразового переноса.
        image_migration = conn.execute(
            """
            SELECT value
            FROM app_meta
            WHERE key = 'image_ocr_queue_v13'
            """
        ).fetchone()

        if not image_migration:
            conn.execute(
                """
                UPDATE files
                SET
                    extraction_status = 'ocr_pending',
                    extraction_error = NULL,
                    ocr_page = 0,
                    ocr_total_pages = 0,
                    ocr_updated = NULL
                WHERE extension IN (
                        '.jpg',
                        '.jpeg',
                        '.png',
                        '.tif',
                        '.tiff'
                      )
                  AND extraction_status IN (
                        'pending',
                        'processing',
                        'error',
                        'ok'
                      )
                  AND (
                        content IS NULL
                        OR length(trim(content)) < 10
                      )
                """
            )

            conn.execute(
                """
                INSERT INTO app_meta (
                    key,
                    value
                )
                VALUES (
                    'image_ocr_queue_v13',
                    '1'
                )
                """
            )

        # Старый "успешный" мусор вроде 'Ш №'
        # не считаем полноценным индексом.
        conn.execute(
            """
            UPDATE files
            SET extraction_status = 'pending'
            WHERE extraction_status = 'ok'
              AND (
                    content IS NULL
                    OR length(trim(content)) < 10
                  )
            """
        )

        conn.execute(
            """
            CREATE INDEX IF NOT EXISTS
            idx_files_filename
            ON files(filename)
            """
        )

        conn.execute(
            """
            CREATE INDEX IF NOT EXISTS
            idx_files_status
            ON files(extraction_status)
            """
        )

        conn.execute(
            """
            CREATE INDEX IF NOT EXISTS
            idx_files_cloud
            ON files(is_cloud)
            """
        )

        conn.commit()

    finally:
        conn.close()


def get_status_counts():
    conn = get_connection()

    try:
        rows = conn.execute(
            """
            SELECT
                extraction_status,
                COUNT(*)
            FROM files
            GROUP BY extraction_status
            """
        ).fetchall()

    finally:
        conn.close()

    return {
        (
            status
            if status
            else "pending"
        ): count
        for status, count in rows
    }


def get_ocr_pending_count():
    conn = get_connection()

    try:
        return conn.execute(
            """
            SELECT COUNT(*)
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
            """
        ).fetchone()[0]

    finally:
        conn.close()
