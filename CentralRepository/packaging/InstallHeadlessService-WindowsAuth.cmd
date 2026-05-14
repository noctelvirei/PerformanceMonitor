@echo off
setlocal
echo This installer has been renamed to InstallCentralRepositoryService-WindowsAuth.cmd.
call "%~dp0InstallCentralRepositoryService-WindowsAuth.cmd" %*
exit /b %ERRORLEVEL%
