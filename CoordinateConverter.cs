using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PlateauRevitImporter
{
    /// <summary>
    /// 座標補正とExtensible Storageの管理を行うクラス
    /// PLATEAUの測地座標をRevitの作図範囲内に変換する
    /// </summary>
    public class CoordinateConverter
    {
        // Extensible Storage用のGUID（このアドオン専用の一意なID）
        private static readonly Guid SchemaGuid = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891"); // GUIDを変更（スキーマ変更のため）
        private static readonly string SchemaName = "PlateauImportData";
        private static readonly string OffsetJsonFieldName = "OffsetJson";
        private static readonly string OffsetXFieldName = "OffsetX"; // 旧フィールド（互換用）
        private static readonly string OffsetYFieldName = "OffsetY"; // 旧フィールド（互換用）
        private static readonly string OffsetZFieldName = "OffsetZ"; // 旧フィールド（互換用）

        // PLATEAU識別用のサブカテゴリ名
        public const string PlateauCategoryName = "PLATEAU_Imported_Model";

        /// <summary>
        /// 座標のオフセット値（移動ベクトル）と参照点
        /// </summary>
        public class CoordinateOffset
        {
            public double OffsetX { get; set; }
            public double OffsetY { get; set; }
            public double OffsetZ { get; set; }
            public double ReferenceLat { get; set; }
            public double ReferenceLon { get; set; }

            public CoordinateOffset(double offsetX, double offsetY, double offsetZ, double referenceLat = 0, double referenceLon = 0)
            {
                OffsetX = offsetX;
                OffsetY = offsetY;
                OffsetZ = offsetZ;
                ReferenceLat = referenceLat;
                ReferenceLon = referenceLon;
            }
        }

        /// <summary>
        /// プロジェクト内に既存のPLATEAUモデルが存在するかチェックし、
        /// 存在する場合はそのオフセット値を取得
        /// </summary>
        public static CoordinateOffset? GetExistingOffset(Document doc)
        {
            try
            {
                // DirectShapeのフィルター
                FilteredElementCollector collector = new FilteredElementCollector(doc);
                var directShapes = collector.OfClass(typeof(DirectShape)).Cast<DirectShape>();

                // PLATEAUカテゴリを持つDirectShapeを検索
                foreach (var shape in directShapes)
                {
                    // Extensible Storageからデータを取得
                    CoordinateOffset? offset = ReadOffsetFromElement(shape);
                    if (offset != null)
                    {
                        return offset;
                    }
                }

                return null; // 既存モデルなし
            }
            catch (Exception ex)
            {
                throw new Exception($"既存オフセットの取得に失敗: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 新しいオフセット値を計算（初回インポート時）
        /// 動的参照点を使用してオフセットを計算
        /// </summary>
        public static CoordinateOffset CalculateNewOffset(CityGMLParser.XYZ bboxMin, double referenceLat, double referenceLon)
        {
            // 注意: bboxMinは既に参照点からの相対座標（メートル単位）
            // XY座標: オフセット = 0 で、すべてのインポートが同じ座標系を使用
            // Z座標: バウンディングボックスの最小値を引いて地面レベルを0にする
            var offset = new CoordinateOffset(0, 0, -bboxMin.Z, referenceLat, referenceLon);

#if DEBUG
            // デバッグ: 計算されたオフセットをログ出力
            System.Diagnostics.Debug.WriteLine($"=== 計算されたオフセット（動的参照点方式） ===");
            System.Diagnostics.Debug.WriteLine($"参照点: ({referenceLat:F6}°, {referenceLon:F6}°)");
            System.Diagnostics.Debug.WriteLine($"X={offset.OffsetX:F2}m, Y={offset.OffsetY:F2}m, Z={offset.OffsetZ:F2}m");
            System.Diagnostics.Debug.WriteLine($"参照点からの相対座標を直接使用（追加インポート対応）");
            System.Diagnostics.Debug.WriteLine($"Z座標: バウンディングボックス最小値={bboxMin.Z:F2}mを引いて地面レベルを0に調整");
            System.Diagnostics.Debug.WriteLine($"");
#endif

            return offset;
        }

        private static int debugOffsetCount = 0;

        /// <summary>
        /// オフセットを適用して座標を変換
        /// </summary>
        public static XYZ ApplyOffset(CityGMLParser.XYZ originalPoint, CoordinateOffset offset)
        {
            // メートル単位からフィート単位に変換（Revitはフィート単位）
            const double metersToFeet = 3.28084;

            double x = (originalPoint.X + offset.OffsetX) * metersToFeet;
            double y = (originalPoint.Y + offset.OffsetY) * metersToFeet;
            double z = (originalPoint.Z + offset.OffsetZ) * metersToFeet;

#if DEBUG
            // デバッグ: 最初の数点のオフセット適用を確認
            if (debugOffsetCount < 5)
            {
                System.Diagnostics.Debug.WriteLine($"ApplyOffset #{debugOffsetCount}: 元座標Z={originalPoint.Z:F2}m, オフセットZ={offset.OffsetZ:F2}m → 結果Z={z:F2}ft");
                debugOffsetCount++;
            }
#endif

            return new XYZ(x, y, z);
        }

        /// <summary>
        /// DirectShapeにオフセット情報を保存
        /// </summary>
        public static void SaveOffsetToElement(DirectShape shape, CoordinateOffset offset)
        {
            try
            {
                Schema schema = GetOrCreateSchema();
                Entity entity = new Entity(schema);

                string json = JsonSerializer.Serialize(new OffsetStorageModel
                {
                    OffsetX = offset.OffsetX,
                    OffsetY = offset.OffsetY,
                    OffsetZ = offset.OffsetZ,
                    ReferenceLat = offset.ReferenceLat,
                    ReferenceLon = offset.ReferenceLon
                });

                Field? jsonField = schema.GetField(OffsetJsonFieldName);
                if (jsonField == null)
                    throw new InvalidOperationException("OffsetJsonフィールドを取得できません。");

                entity.Set(jsonField, json);
                shape.SetEntity(entity);
            }
            catch (Exception ex)
            {
                throw new Exception($"オフセット情報の保存に失敗: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// DirectShapeからオフセット情報を読み取る
        /// </summary>
        private static CoordinateOffset? ReadOffsetFromElement(DirectShape shape)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null)
                    return null;

                Entity entity = shape.GetEntity(schema);
                if (!entity.IsValid())
                    return null;

                Field? jsonField = schema.GetField(OffsetJsonFieldName);
                if (jsonField != null)
                {
                    string? json = entity.Get<string>(jsonField);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        try
                        {
                            var storage = JsonSerializer.Deserialize<OffsetStorageModel>(json);
                            if (storage != null)
                            {
                                return new CoordinateOffset(
                                    storage.OffsetX,
                                    storage.OffsetY,
                                    storage.OffsetZ,
                                    storage.ReferenceLat,
                                    storage.ReferenceLon);
                            }
                        }
                        catch (JsonException)
                        {
                            // JSON形式でない場合は旧フォーマットを試みる
                        }
                    }
                }

                return TryReadLegacyOffset(entity, schema);
            }
            catch
            {
                return null;
            }
        }

        private static CoordinateOffset? TryReadLegacyOffset(Entity entity, Schema schema)
        {
            try
            {
                Field? fieldX = schema.GetField(OffsetXFieldName);
                Field? fieldY = schema.GetField(OffsetYFieldName);
                Field? fieldZ = schema.GetField(OffsetZFieldName);

                if (fieldX == null || fieldY == null || fieldZ == null)
                    return null;

                double offsetX = entity.Get<double>(fieldX);
                double offsetY = entity.Get<double>(fieldY);
                double offsetZ = entity.Get<double>(fieldZ);

                return new CoordinateOffset(offsetX, offsetY, offsetZ);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extensible Storage用のスキーマを取得または作成
        /// </summary>
        private static Schema GetOrCreateSchema()
        {
            // 既存のスキーマを検索
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null)
                return schema;

            // 新規作成
            SchemaBuilder schemaBuilder = new SchemaBuilder(SchemaGuid);
            schemaBuilder.SetSchemaName(SchemaName);
            schemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);

            FieldBuilder fieldJson = schemaBuilder.AddSimpleField(OffsetJsonFieldName, typeof(string));
            fieldJson.SetDocumentation("PLATEAU座標補正オフセット値（JSON形式）");
            // 文字列型にはSetSpecは不要（単位変換を使用しない型のため）

            return schemaBuilder.Finish();
        }

        /// <summary>
        /// PLATEAUサブカテゴリを取得または作成し、親カテゴリを返す
        /// DirectShapeには親カテゴリのIDが必要
        /// </summary>
        public static Category GetOrCreatePlateauCategory(Document doc)
        {
            // 汎用モデルカテゴリを取得
            Category genericModelCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_GenericModel);

            // サブカテゴリの存在確認と作成
            bool subCategoryExists = false;
            foreach (Category subCat in genericModelCategory.SubCategories)
            {
                if (subCat.Name == PlateauCategoryName)
                {
                    subCategoryExists = true;
                    break;
                }
            }

            // サブカテゴリが存在しない場合は作成
            if (!subCategoryExists)
            {
                doc.Settings.Categories.NewSubcategory(
                    genericModelCategory,
                    PlateauCategoryName
                );
            }

            // DirectShapeには親カテゴリを返す
            return genericModelCategory;
        }

        private class OffsetStorageModel
        {
            public double OffsetX { get; set; }
            public double OffsetY { get; set; }
            public double OffsetZ { get; set; }
            public double ReferenceLat { get; set; }
            public double ReferenceLon { get; set; }
        }
    }
}
