import os
from pathlib import Path

from app_paths import GUI_LOCK_PATH, WRITER_LOCK_PATH


class FileProcessLock:
    """Small cross-process advisory lock released automatically on process exit."""

    def __init__(self, path):
        self.path = Path(path)
        self.file = None
        self.locked = False

    def acquire(self):
        if self.locked:
            return True

        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.file = self.path.open("a+b")

        self.file.seek(0, os.SEEK_END)
        if self.file.tell() == 0:
            self.file.write(b"0")
            self.file.flush()

        self.file.seek(0)

        try:
            if os.name == "nt":
                import msvcrt

                msvcrt.locking(
                    self.file.fileno(),
                    msvcrt.LK_NBLCK,
                    1,
                )
            else:
                import fcntl

                fcntl.flock(
                    self.file.fileno(),
                    fcntl.LOCK_EX | fcntl.LOCK_NB,
                )
        except (OSError, PermissionError):
            self.file.close()
            self.file = None
            return False

        self.locked = True
        return True

    def release(self):
        if not self.file:
            self.locked = False
            return

        if self.locked:
            try:
                self.file.seek(0)
                if os.name == "nt":
                    import msvcrt

                    msvcrt.locking(
                        self.file.fileno(),
                        msvcrt.LK_UNLCK,
                        1,
                    )
                else:
                    import fcntl

                    fcntl.flock(
                        self.file.fileno(),
                        fcntl.LOCK_UN,
                    )
            except OSError:
                pass

        try:
            self.file.close()
        except OSError:
            pass

        self.file = None
        self.locked = False

    def __enter__(self):
        if not self.acquire():
            raise RuntimeError(f"Lock is already held: {self.path}")
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.release()

    def __del__(self):
        self.release()
