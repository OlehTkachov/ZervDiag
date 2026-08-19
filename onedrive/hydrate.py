import os
import subprocess
import sys
from pathlib import Path


_WORKER = (
    Path(__file__)
    .resolve()
    .with_name("cloud_worker.py")
)


def _run_cloud_worker(
    command,
    path,
    timeout,
):
    path = str(path)

    result = subprocess.run(
        [
            sys.executable,
            str(_WORKER),
            command,
            path,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )

    if result.returncode != 0:
        message = (
            result.stderr.strip()
            or result.stdout.strip()
            or f"{command} failed"
        )

        raise RuntimeError(message)

    return True


def hydrate_file(
    path,
    length=None,
    timeout=300,
):
    """
    Загружает OneDrive placeholder.

    Hydrate выполняется в отдельном процессе,
    поэтому зависание Cloud Files API не блокирует ZervDiag.
    """

    if os.name != "nt":
        return True

    return _run_cloud_worker(
        "hydrate",
        path,
        timeout,
    )


def dehydrate_file(
    path,
    length=None,
    timeout=20,
):
    """
    Возвращает файл под управление OneDrive без прямого
    CfDehydratePlaceholder.

    attrib +U -P — эквивалент команды OneDrive
    "Освободить место": файл помечается unpinned и OneDrive
    может удалить локальные данные без 30-секундного зависания
    CfDehydratePlaceholder.

    Физическое освобождение места OneDrive может выполнить
    не мгновенно, но ZervDiag ждать этого не должен.
    """

    if os.name != "nt":
        return True

    try:
        result = subprocess.run(
            [
                "attrib",
                "+U",
                "-P",
                str(path),
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
        )

        if result.returncode != 0:
            message = (
                result.stderr.strip()
                or result.stdout.strip()
                or "attrib +U -P failed"
            )

            print(
                f"WARNING: OneDrive release failed: "
                f"{path}: {message}",
                flush=True,
            )

            return False

        print(
            f"Released to OneDrive: {path}",
            flush=True,
        )

        return True

    except Exception as error:
        print(
            f"WARNING: OneDrive release error: "
            f"{path}: {error}",
            flush=True,
        )

        return False
