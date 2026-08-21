import faulthandler
import json
import logging
import os
import platform
import subprocess
import sys
import threading
import traceback
import uuid
from datetime import datetime
from logging.handlers import RotatingFileHandler
from pathlib import Path
from types import MethodType


BASE_DIR = Path(__file__).resolve().parent.parent
LOG_DIR = BASE_DIR / "data" / "logs"
APP_LOG_PATH = LOG_DIR / "zervdiag.log"
CRASH_LOG_PATH = LOG_DIR / "crash.log"
FATAL_LOG_PATH = LOG_DIR / "fatal.log"
SESSION_PATH = LOG_DIR / "session_state.json"

MAX_LOG_BYTES = 2 * 1024 * 1024
BACKUP_COUNT = 5

_app_logger = None
_crash_logger = None
_fatal_file = None
_session = None
_hooks_installed = False


def _ensure_log_dir():
    LOG_DIR.mkdir(parents=True, exist_ok=True)


def _make_logger(name, path, level):
    logger = logging.getLogger(name)
    logger.setLevel(level)
    logger.propagate = False

    for handler in list(logger.handlers):
        logger.removeHandler(handler)
        try:
            handler.close()
        except Exception:
            pass

    handler = RotatingFileHandler(
        path,
        maxBytes=MAX_LOG_BYTES,
        backupCount=BACKUP_COUNT,
        encoding="utf-8",
    )
    handler.setFormatter(
        logging.Formatter(
            "%(asctime)s.%(msecs)03d | %(levelname)s | "
            "pid=%(process)d | %(threadName)s | %(message)s",
            datefmt="%Y-%m-%d %H:%M:%S",
        )
    )
    logger.addHandler(handler)
    return logger


def get_app_logger():
    global _app_logger

    if _app_logger is None:
        _ensure_log_dir()
        _app_logger = _make_logger(
            "zervdiag",
            APP_LOG_PATH,
            logging.INFO,
        )

    return _app_logger


def get_crash_logger():
    global _crash_logger

    if _crash_logger is None:
        _ensure_log_dir()
        _crash_logger = _make_logger(
            "zervdiag.crash",
            CRASH_LOG_PATH,
            logging.ERROR,
        )

    return _crash_logger


def _git_revision():
    try:
        result = subprocess.run(
            ["git", "-C", str(BASE_DIR), "rev-parse", "--short=12", "HEAD"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=2,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        if result.returncode == 0:
            return result.stdout.decode("utf-8", errors="replace").strip()
    except Exception:
        pass

    return "unknown"


def _write_json_atomic(path, payload):
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    temp.replace(path)


def _read_previous_session():
    if not SESSION_PATH.exists():
        return None

    try:
        return json.loads(SESSION_PATH.read_text(encoding="utf-8"))
    except Exception as error:
        get_app_logger().warning("SESSION STATE READ FAILED | %s", error)
        return None


class BlackBoxSession:
    def __init__(self):
        self.session_id = str(uuid.uuid4())
        self.started = datetime.now().isoformat(timespec="seconds")
        self.clean = False
        self.closed = False

    def payload(self):
        return {
            "session_id": self.session_id,
            "pid": os.getpid(),
            "started": self.started,
            "clean": self.clean,
            "python": sys.version.split()[0],
            "executable": sys.executable,
            "cwd": os.getcwd(),
            "git": _git_revision(),
        }

    def write(self):
        _write_json_atomic(SESSION_PATH, self.payload())

    def mark_clean(self):
        if self.closed:
            return

        self.clean = True
        self.closed = True

        try:
            self.write()
        except Exception as error:
            get_app_logger().error("SESSION CLEAN MARK FAILED | %s", error)

        get_app_logger().info(
            "CLEAN SHUTDOWN | session=%s",
            self.session_id,
        )

        if _fatal_file is not None:
            try:
                _fatal_file.flush()
            except Exception:
                pass


def _exception_text(exc_type, exc_value, exc_traceback):
    return "".join(
        traceback.format_exception(
            exc_type,
            exc_value,
            exc_traceback,
        )
    ).rstrip()


def _record_exception(label, exc_type, exc_value, exc_traceback):
    text = _exception_text(exc_type, exc_value, exc_traceback)

    get_crash_logger().critical("%s\n%s", label, text)
    get_app_logger().critical(
        "%s | %s: %s",
        label,
        getattr(exc_type, "__name__", str(exc_type)),
        exc_value,
    )


def _install_exception_hooks():
    global _hooks_installed

    if _hooks_installed:
        return

    _hooks_installed = True

    def sys_hook(exc_type, exc_value, exc_traceback):
        _record_exception(
            "UNHANDLED EXCEPTION",
            exc_type,
            exc_value,
            exc_traceback,
        )
        try:
            sys.__excepthook__(exc_type, exc_value, exc_traceback)
        except Exception:
            pass

    sys.excepthook = sys_hook

    if hasattr(threading, "excepthook"):
        def thread_hook(args):
            _record_exception(
                f"UNHANDLED THREAD EXCEPTION | thread={args.thread.name}",
                args.exc_type,
                args.exc_value,
                args.exc_traceback,
            )

        threading.excepthook = thread_hook

    if hasattr(sys, "unraisablehook"):
        def unraisable_hook(args):
            exc_value = args.exc_value or RuntimeError(
                args.err_msg or "Unraisable exception"
            )
            _record_exception(
                f"UNRAISABLE EXCEPTION | object={args.object!r}",
                type(exc_value),
                exc_value,
                args.exc_traceback,
            )

        sys.unraisablehook = unraisable_hook


def _install_qt_message_handler():
    try:
        from PySide6.QtCore import qInstallMessageHandler
    except Exception as error:
        get_app_logger().warning("QT MESSAGE HANDLER UNAVAILABLE | %s", error)
        return

    def qt_handler(message_type, context, message):
        type_name = getattr(message_type, "name", str(message_type))
        location = ""

        try:
            if context and context.file:
                location = (
                    f" | {context.file}:{context.line}"
                    f" | {context.function or ''}"
                )
        except Exception:
            pass

        text = f"QT {type_name}: {message}{location}"
        lowered = type_name.casefold()

        if "fatal" in lowered:
            get_crash_logger().critical(text)
            get_app_logger().critical(text)
        elif "critical" in lowered:
            get_crash_logger().error(text)
            get_app_logger().error(text)
        elif "warning" in lowered:
            get_app_logger().warning(text)
        else:
            get_app_logger().info(text)

    qInstallMessageHandler(qt_handler)


def _rotate_fatal_log():
    try:
        if not FATAL_LOG_PATH.exists():
            return
        if FATAL_LOG_PATH.stat().st_size < MAX_LOG_BYTES:
            return

        for index in range(BACKUP_COUNT, 0, -1):
            source = FATAL_LOG_PATH.with_name(
                FATAL_LOG_PATH.name
                if index == 1
                else f"{FATAL_LOG_PATH.name}.{index - 1}"
            )
            target = FATAL_LOG_PATH.with_name(
                f"{FATAL_LOG_PATH.name}.{index}"
            )

            if target.exists():
                target.unlink()
            if source.exists():
                source.replace(target)
    except Exception as error:
        get_app_logger().warning("FATAL LOG ROTATION FAILED | %s", error)


def _enable_faulthandler():
    global _fatal_file

    _ensure_log_dir()
    _rotate_fatal_log()

    try:
        _fatal_file = FATAL_LOG_PATH.open(
            "a",
            encoding="utf-8",
            buffering=1,
        )
        _fatal_file.write(
            "\n"
            f"[{datetime.now().isoformat(timespec='seconds')}] "
            f"FAULTHANDLER ENABLED pid={os.getpid()}\n"
        )
        _fatal_file.flush()

        faulthandler.enable(
            file=_fatal_file,
            all_threads=True,
        )
    except Exception as error:
        get_app_logger().error("FAULTHANDLER ENABLE FAILED | %s", error)


def install_black_box(app):
    global _session

    _ensure_log_dir()
    logger = get_app_logger()
    get_crash_logger()

    previous = _read_previous_session()
    if previous and not previous.get("clean", False):
        message = (
            "UNCLEAN PREVIOUS EXIT"
            f" | session={previous.get('session_id', '?')}"
            f" | pid={previous.get('pid', '?')}"
            f" | started={previous.get('started', '?')}"
            f" | git={previous.get('git', '?')}"
        )
        logger.warning(message)
        get_crash_logger().error(message)

    _session = BlackBoxSession()
    try:
        _session.write()
    except Exception as error:
        logger.error("SESSION STATE WRITE FAILED | %s", error)

    _install_exception_hooks()
    _install_qt_message_handler()
    _enable_faulthandler()

    logger.info(
        "STARTUP | session=%s | python=%s | platform=%s | "
        "executable=%s | cwd=%s | git=%s",
        _session.session_id,
        sys.version.replace("\n", " "),
        platform.platform(),
        sys.executable,
        os.getcwd(),
        _git_revision(),
    )

    app.aboutToQuit.connect(_session.mark_clean)
    return _session


def install_runtime_event_logging(main_window):
    if getattr(main_window, "_v14_runtime_event_logging_installed", False):
        return

    main_window._v14_runtime_event_logging_installed = True
    logger = get_app_logger()

    original_start_indexing = main_window.start_indexing
    original_indexing_finished = main_window.indexing_finished
    original_indexing_error = main_window.indexing_error

    def start_indexing_logged(self):
        logger.info(
            "INDEX REQUEST | folder=%s",
            self.settings.value("documentation_folder", ""),
        )
        previous_worker = self.index_worker
        original_start_indexing()
        if (
            self.index_worker
            and self.index_worker is not previous_worker
            and self.index_worker.isRunning()
        ):
            logger.info("INDEX START")

    def indexing_finished_logged(
        self,
        added,
        updated,
        skipped,
        deleted,
        total,
        stopped,
    ):
        logger.info(
            "INDEX FINISH | stopped=%s | total=%s | added=%s | "
            "updated=%s | skipped=%s | deleted=%s",
            stopped,
            total,
            added,
            updated,
            skipped,
            deleted,
        )
        return original_indexing_finished(
            added,
            updated,
            skipped,
            deleted,
            total,
            stopped,
        )

    def indexing_error_logged(self, message):
        logger.error("INDEX ERROR | %s", message)
        return original_indexing_error(message)

    main_window.start_indexing = MethodType(start_indexing_logged, main_window)
    main_window.indexing_finished = MethodType(
        indexing_finished_logged,
        main_window,
    )
    main_window.indexing_error = MethodType(indexing_error_logged, main_window)

    original_start_ocr = main_window.start_ocr
    original_ocr_finished = main_window.ocr_finished
    original_ocr_error = main_window.ocr_error

    def start_ocr_logged(self):
        logger.info("OCR REQUEST")
        previous_worker = self.ocr_worker
        original_start_ocr()
        if (
            self.ocr_worker
            and self.ocr_worker is not previous_worker
            and self.ocr_worker.isRunning()
        ):
            logger.info("OCR START")

    def ocr_finished_logged(self, processed, errors, total, stopped):
        logger.info(
            "OCR FINISH | stopped=%s | processed=%s | errors=%s | total=%s",
            stopped,
            processed,
            errors,
            total,
        )
        return original_ocr_finished(
            processed,
            errors,
            total,
            stopped,
        )

    def ocr_error_logged(self, message):
        logger.error("OCR ERROR | %s", message)
        return original_ocr_error(message)

    main_window.start_ocr = MethodType(start_ocr_logged, main_window)
    main_window.ocr_finished = MethodType(ocr_finished_logged, main_window)
    main_window.ocr_error = MethodType(ocr_error_logged, main_window)

    original_search_finished = main_window.search_finished
    original_search_error = main_window.search_error

    def search_finished_logged(self, results):
        logger.info("SEARCH FINISH | results=%s", len(results or []))
        return original_search_finished(results)

    def search_error_logged(self, message):
        logger.error("SEARCH ERROR | %s", message)
        return original_search_error(message)

    main_window.search_finished = MethodType(search_finished_logged, main_window)
    main_window.search_error = MethodType(search_error_logged, main_window)

    def log_search_request(*_args):
        logger.info(
            "SEARCH REQUEST | query=%r",
            main_window.search_input.text().strip(),
        )

    main_window.search_input.returnPressed.connect(log_search_request)
    main_window.search_button.clicked.connect(log_search_request)
    main_window.btn_search.clicked.connect(log_search_request)

    logger.info("RUNTIME EVENT LOGGING INSTALLED")
