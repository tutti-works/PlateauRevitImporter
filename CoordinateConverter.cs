using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Collections.Generic;
using System.Linq;

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
        private static readonly string OffsetXFieldName = "OffsetX";
        private static readonly string OffsetYFieldName = "OffsetY";
        private static readonly string OffsetZFieldName = "OffsetZ";

        // PLATEAU識別用のサブカテゴリ名
        public const string PlateauCategoryName = "PLATEAU_Imported_Model";

        /// <summary>
        /// 座標のオフセット値（移動ベクトル）
        /// </summary>
        public class CoordinateOffset
        {
            public double OffsetX { get; set; }
            public double OffsetY { get; set; }
            public double OffsetZ { get; set; }

            public CoordinateOffset(double offsetX, double offsetY, double offsetZ)
            {
                OffsetX = offsetX;
                OffsetY = offsetY;
                OffsetZ = offsetZ;
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
        /// モデルの中心座標がRevit原点(0,0,0)付近に来るように計算
        /// </summary>
        public static CoordinateOffset CalculateNewOffset(CityGMLParser.XYZ centroid)
        {
            // 中心座標を原点に移動するためのオフセット（符号反転）
            return new CoordinateOffset(
                -centroid.X,
                -centroid.Y,
                -centroid.Z
            );
        }

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

            return new XYZ(x, y, z);
        }

        /// <summary>
        /// DirectShapeにオフセット情報を保存
        /// </summary>
        public static void SaveOffsetToElement(DirectShape shape, CoordinateOffset offset)
        {
            // TODO: Extensible Storageの保存は一時的に無効化
            // Revit 2025+のSpecTypeId互換性問題のため
            // 将来的には、文字列フィールドとしてJSON形式で保存する方法を検討
            return;

            /*
            try
            {
                // スキーマを取得または作成
                Schema schema = GetOrCreateSchema();

                // データエンティティを作成
                Entity entity = new Entity(schema);

                // 3つの個別のdoubleフィールドでオフセットを保存
                entity.Set(schema.GetField(OffsetXFieldName), offset.OffsetX);
                entity.Set(schema.GetField(OffsetYFieldName), offset.OffsetY);
                entity.Set(schema.GetField(OffsetZFieldName), offset.OffsetZ);

                // DirectShapeに保存
                shape.SetEntity(entity);
            }
            catch (Exception ex)
            {
                throw new Exception($"オフセット情報の保存に失敗: {ex.Message}", ex);
            }
            */
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

                // 3つの個別のdoubleフィールドから読み取り
                double offsetX = entity.Get<double>(schema.GetField(OffsetXFieldName));
                double offsetY = entity.Get<double>(schema.GetField(OffsetYFieldName));
                double offsetZ = entity.Get<double>(schema.GetField(OffsetZFieldName));

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

            // オフセット値を3つの個別のdoubleフィールドとして追加
            // Revit 2025+では単位指定が必須 - 単位なし（Unitless）を使用
            FieldBuilder fieldX = schemaBuilder.AddSimpleField(OffsetXFieldName, typeof(double));
            fieldX.SetDocumentation("PLATEAU座標補正オフセット値 X (メートル単位)");
            fieldX.SetSpec(SpecTypeId.Number); // 単位なしの数値

            FieldBuilder fieldY = schemaBuilder.AddSimpleField(OffsetYFieldName, typeof(double));
            fieldY.SetDocumentation("PLATEAU座標補正オフセット値 Y (メートル単位)");
            fieldY.SetSpec(SpecTypeId.Number); // 単位なしの数値

            FieldBuilder fieldZ = schemaBuilder.AddSimpleField(OffsetZFieldName, typeof(double));
            fieldZ.SetDocumentation("PLATEAU座標補正オフセット値 Z (メートル単位)");
            fieldZ.SetSpec(SpecTypeId.Number); // 単位なしの数値

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
    }
}
