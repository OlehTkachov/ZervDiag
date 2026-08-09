import ctypes
from ctypes import wintypes

cldapi = ctypes.WinDLL("cldapi.dll", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GENERIC_READ = 0x80000000
FILE_SHARE_READ = 0x00000001
FILE_SHARE_WRITE = 0x00000002
OPEN_EXISTING = 3


def hydrate_file(path, length=None):
    path = str(path)

    kernel32.CreateFileW.restype = wintypes.HANDLE

    handle = kernel32.CreateFileW(
        path,
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        None,
        OPEN_EXISTING,
        0,
        None,
    )

    if handle == wintypes.HANDLE(-1).value:
        raise OSError(ctypes.get_last_error(), "CreateFileW failed")

    try:
        if length is None:
            import os
            length = os.path.getsize(path)

        cldapi.CfHydratePlaceholder.restype = wintypes.HRESULT

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
                "CfHydratePlaceholder failed"
            )

        return True

    finally:
        kernel32.CloseHandle(handle)
