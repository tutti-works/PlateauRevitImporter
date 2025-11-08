using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace PlateauRevitImporter
{
    /// <summary>
    /// CityGML 2.0ファイルを解析して建物データを抽出するクラス
    /// </summary>
    public class CityGMLParser
    {
        /// <summary>
        /// CityGML地物タイプ
        /// </summary>
        public enum CityObjectType
        {
            Building,
            Road,
            Bridge,
            All
        }

        /// <summary>
        /// 汎用ジオメトリコンテナ
        /// </summary>
        public class CityObjectGeometry
        {
            public CityObjectType Type { get; set; } = CityObjectType.Building;
            public string ObjectId { get; set; } = string.Empty;
            public List<List<XYZ>> Surfaces { get; set; } = new List<List<XYZ>>();
            public string ClassName { get; set; } = string.Empty;
        }

        /// <summary>
        /// 3D座標点
        /// </summary>
        public class XYZ
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }

            public XYZ(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        // CityGML名前空間
        private static readonly XNamespace gmlNs = "http://www.opengis.net/gml";
        private static readonly XNamespace coreNs = "http://www.opengis.net/citygml/2.0";
        private static readonly XNamespace bldgNs = "http://www.opengis.net/citygml/building/2.0";
        private static readonly XNamespace xlinkNs = "http://www.w3.org/1999/xlink";

                /// <summary>
        /// CityGMLファイルから指定タイプの地物ジオメトリを解析
        /// </summary>
        /// <param name="filePath">CityGMLファイルのパス</param>
        /// <param name="objectType">解析対象の地物タイプ</param>
        /// <param name="progressCallback">進捗報告用コールバック（0-95%）</param>
        /// <param name="targetLod">インポートするLODレベル（1=簡易, 2=詳細）</param>
        /// <returns>地物ジオメトリのリスト</returns>
        public static List<CityObjectGeometry> ParseCityGML(
            string filePath,
            CityObjectType objectType = CityObjectType.Building,
            Action<int>? progressCallback = null,
            int targetLod = 2)
        {
            List<CityObjectGeometry> cityObjects = new List<CityObjectGeometry>();

            try
            {
                // XMLファイルを読み込み（大容量ファイル対応）
                XDocument doc;
                using (var stream = System.IO.File.OpenRead(filePath))
                {
                    doc = XDocument.Load(stream, LoadOptions.None);
                }

                // ルート要素の存在確認
                var root = doc.Root;
                if (root == null)
                    throw new Exception("XMLファイルのルート要素が見つかりません");

                var elementLookup = BuildElementLookup(doc);

                bool includeBuildings = objectType == CityObjectType.Building || objectType == CityObjectType.All;
                bool includeRoads = objectType == CityObjectType.Road || objectType == CityObjectType.All;

                var buildingElements = includeBuildings ? FindBuildingElements(doc) : new List<XElement>();
                var roadElements = includeRoads ? FindRoadElements(doc) : new List<XElement>();

                int totalTargets = buildingElements.Count + roadElements.Count;

                if (totalTargets == 0)
                {
                    throw new Exception($"{GetTypeDisplayName(objectType)}のCityGML要素が見つかりませんでした。");
                }

                int processedCount = 0;

                if (includeBuildings)
                {
                    int buildingIndex = 0;
                    foreach (var buildingElement in buildingElements)
                    {
                        try
                        {
                            var building = CreateCityObject(
                                buildingElement,
                                CityObjectType.Building,
                                targetLod,
                                buildingIndex,
                                elementLookup);
                            if (building.Surfaces.Count > 0)
                            {
                                cityObjects.Add(building);
                            }
                        }
                        catch (Exception ex)
                        {
#if DEBUG
                            System.Diagnostics.Debug.WriteLine($"建物の解析エラー: {ex.Message}");
#endif
                        }
                        finally
                        {
                            buildingIndex++;
                            processedCount++;
                            ReportProgress(progressCallback, processedCount, totalTargets);
                        }
                    }
                }

                if (includeRoads)
                {
                    int roadIndex = 0;
                    foreach (var roadElement in roadElements)
                    {
                        try
                        {
                            var road = CreateCityObject(
                                roadElement,
                                CityObjectType.Road,
                                targetLod,
                                roadIndex,
                                elementLookup);
                            if (road.Surfaces.Count > 0)
                            {
                                cityObjects.Add(road);
                            }
                        }
                        catch (Exception ex)
                        {
#if DEBUG
                            System.Diagnostics.Debug.WriteLine($"道路の解析エラー: {ex.Message}");
#endif
                        }
                        finally
                        {
                            roadIndex++;
                            processedCount++;
                            ReportProgress(progressCallback, processedCount, totalTargets);
                        }
                    }
                }

                if (cityObjects.Count == 0)
                {
                    throw new Exception(
                        $"有効な{GetTypeDisplayName(objectType)}ジオメトリが見つかりませんでした。\n" +
                        "LOD設定やCityGMLファイル内容をご確認ください。"
                    );
                }

                return cityObjects;
            }
            catch (System.Xml.XmlException xmlEx)
            {
                throw new System.Xml.XmlException($"XMLファイルの解析エラー: {xmlEx.Message}", xmlEx);
            }
            catch (Exception ex)
            {
                throw new Exception($"CityGMLファイルの解析に失敗しました: {ex.Message}", ex);
            }
        }

        private static List<XElement> FindBuildingElements(XDocument doc)
        {
            var elements = doc.Descendants()
                .Where(e => e.Name.LocalName == "Building" &&
                            (e.Name.Namespace.NamespaceName.Contains("building") ||
                             e.Name.Namespace.NamespaceName.Contains("bldg")))
                .ToList();

            if (elements.Count == 0)
            {
                elements = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Building")
                    .ToList();
            }

            return elements;
        }

        private static List<XElement> FindRoadElements(XDocument doc)
        {
            var elements = doc.Descendants()
                .Where(e => e.Name.LocalName == "Road" &&
                            (e.Name.Namespace.NamespaceName.Contains("transportation") ||
                                e.Name.Namespace.NamespaceName.Contains("tran")))
                .ToList();

            if (elements.Count == 0)
            {
                elements = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Road")
                    .ToList();
            }

            return elements;
        }

        private static Dictionary<string, XElement> BuildElementLookup(XDocument doc)
        {
            Dictionary<string, XElement> lookup = new Dictionary<string, XElement>();

            foreach (var element in doc.Descendants())
            {
                var idAttr = element.Attribute(gmlNs + "id") ??
                             element.Attribute("id") ??
                             element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id");

                if (idAttr == null)
                    continue;

                string idValue = idAttr.Value;
                if (string.IsNullOrWhiteSpace(idValue))
                    continue;

                if (!lookup.ContainsKey(idValue))
                {
                    lookup[idValue] = element;
                }
            }

            return lookup;
        }

        private static CityObjectGeometry CreateCityObject(
            XElement element,
            CityObjectType type,
            int targetLod,
            int fallbackIndex,
            Dictionary<string, XElement> elementLookup)
        {
            CityObjectGeometry geometry = new CityObjectGeometry
            {
                Type = type,
                ObjectId = ResolveObjectId(element, type, fallbackIndex),
                ClassName = ExtractClassName(element)
            };

            var allDescendants = element.Descendants().ToList();
            int maxLod = GetMaxLodLevel(allDescendants);
            int useLod = DetermineTargetLod(targetLod, maxLod);

            var posLists = allDescendants
                .Where(IsPosListElement)
                .Where(e => !IsLOD0Element(e) && GetLodLevel(e) == useLod);

            foreach (var posList in posLists)
            {
                try
                {
                    List<XYZ> surface = ParsePosList(posList.Value);
                    if (surface.Count >= 3)
                    {
                        geometry.Surfaces.Add(surface);
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"面の解析エラー ({geometry.ObjectId}): {ex.Message}");
#endif
                }
            }

            AddReferencedSurfaces(geometry, element, useLod, elementLookup);

            return geometry;
        }

        private static string ResolveObjectId(XElement element, CityObjectType type, int fallbackIndex)
        {
            var idAttr = element.Attribute(gmlNs + "id") ??
                         element.Attribute("id") ??
                         element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id");

            if (idAttr != null && !string.IsNullOrWhiteSpace(idAttr.Value))
            {
                return idAttr.Value;
            }

            string prefix = type switch
            {
                CityObjectType.Road => "Road",
                CityObjectType.Bridge => "Bridge",
                CityObjectType.All => "CityObject",
                _ => "Building"
            };

            return $"{prefix}_{fallbackIndex}";
        }

        private static string ExtractClassName(XElement element)
        {
            var classElement = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase));

            return classElement?.Value?.Trim() ?? string.Empty;
        }

        private static bool IsPosListElement(XElement element)
        {
            string localName = element.Name.LocalName;
            return localName == "posList" || localName == "coordinates";
        }

        private static void AddReferencedSurfaces(
            CityObjectGeometry geometry,
            XElement sourceElement,
            int useLod,
            Dictionary<string, XElement> elementLookup)
        {
            var referenceElements = sourceElement
                .Descendants()
                .Where(e => e.Attribute(xlinkNs + "href") != null)
                .ToList();

            HashSet<string> processedReferences = new HashSet<string>();

            foreach (var reference in referenceElements)
            {
                int lodLevel = GetLodLevel(reference);
                if (lodLevel != useLod)
                    continue;

                string? hrefValue = reference.Attribute(xlinkNs + "href")?.Value;
                if (string.IsNullOrEmpty(hrefValue))
                    continue;

                string targetId = hrefValue.TrimStart('#');
                if (string.IsNullOrEmpty(targetId))
                    continue;

                // Avoid processing the same referenced geometry multiple times per CityObject
                if (!processedReferences.Add(targetId))
                    continue;

                if (!elementLookup.TryGetValue(targetId, out XElement? referencedElement))
                    continue;

                foreach (var posList in ResolveReferencedPosLists(referencedElement, elementLookup, new HashSet<string>()))
                {
                    try
                    {
                        List<XYZ> surface = ParsePosList(posList.Value);
                        if (surface.Count >= 3)
                        {
                            geometry.Surfaces.Add(surface);
                        }
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        System.Diagnostics.Debug.WriteLine($"参照ポリゴンの解析エラー ({geometry.ObjectId}): {ex.Message}");
#endif
                    }
                }
            }
        }

        private static IEnumerable<XElement> ResolveReferencedPosLists(
            XElement rootElement,
            Dictionary<string, XElement> elementLookup,
            HashSet<string> visitedIds)
        {
            string? rootId = GetElementId(rootElement);
            if (!string.IsNullOrEmpty(rootId))
            {
                visitedIds.Add(rootId);
            }

            foreach (var posList in rootElement.Descendants().Where(IsPosListElement))
            {
                yield return posList;
            }

            var nestedReferences = rootElement
                .Descendants()
                .Where(e => e.Attribute(xlinkNs + "href") != null);

            foreach (var reference in nestedReferences)
            {
                string? hrefValue = reference.Attribute(xlinkNs + "href")?.Value;
                if (string.IsNullOrEmpty(hrefValue))
                    continue;

                string targetId = hrefValue.TrimStart('#');
                if (string.IsNullOrEmpty(targetId))
                    continue;

                if (!visitedIds.Add(targetId))
                    continue;

                if (elementLookup.TryGetValue(targetId, out XElement? referencedElement))
                {
                    foreach (var posList in ResolveReferencedPosLists(referencedElement, elementLookup, visitedIds))
                    {
                        yield return posList;
                    }
                }
            }
        }

        private static string? GetElementId(XElement element)
        {
            return element.Attribute(gmlNs + "id")?.Value ??
                   element.Attribute("id")?.Value ??
                   element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
        }

        private static int DetermineTargetLod(int requestedLod, int maxAvailableLod)
        {
            if (requestedLod >= 2)
            {
                if (maxAvailableLod >= 2)
                    return 2;

                if (maxAvailableLod >= 1)
                    return 1;
            }

            return 1;
        }

        private static void ReportProgress(Action<int>? progressCallback, int processed, int total)
        {
            if (progressCallback == null || total == 0)
                return;

            int percent = (int)((processed / (double)total) * 95);
            progressCallback(percent);
        }

        private static string GetTypeDisplayName(CityObjectType type)
        {
            return type switch
            {
                CityObjectType.Road => "道路",
                CityObjectType.Bridge => "橋",
                CityObjectType.All => "建物・道路",
                _ => "建物"
            };
        }
        /// <summary>
        /// 建物要素内の最高LODレベルを取得
        /// </summary>
        /// <param name="descendants">建物要素の子孫要素リスト（最適化のためキャッシュ済み）</param>
        private static int GetMaxLodLevel(List<XElement> descendants)
        {
            int maxLod = 0;

            // すべての子孫要素を調査（既にリスト化済み）
            foreach (var element in descendants)
            {
                string localName = element.Name.LocalName;

                // LOD2要素をチェック
                if (localName.StartsWith("lod2", StringComparison.OrdinalIgnoreCase))
                {
                    maxLod = Math.Max(maxLod, 2);
                }
                // LOD1要素をチェック
                else if (localName.StartsWith("lod1", StringComparison.OrdinalIgnoreCase))
                {
                    maxLod = Math.Max(maxLod, 1);
                }
            }

            // デフォルトはLOD1（LOD0は除外）
            return maxLod > 0 ? maxLod : 1;
        }

        /// <summary>
        /// 要素のLODレベルを取得
        /// </summary>
        private static int GetLodLevel(XElement element)
        {
            // 親要素を遡ってLOD要素を探す
            var current = element;
            while (current != null)
            {
                string localName = current.Name.LocalName;

                // LOD2をチェック
                if (localName.StartsWith("lod2", StringComparison.OrdinalIgnoreCase))
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

            // デフォルトはLOD1
            return 1;
        }

        /// <summary>
        /// 要素がLOD0（2D足跡データ）に属するかチェック
        /// </summary>
        private static bool IsLOD0Element(XElement element)
        {
            return GetLodLevel(element) == 0;
        }

        /// <summary>
        /// posListの文字列から座標点リストを抽出
        /// 例: "35.123 139.456 10.5 35.124 139.457 11.0 ..." → [(35.123, 139.456, 10.5), (35.124, 139.457, 11.0), ...]
        /// </summary>
        private static List<XYZ> ParsePosList(string posListText)
        {
            List<XYZ> points = new List<XYZ>();

            // 空白で分割して数値配列を取得
            string[] values = posListText.Split(new[] { ' ', '\n', '\r', '\t' },
                                                StringSplitOptions.RemoveEmptyEntries);

            // 3つずつ（X, Y, Z）読み取る
            for (int i = 0; i < values.Length - 2; i += 3)
            {
                if (double.TryParse(values[i], out double lat) &&
                    double.TryParse(values[i + 1], out double lon) &&
                    double.TryParse(values[i + 2], out double z))
                {
#if DEBUG
                    // デバッグ: 最初の3点の生座標（緯度・経度）をログ出力
                    if (points.Count < 3)
                    {
                        System.Diagnostics.Debug.WriteLine($"CityGML生座標（緯度・経度） #{points.Count + 1}: 緯度={lat:F8}, 経度={lon:F8}, 高さ={z:F2}m");
                    }
#endif

                    // 緯度・経度をメートル単位の平面座標に変換
                    XYZ convertedPoint = ConvertLatLonToMeters(lat, lon, z);
                    points.Add(convertedPoint);

#if DEBUG
                    // デバッグ: 変換後の座標をログ出力
                    if (points.Count <= 3)
                    {
                        System.Diagnostics.Debug.WriteLine($"  → 変換後（メートル） #{points.Count}: X={convertedPoint.X:F2}m, Y={convertedPoint.Y:F2}m, Z={convertedPoint.Z:F2}m");
                    }
#endif
                }
            }

            return points;
        }

        /// <summary>
        /// ヒュベニの公式による距離計算（WGS84楕円体モデル）
        /// Blender版PLATEAUインポーターから移植
        /// </summary>
        private class HubenyDistanceCalculator
        {
            // WGS84楕円体パラメータ
            private const double A = 6378137.0;              // 長半径（メートル）
            private const double B = 6356752.314245;         // 短半径（メートル）
            private const double E2 = 0.006694380022900788;  // 第一離心率の2乗

            /// <summary>
            /// 2点間の距離をヒュベニの公式で計算
            /// </summary>
            /// <returns>(x: 東西方向距離, y: 南北方向距離) メートル単位</returns>
            public static (double x, double y) Calculate(double lat1, double lon1, double lat2, double lon2)
            {
                // 度をラジアンに変換
                double radLat1 = DegreesToRadians(lat1);
                double radLon1 = DegreesToRadians(lon1);
                double radLat2 = DegreesToRadians(lat2);
                double radLon2 = DegreesToRadians(lon2);

                // 平均緯度
                double avgLat = (radLat1 + radLat2) / 2.0;

                // 緯度・経度の差分
                double dy = radLat1 - radLat2;
                double dx = radLon1 - radLon2;

                // 卯酉線曲率半径の分母
                double sinAvgLat = Math.Sin(avgLat);
                double W = Math.Sqrt(1.0 - E2 * sinAvgLat * sinAvgLat);

                // 子午線曲率半径
                double M = (A * (1.0 - E2)) / (W * W * W);

                // 卯酉線曲率半径
                double N = A / W;

                // メートル単位の距離
                double x = dx * N * Math.Cos(avgLat);
                double y = dy * M;

                return (x, y);
            }

            private static double DegreesToRadians(double degrees)
            {
                return degrees * Math.PI / 180.0;
            }
        }

        // 固定参照点（すべてのインポートで同じ座標系を使用）
        // この値はプロジェクト全体で一貫している必要がある
        private const double REFERENCE_LAT = 35.629;   // 参照緯度
        private const double REFERENCE_LON = 139.781;  // 参照経度

        /// <summary>
        /// 緯度・経度をメートル単位の平面座標に変換
        /// EPSG:6697（JGD2011地理座標）をローカル平面座標系に変換
        /// ヒュベニの公式を使用して高精度に計算
        /// </summary>
        private static XYZ ConvertLatLonToMeters(double latitude, double longitude, double height)
        {
            // ヒュベニの公式で参照点からの距離を計算
            var (x, y) = HubenyDistanceCalculator.Calculate(latitude, longitude, REFERENCE_LAT, REFERENCE_LON);

            // 注意: Calculate()の戻り値は(経度差, 緯度差)なので、そのままX/Yに対応
            // X=経度(東西), Y=緯度(南北)
            return new XYZ(x, y, height);
        }


        /// <summary>
        /// すべての建物の座標からバウンディングボックスの最小点を計算
        /// X/Y/Z すべて最小値を使用（南西の角 + 地面レベル）
        /// これにより、オフセット後も建物の相対的な位置関係が保持される
        /// </summary>
        public static XYZ CalculateBoundingBoxMin(List<CityObjectGeometry> cityObjects)
        {
            if (cityObjects.Count == 0)
                throw new ArgumentException("地物ジオメトリが空です");

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double minZ = double.MaxValue;
            int totalPoints = 0;

            foreach (var cityObject in cityObjects)
            {
                foreach (var surface in cityObject.Surfaces)
                {
                    foreach (var point in surface)
                    {
                        minX = Math.Min(minX, point.X);
                        minY = Math.Min(minY, point.Y);
                        minZ = Math.Min(minZ, point.Z);
                        totalPoints++;
                    }
                }
            }

            if (totalPoints == 0)
                throw new ArgumentException("有効な座標点が見つかりません");

#if DEBUG
            System.Diagnostics.Debug.WriteLine("");
            System.Diagnostics.Debug.WriteLine("=== バウンディングボックス最小点（メートル単位） ===");
            System.Diagnostics.Debug.WriteLine($"X={minX:F2}m, Y={minY:F2}m, Z={minZ:F2}m");
            System.Diagnostics.Debug.WriteLine($"総頂点数: {totalPoints}");
#endif

            return new XYZ(minX, minY, minZ);
        }
    }
}




