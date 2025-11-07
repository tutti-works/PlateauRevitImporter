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
                if (double.TryParse(values[i], out double x) &&
                    double.TryParse(values[i + 1], out double y) &&
                    double.TryParse(values[i + 2], out double z))
                {
                    points.Add(new XYZ(x, y, z));
                }
            }

            return points;
        }

        /// <summary>
        /// すべての建物の座標から基準点を計算
        /// X/Yは中心、Zは最小値（地面レベル）を使用
        /// </summary>
        public static XYZ CalculateCentroid(List<BuildingGeometry> buildings)
        {
            if (buildings.Count == 0)
                throw new ArgumentException("建物データが空です");

            double sumX = 0, sumY = 0;
            double minZ = double.MaxValue;
            int totalPoints = 0;

            foreach (var building in buildings)
            {
                foreach (var surface in building.Surfaces)
                {
                    foreach (var point in surface)
                    {
                        sumX += point.X;
                        sumY += point.Y;
                        minZ = Math.Min(minZ, point.Z); // Zは最小値を使用
                        totalPoints++;
                    }
                }
            }

            if (totalPoints == 0)
                throw new ArgumentException("有効な座標点が見つかりません");

            return new XYZ(
                sumX / totalPoints,
                sumY / totalPoints,
                minZ  // Zは最小値（地面レベル）
            );
        }
    }
}
