from app_paths import INSTALL_DIR, LOG_DIR, is_frozen


def configure_black_box_paths():
    """Redirect legacy black-box globals only in an installed/frozen build."""
    if not is_frozen():
        return

    from diagnostics import black_box

    black_box.LOG_DIR = LOG_DIR
    black_box.APP_LOG_PATH = LOG_DIR / "zervdiag.log"
    black_box.CRASH_LOG_PATH = LOG_DIR / "crash.log"
    black_box.FATAL_LOG_PATH = LOG_DIR / "fatal.log"
    black_box.SESSION_PATH = LOG_DIR / "session_state.json"


def configure_packaged_scheduler(module):
    """Make Task Scheduler call the bundled background runner, not Python."""
    if not is_frozen():
        return

    runner = INSTALL_DIR / "ZervDiagScheduledIndex.exe"

    def packaged_task_command():
        return f'"{runner}"'

    module._task_command = packaged_task_command

    # A task created by a source/developer build may already exist. In the
    # installed Beta refresh it once so /TR points to the packaged runner.
    def packaged_ensure_windows_task(settings):
        return module.sync_windows_task(settings)

    module.ensure_windows_task = packaged_ensure_windows_task
