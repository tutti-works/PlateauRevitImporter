using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlateauRevitImporter
{
    /// <summary>
    /// CityGMLインポートコマンド
    /// ユーザーがボタンをクリックしたときに実行される
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ImportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                // ステップ1: ファイル選択
                Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "CityGMLファイルを選択",
                    Filter = "CityGML Files (*.gml)|*.gml|All Files (*.*)|*.*",
                    FilterIndex = 1
                };

                bool? dialogResult = openFileDialog.ShowDialog();

                if (dialogResult != true)
                {
                    return Result.Cancelled;
                }

                string gmlFilePath = openFileDialog.FileName;
                string fileName = Path.GetFileName(gmlFilePath);

                // ファイルサイズをチェック
                long fileSizeBytes = new FileInfo(gmlFilePath).Length;
                double fileSizeMB = fileSizeBytes / (1024.0 * 1024.0);

                // ステップ2: CityGMLファイルを解析
                List<CityGMLParser.BuildingGeometry> buildings;

                try
                {
                    // ダイアログを表示せず、直接解析を開始
                    buildings = CityGMLParser.ParseCityGML(gmlFilePath);
                }
                catch (System.Xml.XmlException xmlEx)
                {
                    TaskDialog.Show(
                        "XMLエラー",
                        $"CityGMLファイルの形式が正しくありません:\n{xmlEx.Message}"
                    );
                    return Result.Failed;
                }
                catch (UnauthorizedAccessException)
                {
                    TaskDialog.Show(
                        "アクセスエラー",
                        "ファイルにアクセスできません。\n別のプログラムで開かれている可能性があります。"
                    );
                    return Result.Failed;
                }
                catch (IOException ioEx)
                {
                    TaskDialog.Show(
                        "ファイルエラー",
                        $"ファイルの読み込み中にエラーが発生しました:\n{ioEx.Message}"
                    );
                    return Result.Failed;
                }

                if (buildings.Count == 0)
                {
                    TaskDialog.Show("警告", "建物データが見つかりませんでした。");
                    return Result.Failed;
                }

                // ステップ3: 座標補正の準備
                CoordinateConverter.CoordinateOffset offset;
                bool isAdditionalImport = false;

                // 既存のPLATEAUモデルをスキャン
                CoordinateConverter.CoordinateOffset? existingOffset =
                    CoordinateConverter.GetExistingOffset(doc);

                if (existingOffset != null)
                {
                    // 追加インポート: 既存のオフセットを使用
                    offset = existingOffset;
                    isAdditionalImport = true;
                }
                else
                {
                    // 初回インポート: 新しいオフセットを計算
                    CityGMLParser.XYZ centroid = CityGMLParser.CalculateCentroid(buildings);
                    offset = CoordinateConverter.CalculateNewOffset(centroid);
                    isAdditionalImport = false;
                }

                // ステップ4: トランザクション開始してジオメトリ生成
                using (Transaction trans = new Transaction(doc, "PLATEAUインポート"))
                {
                    trans.Start();

                    try
                    {
                        // PLATEAUカテゴリを取得または作成
                        Category category = CoordinateConverter.GetOrCreatePlateauCategory(doc);

                        // プログレス付きでDirectShapeを生成
                        List<DirectShape> shapes = new List<DirectShape>();

                        // プログレスウィンドウを常に表示
                        ProgressWindow? progressWindow = null;
                        System.Threading.Thread? progressThread = null;
                        System.Threading.ManualResetEvent windowReadyEvent = new System.Threading.ManualResetEvent(false);

                        progressThread = new System.Threading.Thread(() =>
                        {
                            progressWindow = new ProgressWindow();
                            progressWindow.Loaded += (s, e) => windowReadyEvent.Set();
                            progressWindow.Show();
                            System.Windows.Threading.Dispatcher.Run();
                        });
                        progressThread.SetApartmentState(System.Threading.ApartmentState.STA);
                        progressThread.Start();

                        // ウィンドウが完全に初期化されるまで待つ
                        windowReadyEvent.WaitOne(TimeSpan.FromSeconds(2));

                        try
                        {
                            // 初期状態を更新
                            progressWindow?.Dispatcher.Invoke(() =>
                            {
                                progressWindow.UpdateProgress(0, $"0 / {buildings.Count} 建物を処理中...");
                            });

                            // プログレスレポーターを作成
                            var progress = new Progress<int>((percent) =>
                            {
                                try
                                {
                                    if (progressWindow?.Dispatcher != null && !progressWindow.Dispatcher.HasShutdownStarted)
                                    {
                                        int processedCount = (int)(percent * buildings.Count / 100.0);
                                        progressWindow.Dispatcher.BeginInvoke(() =>
                                        {
                                            progressWindow.UpdateProgress(
                                                percent,
                                                $"{processedCount} / {buildings.Count} 建物を処理中..."
                                            );
                                        });
                                    }
                                }
                                catch (TaskCanceledException)
                                {
                                    // Dispatcherがシャットダウン中の場合は無視
                                }
                            });

                            // DirectShape生成
                            shapes = GeometryBuilder.CreateDirectShapes(
                                doc,
                                buildings,
                                offset,
                                category.Id,
                                progress
                            );

                            // 完了状態を表示
                            progressWindow?.UpdateProgress(100, $"完了: {shapes.Count} オブジェクトを生成しました");
                            System.Threading.Thread.Sleep(500); // 完了メッセージを表示
                        }
                        finally
                        {
                            // プログレスウィンドウを閉じる
                            progressWindow?.Dispatcher.InvokeShutdown();
                            progressThread?.Join();
                        }

                        trans.Commit();

                        // ステップ5: 完了メッセージ（エラーログ付き）
                        string importType = isAdditionalImport ? "追加インポート" : "初回インポート";

                        string completionMessage = $"✓ インポート完了\n\n" +
                                                  $"ファイル: {fileName}\n" +
                                                  $"サイズ: {fileSizeMB:F2} MB\n" +
                                                  $"種別: {importType}\n" +
                                                  $"建物数: {buildings.Count}\n" +
                                                  $"生成: {shapes.Count} オブジェクト";

                        if (GeometryBuilder.ErrorLog.Count > 0)
                        {
                            completionMessage += $"\n\n⚠ 警告: {GeometryBuilder.ErrorLog.Count}件のエラー";

                            // エラーログを別ダイアログで表示
                            if (GeometryBuilder.ErrorLog.Count <= 10)
                            {
                                completionMessage += "\n\nエラー詳細:\n" +
                                    string.Join("\n", GeometryBuilder.ErrorLog.Take(10));
                            }
                        }

                        TaskDialog.Show("PLATEAU インポート完了", completionMessage);

                        return Result.Succeeded;
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        throw new Exception($"ジオメトリ生成中にエラーが発生しました: {ex.Message}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show(
                    "エラー",
                    $"CityGMLファイルの読み込みに失敗しました:\n\n{ex.Message}\n\n" +
                    $"詳細: {ex.InnerException?.Message}"
                );
                return Result.Failed;
            }
        }
    }
}
