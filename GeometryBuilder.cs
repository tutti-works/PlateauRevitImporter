using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlateauRevitImporter
{
    /// <summary>
    /// CityGMLデータからRevit DirectShapeを生成するクラス
    /// </summary>
    public class GeometryBuilder
    {
        // エラーログ収集用
        public static List<string> ErrorLog { get; private set; } = new List<string>();

        /// <summary>
        /// 建物データからDirectShapeを生成してRevitドキュメントに追加
        /// </summary>
        public static List<DirectShape> CreateDirectShapes(
            Document doc,
            List<CityGMLParser.BuildingGeometry> buildings,
            CoordinateConverter.CoordinateOffset offset,
            ElementId categoryId,
            IProgress<int>? progress = null)
        {
            ErrorLog.Clear();
            List<DirectShape> createdShapes = new List<DirectShape>();
            int processedCount = 0;

            foreach (var building in buildings)
            {
                try
                {
                    // DirectShapeを作成
                    DirectShape directShape = DirectShape.CreateElement(doc, categoryId);

                    // Building IDをサニタイズ（Revitの名前制限に対応）
                    string sanitizedId = SanitizeBuildingId(building.BuildingId);
                    directShape.Name = $"PLATEAU_{sanitizedId}";

                    // ジオメトリを構築（建物全体を1つのビルダーで処理）
                    List<GeometryObject> geometries = CreateBuildingGeometry(building, offset);
                    int surfaceCount = geometries.Count;
                    int failedSurfaceCount = building.Surfaces.Count - surfaceCount;

                    // DirectShapeにジオメトリを設定
                    if (geometries.Count > 0)
                    {
                        directShape.SetShape(geometries);

                        // サブカテゴリを設定（PLATEAUモデルとして識別）
                        SetPlateauSubcategory(doc, directShape);

                        // オフセット情報を保存
                        CoordinateConverter.SaveOffsetToElement(directShape, offset);

                        createdShapes.Add(directShape);
                    }
                    else
                    {
                        // ジオメトリが1つも生成できなかった場合は削除
                        doc.Delete(directShape.Id);
                        ErrorLog.Add($"建物 {sanitizedId}: すべての面の生成に失敗");
                    }

                    if (failedSurfaceCount > 0)
                    {
                        ErrorLog.Add($"建物 {sanitizedId}: {failedSurfaceCount}/{building.Surfaces.Count} 面の生成に失敗");
                    }
                }
                catch (Exception ex)
                {
                    // 個別の建物のエラーをログに記録
                    ErrorLog.Add($"建物 {building.BuildingId}: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"建物 {building.BuildingId} の生成に失敗: {ex.Message}");
                }

                // プログレス報告
                processedCount++;
                progress?.Report((int)((double)processedCount / buildings.Count * 100));
            }

            return createdShapes;
        }

        /// <summary>
        /// DirectShapeにPLATEAUサブカテゴリを設定
        /// </summary>
        private static void SetPlateauSubcategory(Document doc, DirectShape directShape)
        {
            try
            {
                // 汎用モデルカテゴリを取得
                Category genericModelCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel);

                // PLATEAUサブカテゴリを取得
                Category? plateauSubCategory = null;
                foreach (Category subCat in genericModelCategory.SubCategories)
                {
                    if (subCat.Name == CoordinateConverter.PlateauCategoryName)
                    {
                        plateauSubCategory = subCat;
                        break;
                    }
                }

                // サブカテゴリをパラメータで設定
                if (plateauSubCategory != null)
                {
                    Parameter? param = directShape.get_Parameter(BuiltInParameter.FAMILY_ELEM_SUBCATEGORY);
                    if (param != null && !param.IsReadOnly)
                    {
                        param.Set(plateauSubCategory.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"サブカテゴリ設定エラー: {ex.Message}");
                // サブカテゴリ設定に失敗しても続行
            }
        }

        /// <summary>
        /// Building IDをRevitの名前規則に適合するようサニタイズ
        /// </summary>
        private static string SanitizeBuildingId(string buildingId)
        {
            // 不正な文字を除去
            string sanitized = new string(buildingId
                .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')
                .ToArray());

            // 長すぎる場合は短縮
            if (sanitized.Length > 100)
            {
                sanitized = sanitized.Substring(0, 100);
            }

            // 空の場合はGUIDを使用
            if (string.IsNullOrEmpty(sanitized))
            {
                sanitized = Guid.NewGuid().ToString().Substring(0, 8);
            }

            return sanitized;
        }

        /// <summary>
        /// 建物全体のジオメトリを作成（各面を個別のMeshとして処理）
        /// </summary>
        private static List<GeometryObject> CreateBuildingGeometry(
            CityGMLParser.BuildingGeometry building,
            CoordinateConverter.CoordinateOffset offset)
        {
            List<GeometryObject> geometries = new List<GeometryObject>();
            int successfulSurfaces = 0;
            int failedSurfaces = 0;

            // 各面を個別のメッシュとして処理
            foreach (var surface in building.Surfaces)
            {
                try
                {
                    // 座標変換を適用
                    List<XYZ> convertedPoints = surface
                        .Select(p => CoordinateConverter.ApplyOffset(p, offset))
                        .ToList();

                    // ジオメトリを作成
                    GeometryObject? geom = CreateMeshFromPoints(convertedPoints);
                    if (geom != null)
                    {
                        geometries.Add(geom);
                        successfulSurfaces++;
                    }
                    else
                    {
                        failedSurfaces++;
                    }
                }
                catch (Exception ex)
                {
                    failedSurfaces++;
                    System.Diagnostics.Debug.WriteLine($"面の処理失敗: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"建物 {building.BuildingId}: 成功={successfulSurfaces}, 失敗={failedSurfaces}");

            return geometries;
        }

        /// <summary>
        /// 点リストから直接ジオメトリを作成
        /// </summary>
        private static GeometryObject? CreateMeshFromPoints(List<XYZ> points)
        {
            try
            {
                // 最低3点必要
                if (points.Count < 3)
                    return null;

                // 重複する最後の点を除去
                List<XYZ> cleanedPoints = new List<XYZ>(points);
                if (cleanedPoints.Count > 3 &&
                    cleanedPoints[0].IsAlmostEqualTo(cleanedPoints[cleanedPoints.Count - 1]))
                {
                    cleanedPoints.RemoveAt(cleanedPoints.Count - 1);
                }

                // 頂点溶接（Vertex Welding）- Revitの公差より大きい値を使用
                // 推奨値: 2.5mm (約 0.0082ft) - Revitの約2mm公差より大きい
                List<XYZ> uniquePoints = new List<XYZ>();
                const double WELD_TOLERANCE = 0.0082; // 2.5mm in feet

                foreach (var point in cleanedPoints)
                {
                    bool isDuplicate = false;
                    foreach (var existing in uniquePoints)
                    {
                        if (point.DistanceTo(existing) < WELD_TOLERANCE)
                        {
                            isDuplicate = true;
                            break;
                        }
                    }
                    if (!isDuplicate)
                    {
                        uniquePoints.Add(point);
                    }
                }

                if (uniquePoints.Count < 3)
                    return null;

                // TessellatedShapeBuilder with Mesh target (寛容) + Salvage fallback
                TessellatedShapeBuilder builder = new TessellatedShapeBuilder();
                builder.Target = TessellatedShapeBuilderTarget.Mesh;
                builder.Fallback = TessellatedShapeBuilderFallback.Salvage;

                builder.OpenConnectedFaceSet(false);

                // Blender版と同様に、多角形をそのまま1つの面として追加（三角形分割なし）
                try
                {
                    TessellatedFace face = new TessellatedFace(uniquePoints, ElementId.InvalidElementId);
                    builder.AddFace(face);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"多角形面の追加失敗: {ex.Message}");
                    return null;
                }

                try
                {
                    builder.CloseConnectedFaceSet();
                    builder.Build();

                    TessellatedShapeBuilderResult result = builder.GetBuildResult();
                    var geoms = result.GetGeometricalObjects();

                    if (geoms.Count > 0)
                    {
                        return geoms[0];
                    }
                }
                catch
                {
                    // Build失敗
                    return null;
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Mesh作成失敗: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 面の頂点リストからMeshを作成（Solidより堅牢）
        /// </summary>
        private static GeometryObject? CreateMeshFromSurface(List<XYZ> points)
        {
            try
            {
                // 最低3点必要
                if (points.Count < 3)
                {
                    System.Diagnostics.Debug.WriteLine($"面の点数不足: {points.Count}点");
                    return null;
                }

                // 重複する最後の点を除去（閉じたポリゴンの場合）
                List<XYZ> cleanedPoints = new List<XYZ>(points);
                if (cleanedPoints.Count > 3 && cleanedPoints[0].IsAlmostEqualTo(cleanedPoints[cleanedPoints.Count - 1]))
                {
                    cleanedPoints.RemoveAt(cleanedPoints.Count - 1);
                }

                // 重複点を除去（より厳密な距離チェック）
                List<XYZ> uniquePoints = new List<XYZ>();
                const double minDistance = 0.01; // フィート単位で約3mm

                foreach (var point in cleanedPoints)
                {
                    bool isDuplicate = false;
                    foreach (var existing in uniquePoints)
                    {
                        if (point.DistanceTo(existing) < minDistance)
                        {
                            isDuplicate = true;
                            break;
                        }
                    }
                    if (!isDuplicate)
                    {
                        uniquePoints.Add(point);
                    }
                }

                if (uniquePoints.Count < 3)
                {
                    System.Diagnostics.Debug.WriteLine($"重複除去後の点数不足: {uniquePoints.Count}点");
                    return null;
                }

                // 三角形に分割
                List<List<XYZ>> triangles = TriangulateFace(uniquePoints);

                // 有効な三角形を収集
                List<List<XYZ>> validTriangles = new List<List<XYZ>>();
                foreach (var triangle in triangles)
                {
                    if (triangle.Count == 3)
                    {
                        // 三角形の面積チェック（より緩い閾値）
                        double area = CalculateTriangleArea(triangle[0], triangle[1], triangle[2]);
                        // 最小面積を大幅に緩和: 0.000001平方フィート（約0.0001平方ミリ）
                        if (area > 0.000001)
                        {
                            validTriangles.Add(triangle);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"三角形の面積が小さすぎる: {area:F9} 平方フィート");
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"三角形分割: {triangles.Count}個 → 有効: {validTriangles.Count}個");

                if (validTriangles.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("有効な三角形が0個");
                    return null;
                }

                // TessellatedShapeBuilderを使用してSolidを作成（Meshより安定）
                TessellatedShapeBuilder builder = new TessellatedShapeBuilder();
                builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
                builder.Fallback = TessellatedShapeBuilderFallback.Salvage;

                builder.OpenConnectedFaceSet(false);

                // 有効な三角形を追加（成功数をカウント）
                int addedFaceCount = 0;
                foreach (var triangle in validTriangles)
                {
                    try
                    {
                        TessellatedFace face = new TessellatedFace(triangle, ElementId.InvalidElementId);
                        builder.AddFace(face);
                        addedFaceCount++;
                    }
                    catch (Exception addEx)
                    {
                        // 個別の三角形追加失敗の詳細をログ
                        System.Diagnostics.Debug.WriteLine($"三角形追加失敗: {addEx.GetType().Name} - {addEx.Message}");
                        continue;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"Face追加: {addedFaceCount}/{validTriangles.Count}個成功");

                // 1つも追加できなかった場合はnullを返す
                if (addedFaceCount == 0)
                {
                    System.Diagnostics.Debug.WriteLine("追加された面が0個のためスキップ");
                    return null;
                }

                builder.CloseConnectedFaceSet();

                System.Diagnostics.Debug.WriteLine("Build()を呼び出し中...");
                builder.Build();

                System.Diagnostics.Debug.WriteLine("GetBuildResult()を呼び出し中...");
                TessellatedShapeBuilderResult result = builder.GetBuildResult();

                System.Diagnostics.Debug.WriteLine($"Build結果のOutcome: {result.Outcome}");

                // AnyGeometryターゲットの場合、SolidまたはMeshが生成される
                try
                {
                    var geometries = result.GetGeometricalObjects();
                    System.Diagnostics.Debug.WriteLine($"ジオメトリ数: {geometries.Count}");

                    if (geometries.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine("ジオメトリ生成成功");
                        return geometries[0];
                    }
                }
                catch (Exception getEx)
                {
                    System.Diagnostics.Debug.WriteLine($"GetGeometricalObjects()エラー: {getEx.Message}");
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Mesh作成エラー: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"  メッセージ: '{ex.Message}'");
                System.Diagnostics.Debug.WriteLine($"  スタックトレース: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 三角形の面積を計算
        /// </summary>
        private static double CalculateTriangleArea(XYZ p1, XYZ p2, XYZ p3)
        {
            XYZ v1 = p2 - p1;
            XYZ v2 = p3 - p1;
            XYZ cross = v1.CrossProduct(v2);
            return cross.GetLength() / 2.0;
        }

        /// <summary>
        /// 面を三角形に分割（単純なファン三角分割）
        /// </summary>
        private static List<List<XYZ>> TriangulateFace(List<XYZ> points)
        {
            List<List<XYZ>> triangles = new List<List<XYZ>>();

            if (points.Count < 3)
                return triangles;

            // ファン三角分割: 最初の点を中心に扇形に三角形を作成
            for (int i = 1; i < points.Count - 1; i++)
            {
                triangles.Add(new List<XYZ> { points[0], points[i], points[i + 1] });
            }

            return triangles;
        }

        /// <summary>
        /// 面の頂点リストからSolidジオメトリを作成（バックアップ用）
        /// </summary>
        private static GeometryObject? CreateSolidFromSurface(List<XYZ> points)
        {
            try
            {
                // 最低3点必要
                if (points.Count < 3)
                    return null;

                // 重複する最後の点を除去（閉じたポリゴンの場合）
                if (points.Count > 3 && points[0].IsAlmostEqualTo(points[points.Count - 1]))
                {
                    points = points.Take(points.Count - 1).ToList();
                }

                // CurveLoopを作成
                CurveLoop curveLoop = new CurveLoop();

                for (int i = 0; i < points.Count; i++)
                {
                    XYZ start = points[i];
                    XYZ end = points[(i + 1) % points.Count];

                    // 同じ点をスキップ
                    if (start.IsAlmostEqualTo(end))
                        continue;

                    try
                    {
                        Line line = Line.CreateBound(start, end);
                        curveLoop.Append(line);
                    }
                    catch
                    {
                        // 無効な線はスキップ
                        continue;
                    }
                }

                // CurveLoopが有効かチェック
                if (curveLoop.NumberOfCurves() < 3)
                    return null;

                // 法線ベクトルを計算
                XYZ normal = CalculateNormal(points);

                // 押し出し高さ（非常に小さい値 = 面として表現）
                double extrusionHeight = 0.01; // フィート単位

                // Solidを作成（面を少し押し出して立体化）
                Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { curveLoop },
                    normal,
                    extrusionHeight
                );

                return solid;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Solid作成エラー: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 面の法線ベクトルを計算（Newell's method）
        /// </summary>
        private static XYZ CalculateNormal(List<XYZ> points)
        {
            if (points.Count < 3)
                return XYZ.BasisZ;

            double nx = 0, ny = 0, nz = 0;

            for (int i = 0; i < points.Count; i++)
            {
                XYZ current = points[i];
                XYZ next = points[(i + 1) % points.Count];

                nx += (current.Y - next.Y) * (current.Z + next.Z);
                ny += (current.Z - next.Z) * (current.X + next.X);
                nz += (current.X - next.X) * (current.Y + next.Y);
            }

            XYZ normal = new XYZ(nx, ny, nz);

            // 正規化
            if (normal.GetLength() > 0.0001)
            {
                return normal.Normalize();
            }

            // デフォルトはZ軸方向
            return XYZ.BasisZ;
        }
    }
}
