# Phase 8-9: 道路・橋インポート機能 実装計画書

**文書バージョン**: 1.0
**作成日**: 2025年11月7日
**ステータス**: 計画段階（未実装）

---

## 概要

現在のPLATEAU Revitインポーターは建物（bldg:Building）のみに対応しています。本計画書は、道路（tran:Road）と橋（brid:Bridge）のインポート機能追加に向けた実装計画をまとめたものです。

---

## Phase 8: 道路インポート機能

### 8.1. 道路GMLの構造分析

**実際のデータ**: `53393652_tran_6697_op.gml` (16.4MB)

#### 基本構造
```xml
<core:CityModel xmlns:tran="http://www.opengis.net/citygml/transportation/2.0">
    <tran:Road gml:id="tran_xxx">
        <gml:name>国道357号</gml:name>
        <tran:class>1040</tran:class>
        <tran:function>2</tran:function>

        <!-- 車道エリア -->
        <tran:trafficArea>
            <tran:TrafficArea>
                <tran:lod2MultiSurface>
                    <gml:MultiSurface>
                        <gml:surfaceMember>
                            <gml:Polygon>
                                <gml:exterior>
                                    <gml:LinearRing>
                                        <gml:posList>緯度 経度 高さ ...</gml:posList>
                                    </gml:LinearRing>
                                </gml:exterior>
                            </gml:Polygon>
                        </gml:surfaceMember>
                    </gml:MultiSurface>
                </tran:lod2MultiSurface>
            </tran:TrafficArea>
        </tran:trafficArea>

        <!-- 補助エリア（歩道など） -->
        <tran:auxiliaryTrafficArea>
            <tran:AuxiliaryTrafficArea>
                <tran:lod2MultiSurface>...</tran:lod2MultiSurface>
            </tran:AuxiliaryTrafficArea>
        </tran:auxiliaryTrafficArea>

        <!-- 道路全体 -->
        <tran:lod1MultiSurface>...</tran:lod1MultiSurface>
        <tran:lod2MultiSurface>...</tran:lod2MultiSurface>
    </tran:Road>
</core:CityModel>
```

#### 建物との比較

| 項目 | 建物 (Building) | 道路 (Road) |
|------|----------------|-------------|
| **ルート要素** | `bldg:Building` | `tran:Road` |
| **名前空間** | building/2.0 | transportation/2.0 |
| **主なジオメトリ型** | Solid（立体） | MultiSurface（面） |
| **階層構造** | 2層（Building → boundedBy） | 2層（Road → TrafficArea） |
| **座標系** | EPSG:6697 | EPSG:6697（同じ） ✅ |
| **LODレベル** | LOD1, LOD2 | LOD1, LOD2, LOD3 |

### 8.2. 実装計画

#### 8.2.1. CityGMLParser.cs の拡張

**新規追加するクラス・列挙型**:
```csharp
// ジオメトリタイプの列挙
public enum CityObjectType
{
    Building,
    Road,
    Bridge
}

// 統合されたジオメトリクラス
public class CityObjectGeometry
{
    public CityObjectType Type { get; set; }
    public string ObjectId { get; set; }
    public List<List<XYZ>> Surfaces { get; set; }
    public string ClassName { get; set; }  // 道路分類コード等
}
```

**メソッドシグネチャの変更**:
```csharp
// 変更前
public static List<BuildingGeometry> ParseCityGML(
    string filePath,
    Action<int>? progressCallback = null,
    int targetLod = 2)

// 変更後
public static List<CityObjectGeometry> ParseCityGML(
    string filePath,
    CityObjectType objectType = CityObjectType.Building,
    Action<int>? progressCallback = null,
    int targetLod = 2)
```

**Road要素の抽出ロジック**:
```csharp
// Building要素の検索（既存）
if (objectType == CityObjectType.Building || objectType == CityObjectType.All)
{
    var buildingElements = doc.Descendants()
        .Where(e => e.Name.LocalName == "Building" &&
                    (e.Name.Namespace.NamespaceName.Contains("building") ||
                     e.Name.Namespace.NamespaceName.Contains("bldg")))
        .ToList();
    // 処理...
}

// Road要素の検索（新規）
if (objectType == CityObjectType.Road || objectType == CityObjectType.All)
{
    var roadElements = doc.Descendants()
        .Where(e => e.Name.LocalName == "Road" &&
                    e.Name.Namespace.NamespaceName.Contains("transportation"))
        .ToList();

    foreach (var roadElement in roadElements)
    {
        // TrafficAreaとAuxiliaryTrafficAreaを処理
        var trafficAreas = roadElement.Descendants()
            .Where(e => e.Name.LocalName == "TrafficArea" ||
                        e.Name.LocalName == "AuxiliaryTrafficArea");

        foreach (var area in trafficAreas)
        {
            // lod2MultiSurfaceからposListを抽出
            // 既存のParsePosList()を再利用
        }
    }
}
```

**再利用可能な既存コード**:
- ✅ `ParsePosList()` - 100%再利用
- ✅ `ConvertLatLonToMeters()` - 100%再利用（座標系が同じ）
- ✅ `HubenyDistanceCalculator` - 100%再利用
- ✅ `GetLodLevel()` - 90%再利用（要素名を調整）

#### 8.2.2. ImportCommand.cs の変更

**対象選択ダイアログの追加**:
```csharp
// ファイル選択後、LOD選択の前に挿入
TaskDialog typeDialog = new TaskDialog("インポート対象選択");
typeDialog.MainInstruction = "インポートする地物タイプを選択してください";
typeDialog.MainContent = "建物、道路、またはすべてを選択できます";
typeDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "建物 (Building)");
typeDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "道路 (Road)");
typeDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "すべて (Building + Road)");

TaskDialogResult typeResult = typeDialog.Show();

CityObjectType targetType = typeResult switch
{
    TaskDialogResult.CommandLink1 => CityObjectType.Building,
    TaskDialogResult.CommandLink2 => CityObjectType.Road,
    TaskDialogResult.CommandLink3 => CityObjectType.All,
    _ => throw new OperationCanceledException()
};
```

#### 8.2.3. GeometryBuilder.cs の調整

**変更内容**:
```csharp
// DirectShapeの命名規則を調整
directShape.Name = cityObject.Type switch
{
    CityObjectType.Building => $"PLATEAU_Building_{sanitizedId}",
    CityObjectType.Road => $"PLATEAU_Road_{sanitizedId}",
    _ => $"PLATEAU_{sanitizedId}"
};

// サブカテゴリの追加
Category roadCategory = GetOrCreateSubCategory(doc, "PLATEAU_Road");
```

**既存のメッシュ生成ロジックを再利用**:
- 道路は「面」として処理されるため、既存の`CreateMeshFromSurface()`がそのまま使える
- Target: Mesh、Fallback: Salvage の設定も流用

#### 8.2.4. CoordinateConverter.cs

**変更不要**: 道路も建物と同じEPSG:6697座標系を使用しているため、既存の座標変換ロジックをそのまま使用可能。

### 8.3. 実装工数見積もり

| タスク | 難易度 | 推定時間 |
|--------|--------|----------|
| CityGMLParser.cs 拡張 | ★★★☆☆ | 4-6時間 |
| ImportCommand.cs UI変更 | ★★☆☆☆ | 1-2時間 |
| GeometryBuilder.cs 調整 | ★★☆☆☆ | 2-3時間 |
| テスト・デバッグ | ★★★☆☆ | 4-6時間 |
| ドキュメント更新 | ★☆☆☆☆ | 1-2時間 |
| **合計** | - | **12-19時間** |

**建物との共通性**: 約70%のコードが再利用可能

---

## Phase 9: 橋インポート機能

### 9.1. 橋GMLの構造分析

**実際のデータ**: `53393652_brid_6697_op.gml` (22.7MB, 392,328行)

#### 基本構造
```xml
<core:CityModel xmlns:brid="http://www.opengis.net/citygml/bridge/2.0">
    <brid:Bridge gml:id="brid_xxx">
        <brid:class>99</brid:class>
        <brid:function>01</brid:function>

        <!-- 橋本体: Solidで表現 -->
        <brid:lod2Solid>
            <gml:Solid>
                <gml:exterior>
                    <gml:CompositeSurface>
                        <!-- surfaceMemberはxlink:hrefで参照 -->
                        <gml:surfaceMember xlink:href="#poly-3804f63b-..."/>
                    </gml:CompositeSurface>
                </gml:exterior>
            </gml:Solid>
        </brid:lod2Solid>

        <!-- 橋構成要素（橋脚など） -->
        <brid:outerBridgeConstruction>
            <brid:BridgeConstructionElement gml:id="brid_xxx_cons">
                <brid:lod2Geometry>
                    <gml:MultiSurface>
                        <gml:surfaceMember>
                            <gml:Polygon gml:id="poly-xxx">
                                <gml:exterior>
                                    <gml:LinearRing>
                                        <gml:posList>緯度 経度 高さ ...</gml:posList>
                                    </gml:LinearRing>
                                </gml:exterior>
                            </gml:Polygon>
                        </gml:surfaceMember>
                    </gml:MultiSurface>
                </brid:lod2Geometry>
            </brid:BridgeConstructionElement>
        </brid:outerBridgeConstruction>

        <!-- 境界面（意味的分類） -->
        <brid:boundedBy>
            <brid:OuterCeilingSurface gml:id="face_xxx">
                <brid:lod2MultiSurface>
                    <gml:MultiSurface>
                        <gml:surfaceMember>
                            <gml:Polygon>...</gml:Polygon>
                        </gml:surfaceMember>
                    </gml:MultiSurface>
                </brid:lod2MultiSurface>
            </brid:OuterCeilingSurface>
        </brid:boundedBy>
        <!-- OuterFloorSurface, WallSurface, RoofSurface も同様 -->
    </brid:Bridge>
</core:CityModel>
```

#### 建物・道路との比較

| 項目 | 建物 | 道路 | 橋 |
|------|------|------|---|
| **階層の深さ** | 2層 | 2層 | **3層**（最も複雑） |
| **ジオメトリ型** | Solid中心 | MultiSurface | **Solid + MultiSurface混在** |
| **構成要素** | BuildingPart | TrafficArea | **BridgeConstructionElement** |
| **境界面** | 2-3種類 | なし | **5種類**（天井・床・壁・屋根・構造） |
| **XLink参照** | なし | なし | **あり** |
| **データ量** | 中 | 中 | **大**（55,772ポリゴン） |

### 9.2. 橋特有の複雑な点

#### 9.2.1. 3層階層構造
```
Bridge本体
├─ lod2Solid（Solidジオメトリ）
├─ BridgeConstructionElement（橋脚・橋桁）
│   └─ lod2Geometry（MultiSurface）
└─ boundedBy（境界面）
    ├─ OuterCeilingSurface（天井）
    ├─ OuterFloorSurface（床）
    ├─ WallSurface（壁）
    └─ RoofSurface（屋根）
```

#### 9.2.2. XLink参照の解決
```xml
<gml:surfaceMember xlink:href="#poly-3804f63b-3dd3-4b40-b63e-5c661c1611ea"/>
```
- `xlink:href`で別の場所のポリゴンを参照している
- 参照を解決して実際のポリゴンデータを取得する必要がある
- Dictionary等でIDとポリゴンのマッピングを保持

#### 9.2.3. 境界面の意味的分類
```csharp
public enum BridgeSurfaceType
{
    Main,                    // 橋本体
    OuterCeiling,           // 天井面
    OuterFloor,             // 床面
    Wall,                   // 壁面
    Roof,                   // 屋根面
    ConstructionElement     // 構造部材
}
```
- Revit上で色分け表示するためにメタデータを保持

### 9.3. 実装計画（段階的アプローチ）

#### Phase 9-1: 基本パーサー（建物パーサーをベースに）
**目標**: 橋本体のSolidジオメトリを抽出（単純版）
- `brid:Bridge`要素の検索
- `lod2Solid`の処理（CompositeSurfaceは無視）
- 既存のPosList解析・座標変換ロジックを再利用

**推定時間**: 6-8時間

#### Phase 9-2: BridgeConstructionElement対応
**目標**: 橋脚・橋桁の構造部材を抽出
- `brid:outerBridgeConstruction`の検索
- `lod2Geometry`のMultiSurface処理
- 構成要素を別のジオメトリとして保存

**推定時間**: 4-6時間

#### Phase 9-3: boundedBy境界面対応
**目標**: 意味的分類を保持した面の抽出
- OuterCeiling, OuterFloor, Wall, Roof の各要素を検索
- 境界面タイプを保持したまま面を抽出
- サブカテゴリで分類表示

**推定時間**: 4-6時間

#### Phase 9-4: XLink参照解決（オプション）
**目標**: CompositeSurfaceの参照を解決
- `xlink:href="#poly-xxx"`の参照を解決
- Dictionaryでポリゴンをキャッシュ
- パフォーマンス最適化

**推定時間**: 6-8時間

#### Phase 9-5: テスト・最適化
**目標**: 大容量データ（55,772ポリゴン）の処理確認
- 進捗バーの動作確認
- メモリ使用量の最適化
- エラーハンドリングの強化

**推定時間**: 4-6時間

### 9.4. 実装工数見積もり

| フェーズ | 内容 | 推定時間 |
|---------|------|----------|
| Phase 9-1 | 基本パーサー | 6-8時間 |
| Phase 9-2 | BridgeConstructionElement | 4-6時間 |
| Phase 9-3 | boundedBy境界面 | 4-6時間 |
| Phase 9-4 | XLink参照解決（オプション） | 6-8時間 |
| Phase 9-5 | テスト・最適化 | 4-6時間 |
| **合計** | | **24-34時間** |

**難易度**: 建物の**1.4倍**
**建物との共通性**: 約70%のコードが再利用可能（座標変換、基本構造）

---

## 実装の優先順位

### 推奨順序
1. **Phase 8: 道路インポート**（12-19時間）
   - 理由: 建物と構造が類似しており、実装が比較的容易
   - 構造: 2層階層、MultiSurface中心
   - ユーザー需要: 建物と道路は都市モデルの基本要素

2. **Phase 9: 橋インポート**（24-34時間）
   - 理由: 道路実装で得た知見を活用できる
   - 構造: 3層階層、Solid + MultiSurface混在
   - 段階的実装: Phase 9-1 → 9-2 → 9-3 → (9-4オプション)

### 技術的依存関係
- Phase 8とPhase 9は独立しており、並行開発も可能
- Phase 9はPhase 8の実装パターンを参考にできる
- 両方とも既存の座標変換ロジックを100%再利用

---

## データ統計

### 道路GML（53393652_tran_6697_op.gml）
- ファイルサイズ: 16.4 MB
- LODレベル: LOD1, LOD2, LOD3
- 主要要素: TrafficArea, AuxiliaryTrafficArea

### 橋GML（53393652_brid_6697_op.gml）
- ファイルサイズ: 22.7 MB (392,328行)
- 橋要素: 42個
- ポリゴン数: 55,772個
- LODレベル: LOD2のみ
- 構成要素: 44個のBridgeConstructionElement
- 境界面: 362個（OuterCeiling, OuterFloor, Wall, Roof）

---

## 技術的課題とリスク

### Phase 8（道路）
- **低リスク**: 建物との共通性が高く、実装は比較的容易
- **課題**: TrafficAreaとAuxiliaryTrafficAreaの処理
- **対策**: 既存のMultiSurface処理ロジックを流用

### Phase 9（橋）
- **中リスク**: XLink参照解決が新規技術
- **課題**:
  - 3層階層の処理
  - CompositeSurfaceからの面の抽出
  - 大量ポリゴン（55,772個）のパフォーマンス
- **対策**:
  - 段階的実装（Phase 9-1 → 9-4）
  - XLink解決をオプション化（Phase 9-4）
  - Dictionary等でキャッシュ

---

## まとめ

### 実装可能性
- ✅ **技術的に十分実現可能**
- ✅ 建物パーサーの70%を再利用
- ✅ 座標変換ロジックは100%共通

### 総工数
- **Phase 8（道路）**: 12-19時間（2-3営業日）
- **Phase 9（橋）**: 24-34時間（3-4営業日）
- **合計**: 36-53時間（5-7営業日）

### 次のステップ
1. Phase 7（パフォーマンス最適化）のコミット完了
2. Phase 8（道路）の実装開始
3. Phase 9（橋）の段階的実装
4. 統合テストとドキュメント整備

---

**文書更新履歴**:
- 2025-11-07: 初版作成（Phase 8-9の詳細計画）
