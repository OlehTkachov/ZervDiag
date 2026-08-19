import ctypes
import os
import sys
from ctypes import wintypes


if os.name != "nt":
    raise RuntimeError("Cloud Files API доступен только в Windows")


cldapi = ctypes.WinDLL(
    "cldapi.dll",
    use_last_error=True,
)

kernel32 = ctypes.WinDLL(
    "kernel32",
    use_last_error=True,
)


GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000

FILE_SHARE_READ = 0x00000001
FILE_SHARE_WRITE = 0x00000002
FILE_SHARE_DELETE = 0x00000004

OPEN_EXISTING = 3

FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000

INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


def _open_placeholder(path, access):
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.CreateFileW.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        ctypes.c_void_p,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.HANDLE,
    ]

    handle = kernel32.CreateFileW(
        str(path),
        access,
        (
            FILE_SHARE_READ
            | FILE_SHARE_WRITE
            | FILE_SHARE_DELETE
        ),
        None,
        OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT,
        None,
    )

    if handle == INVALID_HANDLE_VALUE:
        error = ctypes.get_last_error()
        raise OSError(
            error,
            "CreateFileW failed",
        )

    return handle


def hydrate(path):
    path = str(path)
    length = os.path.getsize(path)

    handle = _open_placeholder(
        path,
        GENERIC_READ,
    )

    try:
        cldapi.CfHydratePlaceholder.restype = wintypes.HRESULT
        cldapi.CfHydratePlaceholder.argtypes = [
            wintypes.HANDLE,
            ctypes.c_longlong,
            ctypes.c_longlong,
            wintypes.DWORD,
            ctypes.c_void_p,
        ]

        result = cldapi.CfHydratePlaceholder(
            handle,
            0,
            length,
            0,
            None,
        )

        if result != 0:
            raise OSError(
                result & 0xFFFFFFFF,
                "CfHydratePlaceholder failed",
            )

    finally:
        kernel32.CloseHandle(handle)


def dehydrate(path):
    path = str(path)
    length = os.path.getsize(path)

    handle = _open_placeholder(
        path,
        GENERIC_READ | GENERIC_WRITE,
    )

    try:
        cldapi.CfDehydratePlaceholder.restype = wintypes.HRESULT
        cldapi.CfDehydratePlaceholder.argtypes = [
            wintypes.HANDLE,
            ctypes.c_longlong,
            ctypes.c_longlong,
            wintypes.DWORD,
            ctypes.c_void_p,
        ]

        result = cldapi.CfDehydratePlaceholder(
            handle,
            0,
            length,
            0,
            None,
        )

        if result != 0:
            raise OSError(
                result & 0xFFFFFFFF,
                "CfDehydratePlaceholder failed",
            )

    finally:
        kernel32.CloseHandle(handle)


def main():
    if len(sys.argv) != 3:
        print(
            "Usage: cloud_worker.py "
            "hydrate|dehydrate <path>",
            file=sys.stderr,
        )
        return 2

    command = sys.argv[1].lower()
    path = sys.argv[2]

    if command == "hydrate":
        hydrate(path)
        return 0

    if command == "dehydrate":
        dehydrate(path)
        return 0

    print(
        f"Unknown command: {command}",
        file=sys.stderr,
    )

    return 2


if __name__ == "__main__":
    raise SystemExit(main())
