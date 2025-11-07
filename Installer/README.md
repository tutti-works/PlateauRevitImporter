# PLATEAU CityGML Importer インストーラー

このディレクトリには、PLATEAU CityGML Importer for Revit のインストーラー関連ファイルが含まれています。

## ファイル構成

- `PlateauRevitImporter.wxs` - WiX Toolset インストーラー定義ファイル
- `PlateauRevitImporter.addin` - Revit アドイン登録ファイル（インストーラー用）
- `License.rtf` - ライセンス契約書
- `BuildInstaller.bat` - インストーラービルドスクリプト
- `README.md` - このファイル

## ビルド方法

### 前提条件

1. **WiX Toolset v4.x のインストール**
   ```bash
   dotnet tool install --global wix
   ```

2. **PlateauRevitImporter.dll のビルド**
   ```bash
   cd ..
   dotnet build -c Release
   ```

### インストーラーのビルド

#### 方法1: バッチスクリプトを使用（推奨）

```bash
cd Installer
BuildInstaller.bat
```

#### 方法2: 手動でビルド

```bash
cd Installer
wix build PlateauRevitImporter.wxs -o PlateauRevitImporter.msi
```

## インストーラーの配布

ビルドが成功すると、`PlateauRevitImporter.msi` が生成されます。

### インストール対象

- **Revit 2025/2026/2027** に対応
- **インストール先**:
  - DLL: `C:\Program Files (x86)\PlateauRevitImporter\`
  - Addin: `%ProgramData%\Autodesk\Revit\Addins\[バージョン]\`

### アンインストール

Windowsの「プログラムと機能」から「PLATEAU CityGML Importer for Revit」を選択してアンインストールできます。

## トラブルシューティング

### ビルドエラー: "WiX Toolset が見つかりません"

```bash
dotnet tool install --global wix
```

### ビルドエラー: "PlateauRevitImporter.dll が見つかりません"

先に Release ビルドを実行してください：

```bash
cd ..
dotnet build -c Release
```

### インストールエラー: "別のバージョンが既にインストールされています"

既存バージョンを先にアンインストールしてください。

## 開発者向け情報

### WiX 定義ファイルの構造

- **Directory構造**: Revit 2025/2026/2027 の各 Addins フォルダに .addin ファイルを配置
- **Component**: 各ファイルに一意の GUID を割り当て
- **Feature**: すべてのコンポーネントを1つの機能としてまとめる
- **MajorUpgrade**: 自動アップグレード機能を有効化

### .addin ファイルのパス

インストーラー用の `.addin` ファイルは、DLL の絶対パスを指定しています：

```xml
<Assembly>C:\Program Files (x86)\PlateauRevitImporter\PlateauRevitImporter.dll</Assembly>
```

開発用の `.addin` ファイル（プロジェクトルート）とは異なるため、混同しないよう注意してください。
