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

                // LOD選択ダイアログを表示
                TaskDialog lodDialog = new TaskDialog("LOD選択");
                lodDialog.MainInstruction = "インポートする詳細度を選択してください";
                lodDialog.MainContent =
                    "LOD1（簡易モデル）: 押し出し形状のシンプルな建物\n" +
                    "  - 処理速度: 速い\n" +
                    "  - 屋根形状: なし（箱型）\n\n" +
                    "LOD2（詳細モデル）: 屋根や細部を含む詳細な建物\n" +
                    "  - 処理速度: やや遅い\n" +
                    "  - 屋根形状: あり（実際の形状）";
                lodDialog.CommonButtons = TaskDialogCommonButtons.None;
                lodDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "LOD1（簡易・高速）");
                lodDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "LOD2（詳細・推奨）");
                lodDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "キャンセル");

                TaskDialogResult lodResult = lodDialog.Show();

                if (lodResult == TaskDialogResult.CommandLink3)
                {
                    return Result.Cancelled;
                }

                int targetLod = (lodResult == TaskDialogResult.CommandLink1) ? 1 : 2;

                // ステップ2: CityGMLファイルを解析（進捗バー付き）
                List<CityGMLParser.BuildingGeometry> buildings;

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
                        progressWindow.UpdateProgress(0, "CityGMLファイルを解析中...");
                    });

                    // プログレスコールバックを作成（0-95%: 解析フェーズ）
                    Action<int> parseProgressCallback = (percent) =>
                    {
                        try
                        {
                            if (progressWindow?.Dispatcher != null && !progressWindow.Dispatcher.HasShutdownStarted)
                            {
                                progressWindow.Dispatcher.BeginInvoke(() =>
                                {
                                    progressWindow.UpdateProgress(percent, $"CityGMLファイルを解析中... {percent}%");
                                });
                            }
                        }
                        catch (Exception)
                        {
                            // Dispatcherがシャットダウン中の場合は無視
                        }
                    };

                    // ダイアログを表示せず、直接解析を開始（選択されたLODを渡す）
                    buildings = CityGMLParser.ParseCityGML(gmlFilePath, parseProgressCallback, targetLod);

                    // 解析完了（95%）
                    progressWindow?.Dispatcher.Invoke(() =>
                    {
                        progressWindow.UpdateProgress(95, "解析完了。ジオメトリを生成中...");
                    });
                }
                catch (System.Xml.XmlException xmlEx)
                {
                    // プログレスウィンドウを閉じる
                    progressWindow?.Dispatcher.InvokeShutdown();
                    progressThread?.Join();

                    TaskDialog.Show(
                        "XMLエラー",
                        $"CityGMLファイルの形式が正しくありません:\n{xmlEx.Message}"
                    );
                    return Result.Failed;
                }
                catch (UnauthorizedAccessException)
                {
                    // プログレスウィンドウを閉じる
                    progressWindow?.Dispatcher.InvokeShutdown();
                    progressThread?.Join();

                    TaskDialog.Show(
                        "アクセスエラー",
                        "ファイルにアクセスできません。\n別のプログラムで開かれている可能性があります。"
                    );
                    return Result.Failed;
                }
                catch (IOException ioEx)
                {
                    // プログレスウィンドウを閉じる
                    progressWindow?.Dispatcher.InvokeShutdown();
                    progressThread?.Join();

                    TaskDialog.Show(
                        "ファイルエラー",
                        $"ファイルの読み込み中にエラーが発生しました:\n{ioEx.Message}"
                    );
                    return Result.Failed;
                }

                if (buildings.Count == 0)
                {
                    // プログレスウィンドウを閉じる
                    progressWindow?.Dispatcher.InvokeShutdown();
                    progressThread?.Join();

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
                    System.Diagnostics.Debug.WriteLine($"追加インポート: 既存オフセットを使用 X={existingOffset.OffsetX:F2}m, Y={existingOffset.OffsetY:F2}m, Z={existingOffset.OffsetZ:F2}m");
                    offset = existingOffset;
                    isAdditionalImport = true;
                }
                else
                {
                    // 初回インポート: 新しいオフセットを計算
                    System.Diagnostics.Debug.WriteLine("初回インポート: 新しいオフセットを計算");
                    CityGMLParser.XYZ bboxMin = CityGMLParser.CalculateBoundingBoxMin(buildings);
                    offset = CoordinateConverter.CalculateNewOffset(bboxMin);
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

                        // プログレスレポーターを作成（95-100%: ジオメトリ生成フェーズ）
                        var buildProgress = new Progress<int>((percent) =>
                        {
                            try
                            {
                                if (progressWindow?.Dispatcher != null && !progressWindow.Dispatcher.HasShutdownStarted)
                                {
                                    int totalPercent = 95 + (percent / 20); // 95% + (0-5%)
                                    int processedCount = (int)(percent * buildings.Count / 100.0);
                                    progressWindow.Dispatcher.BeginInvoke(() =>
                                    {
                                        progressWindow.UpdateProgress(
                                            totalPercent,
                                            $"ジオメトリ生成中... {processedCount} / {buildings.Count} 建物"
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
                            buildProgress
                        );

                        // 完了状態を表示
                        progressWindow?.Dispatcher.Invoke(() =>
                        {
                            progressWindow.UpdateProgress(100, $"完了: {shapes.Count} オブジェクトを生成しました");
                        });
                        System.Threading.Thread.Sleep(500); // 完了メッセージを表示

                        trans.Commit();

                        // プログレスウィンドウを閉じる
                        progressWindow?.Dispatcher.InvokeShutdown();
                        progressThread?.Join();

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

                        // プログレスウィンドウを閉じる
                        progressWindow?.Dispatcher.InvokeShutdown();
                        progressThread?.Join();

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
