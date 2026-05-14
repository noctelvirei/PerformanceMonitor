@echo off
setlocal
echo This package has been renamed to Performance Monitor Central Repository.
echo Running package-central-repository.cmd...
call "%~dp0package-central-repository.cmd" %*
exit /b %ERRORLEVEL%
