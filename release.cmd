@echo off
REM Release wrapper: publishes a new Counterplay release.
REM
REM   release              build + upload a new release (auto version)
REM   release -FeedOnly -Version 1.0.90    refresh the feed of an existing one
REM   release -Notes "..."                 custom release notes
REM
REM Exists because running the .ps1 directly is blocked by ExecutionPolicy, and
REM typing the full "powershell -ExecutionPolicy Bypass -File ..." invitation is
REM error-prone. Policy is relaxed for this one process only, not system-wide.
REM
REM Comments are ASCII on purpose: cmd.exe reads .cmd in the OEM codepage and
REM chokes on UTF-8 Cyrillic, trying to execute the mangled comment lines.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\release.ps1" -Upload %*
