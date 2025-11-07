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
        /// 建物の3D形状データ
        /// </summary>
        public class BuildingGeometry
        {
            public string BuildingId { get; set; } = string.Empty;
            public List<List<XYZ>> Surfaces { get; set; } = new List<List<XYZ>>();
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

        /// <summary>
        /// CityGMLファイルから建物データを解析する
        /// </summary>
        /// <param name="filePath">CityGMLファイルのパス</param>
        /// <param name="progressCallback">進捗報告用のコールバック（オプション）</param>
        /// <param name="targetLod">インポートするLODレベル（1=簡易、2=詳細）</param>
        /// <returns>建物ジオメトリのリスト</returns>
        public static List<BuildingGeometry> ParseCityGML(string filePath, Action<int>? progressCallback = null, int targetLod = 2)
        {
            List<BuildingGeometry> buildings = new List<BuildingGeometry>();

            try
            {
                // XMLファイルを読み込み（大容量ファイルに対応）
                XDocument doc;
                using (var stream = System.IO.File.OpenRead(filePath))
                {
                    doc = XDocument.Load(stream, LoadOptions.None);
                }

                // 名前空間を動的に取得
                var root = doc.Root;
                if (root == null)
                    throw new Exception("XMLファイルのルート要素が見つかりません");

                // bldg:Building要素をすべて取得（名前空間のバリエーションに対応）
                var buildingElements = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Building" &&
                                (e.Name.Namespace.NamespaceName.Contains("building") ||
                                 e.Name.Namespace.NamespaceName.Contains("bldg")));

                if (!buildingElements.Any())
                {
                    // Buildingが見つからない場合、他の可能性を探す
                    buildingElements = doc.Descendants()
                        .Where(e => e.Name.LocalName.Contains("Building"));
                }

                int processedBuildings = 0;
                int totalBuildings = buildingElements.Count();

                foreach (var buildingElement in buildingElements)
                {
                    try
                    {
                        BuildingGeometry building = new BuildingGeometry();

                        // Building IDを取得（複数の属性名に対応）
                        var idAttr = buildingElement.Attribute(gmlNs + "id") ??
                                     buildingElement.Attribute("id") ??
                                     buildingElement.Attributes().FirstOrDefault(a => a.Name.LocalName == "id");

                        building.BuildingId = idAttr?.Value ?? $"Building_{processedBuildings}";

                        // 最高LODを検出（LOD2 > LOD1）
                        int maxLod = GetMaxLodLevel(buildingElement);

                        // ユーザーが選択したLODに基づいて使用するLODを決定
                        int useLod = targetLod;
                        if (targetLod == 2 && maxLod < 2)
                        {
                            // LOD2を選択したがデータにLOD2がない場合、LOD1にフォールバック
                            useLod = 1;
                        }
                        else if (targetLod == 1)
                        {
                            // LOD1を明示的に選択した場合、LOD2があってもLOD1を使用
                            useLod = 1;
                        }

                        System.Diagnostics.Debug.WriteLine($"建物 {building.BuildingId}: 最高LOD={maxLod}, 使用LOD={useLod}");

                        // 指定されたLODのposList要素のみを抽出（LOD0は常に除外）
                        var posLists = buildingElement.Descendants()
                            .Where(e => (e.Name.LocalName == "posList" || e.Name.LocalName == "coordinates") &&
                                       !IsLOD0Element(e) &&
                                       GetLodLevel(e) == useLod);

                        foreach (var posList in posLists)
                        {
                            try
                            {
                                List<XYZ> surface = ParsePosList(posList.Value);
                                if (surface.Count >= 3) // 最低3点必要（三角形）
                                {
                                    building.Surfaces.Add(surface);
                                }
                            }
                            catch (Exception ex)
                            {
                                // 個別の面の解析エラーはスキップ
                                System.Diagnostics.Debug.WriteLine($"面の解析エラー: {ex.Message}");
                            }
                        }

                        // 有効な面を持つ建物のみ追加
                        if (building.Surfaces.Count > 0)
                        {
                            buildings.Add(building);
                        }

                        processedBuildings++;

                        // 進捗報告（0-95%の範囲で報告）
                        if (progressCallback != null && totalBuildings > 0)
                        {
                            int percent = (int)((processedBuildings / (double)totalBuildings) * 95);
                            progressCallback(percent);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 個別の建物の解析エラーはスキップして続行
                        System.Diagnostics.Debug.WriteLine($"建物の解析エラー: {ex.Message}");
                    }
                }

                if (buildings.Count == 0)
                {
                    throw new Exception(
                        "有効な建物データが見つかりませんでした。\n" +
                        "このファイルはCityGML 2.0形式ですか？\n" +
                        "建物（bldg:Building）要素が含まれているか確認してください。"
                    );
                }

                return buildings;
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

        /// <summary>
        /// 建物要素内の最高LODレベルを取得
        /// </summary>
        private static int GetMaxLodLevel(XElement buildingElement)
        {
            int maxLod = 0;

            // すべての子孫要素を調査
            foreach (var element in buildingElement.Descendants())
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
                    // デバッグ: 最初の3点の生座標（緯度・経度）をログ出力
                    if (points.Count < 3)
                    {
                        System.Diagnostics.Debug.WriteLine($"CityGML生座標（緯度・経度） #{points.Count + 1}: 緯度={lat:F8}, 経度={lon:F8}, 高さ={z:F2}m");
                    }

                    // 緯度・経度をメートル単位の平面座標に変換
                    XYZ convertedPoint = ConvertLatLonToMeters(lat, lon, z);
                    points.Add(convertedPoint);

                    // デバッグ: 変換後の座標をログ出力
                    if (points.Count <= 3)
                    {
                        System.Diagnostics.Debug.WriteLine($"  → 変換後（メートル） #{points.Count}: X={convertedPoint.X:F2}m, Y={convertedPoint.Y:F2}m, Z={convertedPoint.Z:F2}m");
                    }
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
        public static XYZ CalculateBoundingBoxMin(List<BuildingGeometry> buildings)
        {
            if (buildings.Count == 0)
                throw new ArgumentException("建物データが空です");

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double minZ = double.MaxValue;
            int totalPoints = 0;

            foreach (var building in buildings)
            {
                foreach (var surface in building.Surfaces)
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

            // デバッグ: 計算されたバウンディングボックス最小点をログ出力
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"=== バウンディングボックス最小点（メートル単位） ===");
            System.Diagnostics.Debug.WriteLine($"X={minX:F2}m, Y={minY:F2}m, Z={minZ:F2}m");
            System.Diagnostics.Debug.WriteLine($"総頂点数: {totalPoints}");

            return new XYZ(minX, minY, minZ);
        }
    }
}
