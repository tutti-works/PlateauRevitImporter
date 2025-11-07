using Autodesk.Revit.UI;
using System;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace PlateauRevitImporter
{
    /// <summary>
    /// Revitアプリケーションの起動時に実行されるクラス
    /// リボンタブとボタンを追加する
    /// </summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // "PLATEAU"タブを作成
                string tabName = "PLATEAU";
                application.CreateRibbonTab(tabName);

                // リボンパネルを作成
                RibbonPanel panel = application.CreateRibbonPanel(tabName, "インポート");

                // アセンブリのパスを取得
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                // プッシュボタンのデータを作成
                PushButtonData buttonData = new PushButtonData(
                    "CityGMLImport",
                    "CityGML\nインポート",
                    assemblyPath,
                    "PlateauRevitImporter.ImportCommand"
                );

                // ツールチップを設定
                buttonData.ToolTip = "PLATEAU CityGMLファイルをRevitにインポートします";
                buttonData.LongDescription =
                    "CityGML 2.0形式の3D都市モデルをRevitプロジェクトにインポートします。\n" +
                    "座標は自動的にRevitの作図範囲内に補正されます。";

                // ボタンをパネルに追加
                PushButton button = panel.AddItem(buttonData) as PushButton;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("エラー", $"アドオンの初期化に失敗しました:\n{ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // クリーンアップ処理（必要に応じて）
            return Result.Succeeded;
        }
    }
}
