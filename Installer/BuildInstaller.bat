@echo off
REM PLATEAU CityGML Importer インストーラービルドスクリプト
REM
REM 前提条件:
REM - WiX Toolset v4.x がインストールされていること
REM - dotnet wix extension がインストールされていること (dotnet tool install --global wix)
REM - PlateauRevitImporter.dll がビルド済みであること

echo ========================================
echo PLATEAU CityGML Importer インストーラービルド
echo ========================================
echo.

REM カレントディレクトリをスクリプトの場所に設定
cd /d "%~dp0"

REM Releaseビルドが存在するか確認
if not exist "..\bin\Release\net8.0-windows\PlateauRevitImporter.dll" (
    echo [エラー] PlateauRevitImporter.dll が見つかりません。
    echo 先に Release ビルドを実行してください。
    echo.
    echo コマンド例:
    echo   cd ..
    echo   dotnet build -c Release
    echo.
    pause
    exit /b 1
)

echo [1/3] WiX Toolset の確認...
where wix >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo [エラー] WiX Toolset が見つかりません。
    echo 以下のコマンドでインストールしてください:
    echo   dotnet tool install --global wix
    echo.
    pause
    exit /b 1
)
echo [OK] WiX Toolset が見つかりました。
echo.

echo [2/3] インストーラーのビルド...
wix build PlateauRevitImporter.wxs -o PlateauRevitImporter.msi
if %ERRORLEVEL% neq 0 (
    echo [エラー] インストーラーのビルドに失敗しました。
    pause
    exit /b 1
)
echo [OK] インストーラーのビルドが完了しました。
echo.

echo [3/3] 出力確認...
if exist "PlateauRevitImporter.msi" (
    echo [完了] PlateauRevitImporter.msi が正常に作成されました。
    echo 場所: %~dp0PlateauRevitImporter.msi
    echo.
    echo ファイルサイズ:
    dir PlateauRevitImporter.msi | findstr "PlateauRevitImporter.msi"
) else (
    echo [エラー] PlateauRevitImporter.msi が見つかりません。
    pause
    exit /b 1
)

echo.
echo ========================================
echo ビルド完了
echo ========================================
pause
