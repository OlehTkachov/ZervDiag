from database.db import get_connection


FILTERS = {
    "all": ("", ()),
    "ok": ("WHERE extraction_status = ?", ("ok",)),
    "ocr": (
        "WHERE extraction_status IN ('ocr_pending', 'ocr_processing')",
        (),
    ),
    "error": ("WHERE extraction_status = ?", ("error",)),
    "pending": (
        "WHERE extraction_status IS NULL "
        "OR extraction_status IN ('pending', 'processing')",
        (),
    ),
    "unsupported": (
        "WHERE extraction_status = ?",
        ("unsupported",),
    ),
}


def get_database_summary():
    conn = get_connection()
    try:
        row = conn.execute(
            """
            SELECT
                COUNT(*),
                SUM(CASE WHEN extraction_status = 'ok' THEN 1 ELSE 0 END),
                SUM(
                    CASE
                        WHEN extraction_status IN ('ocr_pending', 'ocr_processing')
                        THEN 1 ELSE 0
                    END
                ),
                SUM(CASE WHEN extraction_status = 'error' THEN 1 ELSE 0 END),
                SUM(
                    CASE
                        WHEN extraction_status IS NULL
                          OR extraction_status IN ('pending', 'processing')
                        THEN 1 ELSE 0
                    END
                ),
                SUM(CASE WHEN extraction_status = 'unsupported' THEN 1 ELSE 0 END)
            FROM files
            """
        ).fetchone()
    finally:
        conn.close()

    row = row or (0, 0, 0, 0, 0, 0)
    return {
        "total": int(row[0] or 0),
        "ok": int(row[1] or 0),
        "ocr": int(row[2] or 0),
        "error": int(row[3] or 0),
        "pending": int(row[4] or 0),
        "unsupported": int(row[5] or 0),
    }


def get_status_files(filter_name="all", limit=5000):
    where_sql, params = FILTERS.get(filter_name, FILTERS["all"])

    if filter_name == "ocr":
        order_sql = """
            ORDER BY
                CASE
                    WHEN ocr_total_pages > 0 THEN ocr_total_pages
                    ELSE 2147483647
                END,
                id
        """
    elif filter_name == "error":
        order_sql = "ORDER BY extension, filename"
    else:
        order_sql = "ORDER BY id"

    conn = get_connection()
    try:
        return conn.execute(
            f"""
            SELECT
                id,
                filename,
                extension,
                COALESCE(extraction_status, 'pending'),
                COALESCE(extraction_error, ''),
                filepath,
                COALESCE(is_cloud, 0),
                COALESCE(ocr_page, 0),
                COALESCE(ocr_total_pages, 0),
                length(COALESCE(content, ''))
            FROM files
            {where_sql}
            {order_sql}
            LIMIT ?
            """,
            tuple(params) + (int(limit),),
        ).fetchall()
    finally:
        conn.close()
