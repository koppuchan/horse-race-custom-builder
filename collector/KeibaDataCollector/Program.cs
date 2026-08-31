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
                Run(mode, arg, args);
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

        private static void Run(string mode, string arg = null, string[] args = null)
        {
            args = args ?? new string[0];
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

                    case "score":
                    {
                        // 当日出走馬の6ファクターを算出しWordPress(hrc_factors)へ反映する。
                        // ローカルSQLite（backfill済みの履歴）を読むだけで、JV-Link/UmaConnからは
                        // 当日の出走表（KettoNum突き合わせ用）のみ取得する。
                        //
                        // 引数（省略可）: yyyy-MM-dd形式の日付。省略時は今日の日付（従来通り）。
                        //
                        // 注意: RA/SE自体はJVOpen(ThisWeekAndToday)でJV-Link/UmaConn側から都度
                        // 取り直す作りのため、この絞り込みが指定日をどこまで遡って返すかは
                        // JV-Link側の「今週」判定に依存し、こちらでは制御できない。日をまたいだ
                        // 直後（例: 昨日のscore失敗分を今日中に再実行）なら通ることが多いが、
                        // 保証はできないので、実行後のログで対象日のレースが実際に何件処理された
                        // かを必ず確認すること（0レースなら、この絞り込みの対象外になっている）。
                        var targetDate = DateTime.Today;
                        foreach (var a in args)
                        {
                            if (DateTime.TryParseExact(a, "yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var parsed))
                            {
                                targetDate = parsed;
                                break;
                            }
                        }
                        if (targetDate != DateTime.Today)
                            Console.WriteLine($"対象日: {targetDate:yyyy-MM-dd}（引数指定）");

                        var wp = new WordPressClient(
                            AppConfig.WordPressBaseUrl,
                            AppConfig.WordPressUser,
                            AppConfig.WordPressAppPassword);

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        {
                            var scoring = new FactorScoringService(store);
                            RunScoreFor(jvLink, wp, scoring, targetDate);
                            RunScoreFor(umaConn, wp, scoring, targetDate);
                        }
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
                        // 6ファクター用の履歴取得。WordPressには書き込まない
                        // （ローカルSQLiteに蓄積するだけ）。
                        //
                        // 引数（順不同・省略可）:
                        //   jv / uma      : 取得元を中央競馬のみ／地方競馬のみに絞る（既定は両方）
                        //   incremental   : 差分のみ取得する（JVOpenのoption=Normal）。
                        //                   ダイアログが出ないため、タスクスケジューラからの
                        //                   無人実行はこちらを使うこと。省略時はSetup（全履歴）で、
                        //                   スタートキット確認ダイアログに人が答える必要がある。
                        var isIncremental = Array.IndexOf(args, "incremental") >= 0;
                        var sourceArg = Array.Find(args, a => a == "jv" || a == "uma");
                        var targetJv = sourceArg == null || sourceArg == "jv";
                        var targetUma = sourceArg == null || sourceArg == "uma";

                        var backfillOption = isIncremental ? DataOption.Normal : DataOption.Setup;
                        Console.WriteLine(isIncremental
                            ? "差分モード(option=Normal)で取得します。ダイアログは表示されません。"
                            : "全履歴モード(option=Setup)で取得します。スタートキット確認ダイアログが"
                              + "データ種別ごとに表示されるため、手動で応答してください"
                              + "（無人実行する場合は引数に incremental を付けてください）。");

                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        {
                            if (targetJv) RunBackfillFor(jvLink, store, backfillOption);
                            if (targetUma) RunBackfillFor(umaConn, store, backfillOption);
                        }
                        break;
                    }

                    case "dbstats":
                        // backfill済みのSQLiteの中身を件数・日付範囲で確認する。COMもJV-Link/UmaConnも
                        // 使わないため、setup/backfillと違って一瞬で終わる。
                        using (var store = new HistoricalDataStore(AppConfig.HistoricalDbPath))
                        {
                            store.PrintStats();
                        }
                        break;

                    default:
                        Console.WriteLine("使い方: KeibaDataCollector.exe [setup|morning|predict|score|watch|probe|backfill|dbstats]");
                        Console.WriteLine("  setup    : 初回のみ。利用キー等をGUIダイアログで設定する。");
                        Console.WriteLine("  morning  : 朝一バッチ。当日の出走表を取得しWordPressへ反映する。");
                        Console.WriteLine("  predict  : 朝一オッズの人気順から予想印を生成しWordPressへ反映する。");
                        Console.WriteLine("  score    : 当日出走馬の6ファクターを算出しWordPress(hrc_factors)へ反映する。");
                        Console.WriteLine("            事前にbackfillで履歴を蓄積しておく必要がある。");
                        Console.WriteLine("            引数省略時は今日。yyyy-MM-dd形式の日付を渡すとその日を対象にする");
                        Console.WriteLine("            （例: score 2026-08-30。ただしJV-Link側の「今週」判定の範囲外だと0件になる）。");
                        Console.WriteLine("  watch    : レース確定を監視し、結果・払戻を随時WordPressへ反映する。");
                        Console.WriteLine("  probe    : 調査用。どのデータ種別で何が取得できるか確認する（WordPressへは書き込まない）。");
                        Console.WriteLine("            レースを指定する場合: probe 20260811-46-1R");
                        Console.WriteLine("  backfill : 6ファクター用の過去データ取得（先にprobe推奨）。");
                        Console.WriteLine("            引数なし: 全履歴(option=Setup)。非常に時間がかかり、JV-Linkが");
                        Console.WriteLine("                      スタートキット確認ダイアログをデータ種別ごとに出すため手動実行専用。");
                        Console.WriteLine("            incremental: 差分のみ(option=Normal)。ダイアログが出ないため無人実行可。");
                        Console.WriteLine("                      定期実行（タスクスケジューラ）はこちらを使うこと。");
                        Console.WriteLine("            ソースを絞る場合: backfill jv （中央競馬のみ） / backfill uma （地方競馬のみ）");
                        Console.WriteLine("  dbstats  : backfillで蓄積したSQLiteの件数・日付範囲を確認する。");
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

        /// <summary>4種類のバックフィル（RACE/SLOP/WOOD/BLOD）を1ソース分まとめて実行する。
        /// それぞれ独立してtry/catchする: 例えば血統(BLOD)がこの契約では提供されていない場合でも、
        /// レース履歴(RACE)や調教データだけは取り込めるようにするため。</summary>
        private static void RunBackfillFor(JvSpecComDataSource source, HistoricalDataStore store,
            DataOption dataOption)
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

            var backfill = new BackfillService(source, store, dataOption);

            RunOneBackfillStep(source.SourceName, "RACE(レース履歴)", backfill.BackfillRaceEntries);
            RunOneBackfillStep(source.SourceName, "SLOP(坂路調教)", backfill.BackfillSlopeTraining);
            RunOneBackfillStep(source.SourceName, "WOOD(ウッドチップ調教)", backfill.BackfillWoodChipTraining);
            RunOneBackfillStep(source.SourceName, "BLOD(血統)", backfill.BackfillPedigree);
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

        private static void RunScoreFor(JvSpecComDataSource source, WordPress.WordPressClient wp, FactorScoringService scoring, DateTime targetDate)
        {
            try
            {
                source.Initialize(AppConfig.JvLinkSoftwareId);
                new FactorPublishService(source, wp, scoring).RunForToday(targetDate);
            }
            catch (Exception ex)
            {
                LogFailure(source.SourceName, "6ファクター算出に失敗（このソースのみスキップして続行）", ex);
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
