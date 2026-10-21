@echo off
rem Windows wrapper: tools\autoheim.cmd <command> [key=value ...]
where py >nul 2>nul && (py -3 "%~dp0autoheim.py" %*) || (python "%~dp0autoheim.py" %*)
