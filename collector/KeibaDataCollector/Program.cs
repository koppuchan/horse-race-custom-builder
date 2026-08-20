using System;
using System.Threading;
using KeibaDataCollector.Data;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Services;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector
{
    internal static class Program
    {
        // 異常があったかどうか。タスクスケジューラの「前回の実行結果」に反映させる。
        // これが常に0だと、1日分まるごと反映されていなくても「成功」に見えてしまい、
        // お客様からの指摘で初めて気づくことになる（実際に発生した）。
        private static bool _hadFailure;

        // COM(ActiveX)相手はSTAスレッドが前提のため必須。
        [STAThread]
        private static int Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "help";
            // probe のみ第2引数でレースキーを受け取る（例: probe 20260811-46-1R）。
            var arg = args.Length > 1 ? args[1] : null;

            try
            {
                Run(mode, arg);
            }
            catch (Exception ex)
            {
                // ここまで漏れてくるのは設定不備など、処理を始める前の失敗。
                // 未処理例外のまま落とすと、サーバーではWindowsのエラー報告ダイアログが
                // 出てタスクが終了しなくなる恐れがあるため、必ず捕まえて終了コードで返す。
                LogFailure("起動", "処理を開始できませんでした", ex);
            }

            if (_hadFailure)
            {
                Console.WriteLine("異常終了: 上記のエラーを確認してください。");
                return 1;
            }
            return 0;
        }

        private static void Run(string mode, string arg = null)
        {
            // WordPressClient はここでは作らない: setup モードはWordPressに一切繋がないため、
            // WordPressUser/WordPressAppPassword 未設定でも setup だけは実行できるようにする。
            using (var jvLink = new JvSpecComDataSource(AppConfig.JvLinkProgId, "JV", "JV-Link(中央競馬)"))
            using (var umaConn = new JvSpecComDataSource(AppConfig.UmaConnProgId, "NV", "UmaConn(地方競馬)"))
            try
            {
                switch (mode)
                {
                    case "morning":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        // 片方のソース（例: UmaConn未設置）が失敗しても、もう片方は必ず動くように
                        // ソースごとに独立してtry/catchする。
                        RunMorningFor(jvLink, wp);
                        RunMorningFor(umaConn, wp);
                        break;
                    }

                    case "watch":
                    {
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        using (var cts = new CancellationTokenSource())
                        {
                            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                            // ソースごとに独立してtry/catchし、片方の失敗がもう片方の監視を止めない
                            // ようにする。
                            var jvResultTask = RunWatchFor(jvLink, wp, cts.Token);
                            var umaResultTask = RunWatchFor(umaConn, wp, cts.Token);

                            try
                            {
                                System.Threading.Tasks.Task.WaitAll(jvResultTask, umaResultTask);
                            }
                            catch (AggregateException ex)
                            {
                                // Ctrl+C 時の Task.Delay 由来のキャンセルは正常系。
                                // それ以外は握りつぶさず記録する。
                                foreach (var inner in ex.Flatten().InnerExceptions)
                                {
                                    if (inner is OperationCanceledException) continue;
                                    LogFailure("watch", "監視タスクが異常終了しました", inner);
                                }
                            }
                        }
                        break;
                    }

                    case "predict":
                    {
                        // 朝一オッズの人気順から予想印（◎○▲△）を生成して反映する。
                        // 結果監視とは独立して動くため、片方が失敗しても他方に影響しない。
                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        RunPredictFor(jvLink, wp);
                        RunPredictFor(umaConn, wp);
                        break;
                    }

                    case "probe":
                        // 調査用。どのデータ種別で何が取得できるかを実際に叩いて確認する
                        // （地方競馬でオッズ・人気が別種別で提供されていないかの確認用）。
                        // WordPressには一切書き込まない。
                        RunProbeFor(jvLink, arg);
                        RunProbeFor(umaConn, arg);
                        break;

                    case "setup":
                        // 初回1回だけ手動実行: 利用キー入力ダイアログを開いて設定を保存する。
                        // 片方のProgIDが未確認/未登録でもう片方の結果が分からなくなるのを避けるため、
                        // 個別にtry/catchして両方の結果を必ず表示する。
                        RunSetupFor(jvLink);
                        RunSetupFor(umaConn);
                        break;

                    case "backfill":
                    {
                        // 6ファクター用の履歴一括取得。非常に時間がかかるため、必ず先に
                        // 「probe」で対象データ種別が読めることを確認してから実行すること。
                        // WordPressには書き込まない（ローカルSQLiteに蓄積するだけ）。
                        // 第2引数でソースを絞れる: backfill jv / backfill uma / backfill（両方）。
                        var targetJv = arg == null || arg == "jv";
                        var targetUma = arg == null || arg == "uma";

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        {
                            if (targetJv) RunBackfillFor(jvLink, store);
                            if (targetUma) RunBackfillFor(umaConn, store);
                        }
                        break;
                    }

                    default:
                        Console.WriteLine("使い方: KeibaDataCollector.exe [setup|morning|predict|watch|probe|backfill]");
                        Console.WriteLine("  setup    : 初回のみ。利用キー等をGUIダイアログで設定する。");
                        Console.WriteLine("  morning  : 朝一バッチ。当日の出走表を取得しWordPressへ反映する。");
                        Console.WriteLine("  predict  : 朝一オッズの人気順から予想印を生成しWordPressへ反映する。");
                        Console.WriteLine("  watch    : レース確定を監視し、結果・払戻を随時WordPressへ反映する。");
                        Console.WriteLine("  probe    : 調査用。どのデータ種別で何が取得できるか確認する（WordPressへは書き込まない）。");
                        Console.WriteLine("            レースを指定する場合: probe 20260811-46-1R");
                        Console.WriteLine("  backfill : 6ファクター用の過去データ一括取得（非常に時間がかかる。先にprobe推奨）。");
                        Console.WriteLine("            ソースを絞る場合: backfill jv （中央競馬のみ） / backfill uma （地方競馬のみ）");
                        break;
                }
            }
            finally
            {
                // ここまで来れば作業は終わっている。この先はCOMの後片付けだけで、
                // そこが固まってもプロセスは終了させてよい（終了しないほうが害が大きい）。
                ShutdownWatchdog.Arm(_hadFailure ? 1 : 0);
            }
        }

        /// <summary>例外の内容をログに残す。原因調査には型と発生箇所が要るため、
        /// Messageだけでなく例外の全文（スタックトレース含む）を出す。</summary>
        private static void LogFailure(string sourceName, string what, Exception ex)
        {
            _hadFailure = true;
            Console.WriteLine($"[{sourceName}] {what}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.ToString());
        }

        private static void RunProbeFor(JvSpecComDataSource source, string raceKeySlug = null)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new DataSpecProbeService(source).Run(DateTime.Today, raceKeySlug);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{source.SourceName}] 調査失敗（このソースのみスキップ）: {ex.Message}");
            }
        }

        /// <summary>4種類のバックフィル（RACE/SLOP/WOOD/BLDN）を1ソース分まとめて実行する。
        /// それぞれ独立してtry/catchする: 例えば血統(BLDN)がこの契約では提供されていない場合でも、
        /// レース履歴(RACE)や調教データだけは取り込めるようにするため。</summary>
        private static void RunBackfillFor(JvSpecComDataSource source, HistoricalDataStore store)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "バックフィル初期化失敗（このソースをスキップ）", ex);
                return;
            }

            var backfill = new BackfillService(source, store);

            RunOneBackfillStep(source.SourceName, "RACE(レース履歴)", backfill.BackfillRaceEntries);
            RunOneBackfillStep(source.SourceName, "SLOP(坂路調教)", backfill.BackfillSlopeTraining);
            RunOneBackfillStep(source.SourceName, "WOOD(ウッドチップ調教)", backfill.BackfillWoodChipTraining);
            RunOneBackfillStep(source.SourceName, "BLDN(血統)", backfill.BackfillPedigree);
        }

        private static void RunOneBackfillStep(string sourceName, string stepName, Action step)
        {
            Console.WriteLine($"[{sourceName}] {stepName} バックフィル開始: {DateTime.Now:HH:mm:ss}");
            try
            {
                step();
            }
            catch (Exception ex)
            {
                LogFailure(sourceName, $"{stepName} バックフィル失敗（この種別のみスキップして続行）", ex);
            }
            Console.WriteLine($"[{sourceName}] {stepName} バックフィル終了: {DateTime.Now:HH:mm:ss}");
        }

        private static void RunSetupFor(JvSpecComDataSource source)
        {
            Console.WriteLine($"[{source.SourceName}] セットアップダイアログを開きます...");
            try
            {
                source.RunInteractiveSetup();
                Console.WriteLine($"[{source.SourceName}] セットアップ完了。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{source.SourceName}] セットアップ失敗: {ex.Message}");
            }
        }

        private static void RunMorningFor(JvSpecComDataSource source, WordPress.WordPressClient wp)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new RaceCardService(source, wp).RunMorningBatch(DateTime.Today, trackCode: "");
            }
            catch (Exception ex)
            {
                // 片方のソースが失敗しても、もう片方は動かす。ただし失敗は終了コードに残す。
                LogFailure(source.SourceName, "朝一バッチ失敗（このソースのみスキップして続行）", ex);
            }
        }

        private static void RunPredictFor(JvSpecComDataSource source, WordPress.WordPressClient wp)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new PredictionService(source, wp)
                    .RunAsync(DateTime.Today, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 片方のソースが失敗しても、もう片方は動かす。ただし失敗は終了コードに残す。
                // 予想が出ないことに気付けないと、お客様からの指摘で初めて分かることになる。
                LogFailure(source.SourceName, "予想の生成に失敗（このソースのみスキップして続行）", ex);
            }
        }

        // 監視が例外で落ちたときの再開待ち時間。
        private static readonly TimeSpan WatchRetryDelay = TimeSpan.FromMinutes(3);

        /// <summary>
        /// 1つのデータ源の監視を、その日の打ち切り時刻まで動かし続ける。
        ///
        /// 以前は例外を1回捕まえたら、そのソースの監視をその日ずっと諦めていた。
        /// COMや通信の一時的な失敗でも「その日は一切反映されない」ことになり、
        /// しかも終了コードは正常のままだったため気づけなかった。
        /// 落ちても間隔をあけて再開し、最後まで粘る。
        /// </summary>
        private static async System.Threading.Tasks.Task RunWatchFor(
            JvSpecComDataSource source, WordPress.WordPressClient wp, CancellationToken ct)
        {
            var attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    source.Initialize(AppConfig.JvLinkSoftwareId);
                    await new RaceResultService(source, wp, AppConfig.RealtimePollInterval)
                        .RunWatchLoopAsync(DateTime.Today, ct);

                    // 打ち切り時刻まで動ききった＝その日の監視は完了。
                    return;
                }
                catch (OperationCanceledException)
                {
                    return; // Ctrl+C / 停止要求。異常ではない。
                }
                catch (Exception ex)
                {
                    LogFailure(source.SourceName, $"監視が中断しました（{attempt}回目）", ex);
                }

                // 打ち切り時刻を過ぎていれば再開しない（翌日のタスクを妨げないため）。
                if (DateTime.Now >= DateTime.Today.Add(RaceResultService.DailyCutoff))
                {
                    Console.WriteLine($"[{source.SourceName}] 本日の監視時間を過ぎたため再開しません。");
                    return;
                }

                Console.WriteLine(
                    $"[{source.SourceName}] {WatchRetryDelay.TotalMinutes:0}分後に監視を再開します。");
                try
                {
                    await System.Threading.Tasks.Task.Delay(WatchRetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
