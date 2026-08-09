import hashlib
import os

from database.db import get_connection


def calculate_hash(filepath):

    sha256 = hashlib.sha256()

    try:

        with open(filepath, "rb") as file:

            while True:

                chunk = file.read(1024 * 1024)

                if not chunk:
                    break

                sha256.update(chunk)

        return sha256.hexdigest()

    except Exception:

        return None


def find_duplicates():

    conn = get_connection()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT
            id,
            filename,
            filepath,
            is_cloud
        FROM files
        ORDER BY filename
    """)

    rows = cursor.fetchall()

    conn.close()

    # Сначала группируем файлы по имени.
    # Это позволяет быстро найти одинаковые документы.
    groups = {}

    for row in rows:

        file_id = row[0]
        filename = row[1] or ""
        filepath = row[2] or ""
        is_cloud = bool(row[3])

        key = filename.lower().strip()

        if not key:
            continue

        if key not in groups:
            groups[key] = []

        groups[key].append({
            "id": file_id,
            "filename": filename,
            "filepath": filepath,
            "is_cloud": is_cloud,
        })

    duplicates = []

    for filename, files in groups.items():

        if len(files) < 2:
            continue

        # Пока точно подтверждаем клоны
        # только для локальных файлов.
        local_files = [
            file
            for file in files
            if not file["is_cloud"]
            and os.path.isfile(file["filepath"])
        ]

        if len(local_files) < 2:
            continue

        hashes = {}

        for file in local_files:

            file_hash = calculate_hash(
                file["filepath"]
            )

            if not file_hash:
                continue

            if file_hash not in hashes:
                hashes[file_hash] = []

            hashes[file_hash].append(file)

        for file_hash, same_files in hashes.items():

            if len(same_files) > 1:

                duplicates.append({
                    "hash": file_hash,
                    "files": same_files,
                })

    return duplicates