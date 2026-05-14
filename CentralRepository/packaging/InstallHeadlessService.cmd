@echo off
setlocal
echo This installer has been renamed to InstallCentralRepositoryService.cmd.
call "%~dp0InstallCentralRepositoryService.cmd" %*
exit /b %ERRORLEVEL%
