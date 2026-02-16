@echo off
setlocal enabledelayedexpansion

echo ========================================
echo VortexCut - Build Rust Engine and Copy DLLs
echo ========================================
echo.

set DEST_DIR=%~dp0VortexCut.UI\bin\Debug\net8.0

:: 앱이 실행 중인지 확인 (DLL 잠금 방지)
tasklist /FI "IMAGENAME eq VortexCut.UI.exe" 2>NUL | find /I "VortexCut.UI.exe" >NUL
if !ERRORLEVEL! EQU 0 (
    echo [ERROR] VortexCut.UI.exe is running. Please close it first.
    pause
    exit /b 1
)

cd /d "%~dp0rust-engine"

:: 빌드 모드 선택
set BUILD_MODE=release
set BUILD_FLAG=--release
set FEATURE_FLAG=
set LOG_MODE=off

:: 인자 파싱: debug, log, debug log, log debug 모두 지원
for %%A in (%*) do (
    if "%%A"=="debug" (
        set BUILD_MODE=debug
        set BUILD_FLAG=
    )
    if "%%A"=="log" (
        set FEATURE_FLAG=--features debug_log
        set LOG_MODE=on
    )
)

echo [1/3] Building Rust engine (%BUILD_MODE%, log=%LOG_MODE%)...
cargo build %BUILD_FLAG% %FEATURE_FLAG%
if !ERRORLEVEL! NEQ 0 (
    echo [ERROR] Rust build failed!
    pause
    exit /b !ERRORLEVEL!
)

:: 출력 디렉토리 확인
if not exist "%DEST_DIR%" (
    echo    Creating output directory...
    mkdir "%DEST_DIR%"
)

echo.
echo [2/3] Copying rust_engine.dll...
copy /Y "target\%BUILD_MODE%\rust_engine.dll" "%DEST_DIR%\rust_engine.dll" >NUL
if !ERRORLEVEL! NEQ 0 (
    echo [ERROR] rust_engine.dll copy failed! (file may be locked)
    pause
    exit /b 1
)
echo    OK: rust_engine.dll (bin root)

:: runtimes 경로에도 복사 (.NET이 runtimes/win-x64/native/ 우선 로드)
set RUNTIMES_DIR=%~dp0VortexCut.UI\runtimes\win-x64\native
if exist "%RUNTIMES_DIR%" (
    copy /Y "target\%BUILD_MODE%\rust_engine.dll" "%RUNTIMES_DIR%\rust_engine.dll" >NUL
    echo    OK: rust_engine.dll (runtimes)
)
set RUNTIMES_BIN_DIR=%DEST_DIR%\runtimes\win-x64\native
if exist "%RUNTIMES_BIN_DIR%" (
    copy /Y "target\%BUILD_MODE%\rust_engine.dll" "%RUNTIMES_BIN_DIR%\rust_engine.dll" >NUL
    echo    OK: rust_engine.dll (bin runtimes)
)

echo.
echo [3/3] Copying FFmpeg DLLs...
:: FFMPEG_DIR 해시를 동적으로 찾기
set FFMPEG_DIR=
for /d %%D in (target\%BUILD_MODE%\build\ffmpeg-sys-next-*) do (
    if exist "%%D\out\avcodec-62.dll" (
        set FFMPEG_DIR=%%D\out
    )
)

if "!FFMPEG_DIR!"=="" (
    echo    [WARN] FFmpeg build dir not found - skipping
    goto :done
)

echo    Found: !FFMPEG_DIR!

set COPY_OK=1
for %%F in (avcodec-62 avdevice-62 avfilter-11 avformat-62 avutil-60 swresample-6 swscale-9) do (
    if exist "!FFMPEG_DIR!\%%F.dll" (
        copy /Y "!FFMPEG_DIR!\%%F.dll" "%DEST_DIR%\%%F.dll" >NUL
        if !ERRORLEVEL! NEQ 0 (
            echo    FAIL: %%F.dll
            set COPY_OK=0
        ) else (
            echo    OK: %%F.dll
        )
    )
)

if exist "!FFMPEG_DIR!\pkgconf-7.dll" (
    copy /Y "!FFMPEG_DIR!\pkgconf-7.dll" "%DEST_DIR%\pkgconf-7.dll" >NUL
)

if "!COPY_OK!"=="0" (
    echo.
    echo [WARN] Some FFmpeg DLLs failed to copy
)

:done
echo.
echo ========================================
echo Done! (%BUILD_MODE% mode, log=%LOG_MODE%)
echo Output: %DEST_DIR%
echo.
echo Usage: build-and-copy-dll.bat [debug] [log]
echo   default = release, log off
echo   debug   = debug build
echo   log     = enable debug_log feature (stderr 로그 출력)
echo   예: build-and-copy-dll.bat log
echo   예: build-and-copy-dll.bat debug log
echo ========================================
pause
