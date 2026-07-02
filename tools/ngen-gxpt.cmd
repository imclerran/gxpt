@echo off
rem Generates (or removes) NGEN native images for a built GxPT.exe and its dependency
rem closure (Krypton etc.), for testing launch performance without running the installer.
rem The installed app gets the same treatment automatically via the GxPT.Setup custom
rem action (see GxPT\Services\NgenInstaller.cs).
rem
rem Usage (from an ADMIN command prompt):
rem   ngen-gxpt.cmd                 - ngen install the Release build
rem   ngen-gxpt.cmd uninstall      - remove the native images again
rem   ngen-gxpt.cmd install "C:\path\to\GxPT.exe"   - explicit exe path

setlocal
set VERB=%1
if "%VERB%"=="" set VERB=install

set EXE=%~2
if "%EXE%"=="" set EXE=%~dp0..\GxPT\bin\Release\GxPT.exe

set NGEN=%WINDIR%\Microsoft.NET\Framework\v2.0.50727\ngen.exe
if not exist "%NGEN%" (
    echo ngen.exe not found at %NGEN% - is .NET 3.5 installed?
    exit /b 1
)
if not exist "%EXE%" (
    echo GxPT.exe not found at %EXE% - build Release first or pass the path.
    exit /b 1
)

echo Running: "%NGEN%" %VERB% "%EXE%" /nologo
"%NGEN%" %VERB% "%EXE%" /nologo
echo Done (exit code %ERRORLEVEL%). Launch GxPT and compare the STARTUP lines in theme-perf.log.
endlocal
