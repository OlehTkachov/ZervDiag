# ZervDiag Beta installer

## Purpose

This Beta is not a public release. It validates that ZervDiag works as a normal
Windows application without Python, Git, VS Code or PowerShell on the target PC.

## Runtime layout

Installed program:

`C:\Program Files\ZervDiag\`

Writable user data in the packaged build:

`%LOCALAPPDATA%\ZervDiag\`

This folder contains the working SQLite database, logs and process lock files.
Uninstalling or updating the program does not intentionally delete this user data.

Source/developer mode intentionally keeps the historical repository `data\`
folder so existing development OCR/indexing runs are not moved.

## First launch

If `%LOCALAPPDATA%\ZervDiag\zervdiag.db` does not exist, ZervDiag offers:

- Import an existing indexed database.
- Create a new empty database.
- Exit without changing anything.

Import uses the SQLite Backup API. The source is opened read-only, the imported
copy is checked with `PRAGMA quick_check`, and only a valid copy becomes the
working database.

## External components

The first packaged launch displays a component check:

- SQLite database and QUICK_CHECK.
- Tesseract OCR, including `rus` and `eng` language data.
- LibreOffice for legacy `.DOC` / `.XLS` conversion.
- Ollama as an optional local-AI component.

Missing optional components must not prevent normal local search from starting.

## Task Scheduler

The installed Beta contains two executables:

- `ZervDiag.exe` — GUI application.
- `ZervDiagScheduledIndex.exe` — silent background indexing runner.

The Windows Task Scheduler command is refreshed in the installed build to point
to `ZervDiagScheduledIndex.exe`. It uses the same `%LOCALAPPDATA%\ZervDiag`
database and lock files as the GUI.

## Build

Requirements:

- Windows x64.
- Python development environment for the build machine only.
- Inno Setup 6 to produce the final Setup executable.

Run from the repository:

```powershell
powershell -ExecutionPolicy Bypass -File .\packaging\build_beta.ps1
```

If Inno Setup is installed, final output:

`dist\installer\ZervDiag_Beta_Setup.exe`

If Inno Setup is absent, the unpacked PyInstaller application is still produced
under `dist\ZervDiag\` for smoke testing.

## First Beta smoke test

On another Windows PC:

1. Install `ZervDiag_Beta_Setup.exe`.
2. Start ZervDiag from Start Menu or desktop shortcut.
3. Confirm the title contains `ZervDiag Beta 0.15.0-beta.1`.
4. Import a known-good `zervdiag.db` backup.
5. Confirm the component check appears and reports the expected environment.
6. Open Database Statistics and compare total/status counts with the source DB.
7. Run known searches such as `Terex AC35L` and `ОНК160С E10`.
8. Select the documentation folder appropriate for this PC.
9. Test indexing.
10. If Tesseract is installed, test one OCR item.
11. Enable Windows background scheduling and verify the task points to
    `ZervDiagScheduledIndex.exe`.
12. Close ZervDiag, run the scheduled task, then inspect
    `%LOCALAPPDATA%\ZervDiag\scheduled_index.log`.
13. Uninstall/reinstall ZervDiag and confirm the user database remains intact.

Do not treat the Beta as passed until these steps have been verified on a clean
or otherwise independent Windows machine.
