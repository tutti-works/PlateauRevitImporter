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
        /// <returns>建物ジオメトリのリスト</returns>
        public static List<BuildingGeometry> ParseCityGML(string filePath)
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

                        // すべてのposList要素を抽出
                        var posLists = buildingElement.Descendants()
                            .Where(e => e.Name.LocalName == "posList" ||
                                       e.Name.LocalName == "coordinates");

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
                            processedBuildings++;
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
        /// 緯度・経度をメートル単位の平面座標に変換
        /// EPSG:6697（JGD2011地理座標）をローカル平面座標系に簡易変換
        /// </summary>
        private static XYZ ConvertLatLonToMeters(double latitude, double longitude, double height)
        {
            // 東京周辺（緯度35度付近）の近似変換係数
            const double METERS_PER_DEGREE_LAT = 111000.0;  // 緯度1度 ≈ 111km
            const double METERS_PER_DEGREE_LON = 91000.0;   // 経度1度 ≈ 91km（緯度35度）

            // 参照点を設定（データの範囲内の適当な点）
            // この値は最初のバウンディングボックスの中心付近
            const double REFERENCE_LAT = 35.629;   // 参照緯度
            const double REFERENCE_LON = 139.781;  // 参照経度

            // 参照点からの差分をメートルに変換
            // 注意: 一般的なマッピングでは X=経度(東西), Y=緯度(南北)
            double x = (longitude - REFERENCE_LON) * METERS_PER_DEGREE_LON;  // 東西方向
            double y = (latitude - REFERENCE_LAT) * METERS_PER_DEGREE_LAT;   // 南北方向

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
