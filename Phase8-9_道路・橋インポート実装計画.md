# Phase 8-9: 道路・橋インポート機能 実装計画書（改訂版）

**文書バージョン**: 2.0
**作成日**: 2025年11月7日
**最終更新**: 2025年11月7日
**ステータス**: 調査完了・実装計画確定

---

## 概要

現在のPLATEAU Revitインポーターは建物（bldg:Building）のみに対応しています。本計画書は、道路（tran:Road）と橋（brid:Bridge）のインポート機能追加に向けた実装計画をまとめたものです。

**重要な調査結果**：
- PLATEAUのGMLファイルは **1ファイル = 1地物タイプ** が確定
- ファイル名から地物タイプを100%自動判別可能
- 道路のLOD1/LOD2は平面形状（Z=0）、**LOD3のみ立体形状**を持つ
- 建物はLOD1/LOD2で実際の高さを持つ

---

## 調査結果：道路データの構造分析

### 実際のファイル構造

**ディレクトリ構造**：
```
{地域コード}_{自治体名}_2023_citygml_2_op/udx/
├── bldg/    # 建物ファイル（*bldg*.gml）
├── tran/    # 道路ファイル（*tran*.gml）
└── brid/    # 橋ファイル（*brid*.gml）
```

**ファイル命名規則**：
```
{メッシュコード}_{地物種別}_{座標系コード}_op.gml
例：53393652_tran_6697_op.gml
    ^^^^^^^^ ^^^^ ^^^^ ^^
    メッシュ 道路 座標 オープンデータ
```

### LOD構造の実態

#### 道路データの各LOD

| LOD | 構造 | Z座標の値 | 用途 |
|-----|------|----------|------|
| **LOD1** | `lod1MultiSurface` | **すべて0** | 平面形状（2.5D） |
| **LOD2** | `lod2MultiSurface` | **すべて0** | 平面形状（詳細版） |
| **LOD3** | `lod3MultiSurface` | **2.7m～5.0m** | 立体形状（起伏あり） |

**統計（実測値）**：
- LOD1: 全Road要素に存在
- LOD2: 626箇所出現
- **LOD3: 446箇所出現**（実際の高さデータあり）

#### 建物データとの比較

| 項目 | 建物 (Building) | 道路 (Road) |
|------|----------------|-------------|
| **LOD1** | 箱型（実高さあり：5.3m～100m） | 平面（Z=0） |
| **LOD2** | 詳細形状（窓・屋根、実高さあり） | 平面（Z=0） |
| **LOD3** | （存在しない） | **立体形状（起伏あり）** |
| **ジオメトリ型** | Solid（立体） | MultiSurface（面） |

### XML構造例

**道路LOD3の実際のデータ**：
```xml
<tran:Road gml:id="tran_7471df56-d890-4856-9f91-36df7979cfd9">
    <gml:name>国道357号</gml:name>

    <!-- LOD3: 実際の高さを持つ -->
    <tran:lod3MultiSurface>
        <gml:MultiSurface>
            <gml:surfaceMember>
                <gml:Polygon>
                    <gml:exterior>
                        <gml:LinearRing>
                            <gml:posList>
35.630164297707154 139.78117887508537 3.369
35.63012936960423 139.78101337525717 2.937
35.630081634783394 139.78116401276904 3.715
<!-- Z座標が実際の高さ（2.9m～3.7m）を表現 -->
                            </gml:posList>
                        </gml:LinearRing>
                    </gml:exterior>
                </gml:Polygon>
            </gml:surfaceMember>
        </gml:MultiSurface>
    </tran:lod3MultiSurface>
</tran:Road>
```

---

## Phase 8: 道路・橋インポート機能の実装計画

### 8.1. ファイルタイプ自動判別機能

**実装内容**：
```csharp
/// <summary>
/// ファイル名から地物タイプを自動判別
/// </summary>
private static CityObjectType DetectFileType(string filePath)
{
    string fileName = Path.GetFileName(filePath).ToLower();

    if (fileName.Contains("bldg")) return CityObjectType.Building;
    if (fileName.Contains("tran")) return CityObjectType.Road;
    if (fileName.Contains("brid")) return CityObjectType.Bridge;

    // フォールバック: XML内容から判別
    XDocument doc = XDocument.Load(filePath);

    if (doc.Descendants().Any(e => e.Name.LocalName == "Building"))
        return CityObjectType.Building;
    if (doc.Descendants().Any(e => e.Name.LocalName == "Road"))
        return CityObjectType.Road;
    if (doc.Descendants().Any(e => e.Name.LocalName == "Bridge"))
        return CityObjectType.Bridge;

    throw new Exception("サポートされていない地物タイプです");
}
```

**UIの変更**：
- ❌ **削除**：「建物」「道路」「建物+道路」の選択ダイアログ
- ✅ **自動判別**：ファイル名から地物タイプを検出

### 8.2. LOD処理の改善

**方針**：
1. **建物の場合**：従来通りLOD選択ダイアログを表示（LOD1 / LOD2）
2. **道路・橋の場合**：LOD選択をスキップし、利用可能な最大LODを自動選択

**実装ロジック**：
```csharp
// ファイルタイプを自動判別
CityObjectType detectedType = DetectFileType(gmlFilePath);

int targetLod;

if (detectedType == CityObjectType.Building)
{
    // 建物：ユーザーにLOD選択させる
    TaskDialog lodDialog = new TaskDialog("LOD選択");
    lodDialog.MainInstruction = "インポートするLODレベルを選択してください";
    lodDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "LOD1 (簡易形状)");
    lodDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "LOD2 (詳細形状)");

    TaskDialogResult lodResult = lodDialog.Show();
    targetLod = (lodResult == TaskDialogResult.CommandLink2) ? 2 : 1;
}
else
{
    // 道路・橋：最大LODを自動選択（LOD選択ダイアログなし）
    targetLod = 0; // 0 = 自動検出（最大LODを使用）
}
```

### 8.3. LOD3対応の実装

**CityGMLParser.cs の変更**：

#### GetLodLevel() メソッドの拡張
```csharp
private static int GetLodLevel(XElement element)
{
    var current = element;
    while (current != null)
    {
        string localName = current.Name.LocalName;

        // LOD3をチェック（追加）
        if (localName.StartsWith("lod3", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        // LOD2をチェック
        else if (localName.StartsWith("lod2", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        // LOD1をチェック
        else if (localName.StartsWith("lod1", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        // LOD0をチェック
        else if (localName.StartsWith("lod0", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        current = current.Parent;
    }

    return 1; // デフォルト
}
```

#### GetMaxLodLevel() メソッドの拡張
```csharp
private static int GetMaxLodLevel(List<XElement> elements)
{
    int maxLod = 0;

    foreach (var elem in elements)
    {
        string localName = elem.Name.LocalName;

        if (localName.StartsWith("lod3", StringComparison.OrdinalIgnoreCase))
            maxLod = Math.Max(maxLod, 3);
        else if (localName.StartsWith("lod2", StringComparison.OrdinalIgnoreCase))
            maxLod = Math.Max(maxLod, 2);
        else if (localName.StartsWith("lod1", StringComparison.OrdinalIgnoreCase))
            maxLod = Math.Max(maxLod, 1);
    }

    return maxLod > 0 ? maxLod : 1;
}
```

#### DetermineTargetLod() メソッドの変更
```csharp
private static int DetermineTargetLod(int requestedLod, int maxAvailableLod)
{
    // requestedLod = 0 の場合、最大LODを自動選択（道路・橋用）
    if (requestedLod == 0)
        return maxAvailableLod;

    // 要求されたLODがファイルに存在しない場合、利用可能な最大LODを使用
    return Math.Min(requestedLod, maxAvailableLod);
}
```

### 8.4. Bridge（橋）の完全実装

**FindBridgeElements() メソッドの追加**：
```csharp
private static List<XElement> FindBridgeElements(XDocument doc)
{
    var elements = doc.Descendants()
        .Where(e => e.Name.LocalName == "Bridge" &&
                    (e.Name.Namespace.NamespaceName.Contains("bridge") ||
                     e.Name.Namespace.NamespaceName.Contains("brid")))
        .ToList();

    if (elements.Count == 0)
    {
        // namespaceなしでの検索（フォールバック）
        elements = doc.Descendants()
            .Where(e => e.Name.LocalName == "Bridge")
            .ToList();
    }

    return elements;
}
```

**ParseCityGML() メソッドの変更**：
```csharp
// Bridge要素の検索と処理を追加
bool includeBridges = objectType == CityObjectType.Bridge || objectType == CityObjectType.All;

var bridgeElements = includeBridges ? FindBridgeElements(doc) : new List<XElement>();

// 処理ループに追加
if (includeBridges)
{
    int bridgeIndex = 0;
    foreach (var bridgeElement in bridgeElements)
    {
        try
        {
            var bridge = CreateCityObject(
                bridgeElement,
                CityObjectType.Bridge,
                targetLod,
                bridgeIndex,
                elementLookup);
            if (bridge.Surfaces.Count > 0)
            {
                cityObjects.Add(bridge);
            }
        }
        catch (Exception ex)
        {
            // エラーハンドリング
        }
        finally
        {
            bridgeIndex++;
            processedCount++;
            ReportProgress(progressCallback, processedCount, totalTargets);
        }
    }
}
```

### 8.5. 完了メッセージの改善

**表示情報の追加**：
```csharp
string completionMessage = $"インポート完了\n\n" +
                          $"ファイル: {fileName}\n" +
                          $"サイズ: {fileSizeMB:F2} MB\n" +
                          $"地物タイプ: {GetTypeDisplayName(detectedType)}\n" +  // 追加
                          $"使用LOD: LOD{usedLod}\n" +                            // 追加
                          $"モード: {importType}\n" +
                          $"地物数: {cityObjects.Count}\n" +
                          $"生成: {shapes.Count} DirectShape\n" +
                          $"Zオフセット: {offset.OffsetZ:F2}m";
```

---

## 実装の優先順位と工数

### 実装内容と工数見積もり

| タスク | 内容 | 難易度 | 推定時間 |
|--------|------|--------|----------|
| **1. ファイルタイプ自動判別** | ImportCommand.csでの自動判別実装、選択ダイアログ削除 | ★★☆☆☆ | 2-3時間 |
| **2. LOD処理改善** | 建物は選択、道路・橋は自動 | ★★☆☆☆ | 2-3時間 |
| **3. LOD3対応** | GetLodLevel(), GetMaxLodLevel()の拡張 | ★★☆☆☆ | 2-3時間 |
| **4. Bridge実装** | FindBridgeElements()、処理ループ追加 | ★★★☆☆ | 3-4時間 |
| **5. UI改善** | 完了メッセージの改善、デバッグ出力調整 | ★☆☆☆☆ | 1-2時間 |
| **6. テスト** | 建物・道路・橋の各種ファイルでテスト | ★★★☆☆ | 3-4時間 |
| **7. ドキュメント更新** | 使用方法説明書、README更新 | ★★☆☆☆ | 2-3時間 |
| **合計** | - | - | **15-22時間** |

**建物との共通性**: 約80%のコードが再利用可能（座標変換、基本構造）

---

## 期待される効果

### ユーザー体験の向上
- ✅ **ワンクリックに近い操作**：ファイルを選ぶだけで適切な処理が自動実行
- ✅ **不要な選択を排除**：「建物か道路か」を選ばせる必要なし
- ✅ **直感的なUI**：専門知識不要

### 機能の拡張
- ✅ **道路の立体形状**：LOD3対応により起伏を正確に表現
- ✅ **橋のインポート対応**：完全実装
- ✅ **3種類の地物対応**：建物・道路・橋すべてサポート

### UI改善前後の比較

**Before（現状）**：
1. ファイル選択
2. 「建物」「道路」「建物+道路」を選択 ← **不要**
3. LOD選択（LOD1 / LOD2）
4. インポート

**After（改善後）**：
1. ファイル選択
2. 自動判別 → 建物なら → LOD選択（LOD1 / LOD2）
3. 自動判別 → 道路・橋なら → 最大LOD自動選択（ダイアログなし）
4. インポート

---

## 技術的課題とリスク

### Phase 8（道路・橋）
- **低リスク**: 建物との共通性が高く、実装は比較的容易
- **課題**:
  - LOD3の動作確認（現在未対応）
  - 道路の大量メッシュのパフォーマンス
- **対策**:
  - 既存のMultiSurface処理ロジックを流用
  - 進捗バーの適切な表示

### ファイルタイプ自動判別
- **極低リスク**: PLATEAUの命名規則が厳格で予測可能
- **対策**: フォールバックとしてXML内容からも判別

---

## まとめ

### 実装可能性
- ✅ **技術的に十分実現可能**
- ✅ 建物パーサーの80%を再利用
- ✅ 座標変換ロジックは100%共通
- ✅ PLATEAUの仕様が判明し、シンプルな実装が可能

### 総工数
- **Phase 8（道路・橋・UI改善）**: **15-22時間**（2-3営業日）

### 次のステップ
1. ✅ Phase 7（パフォーマンス最適化）完了
2. ✅ Phase 8の詳細調査完了
3. ⏸ Phase 8の実装開始待ち
4. 統合テストとドキュメント整備

---

**文書更新履歴**:
- 2025-11-07: 初版作成（Phase 8-9の詳細計画）
- 2025-11-07 (v2.0): 実際のGML構造調査結果を反映、UI改善計画追加、橋実装の詳細化
