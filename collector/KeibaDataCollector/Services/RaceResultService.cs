using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 確定後リアルタイム: レース確定検知〜結果・払戻の取得〜WordPress即時反映をポーリングで回す。
    ///
    /// JVRTOpenの"0B12"（払戻確定）は、公式にはJVWatchEvent（COMイベント）が返すレース単位の
    /// キー（"YYYYMMDDJJRR"）で呼ぶ設計だが、後期バインドではCOMイベントを購読できないため、
    /// 朝一相当のクエリで当日のレース一覧を先に取得し、レースごとに個別ポーリングする方式にしている。
    /// </summary>
    public class RaceResultService
    {
        // 速報オッズ（単複枠）。JV-Data仕様書「（２）速報系データ」より、O1レコード
        // （単勝オッズ・単勝人気順を含む）が返る種別。
        private const string RealtimeOddsDataSpec = "0B31";

        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;
        private readonly TimeSpan _pollInterval;

        // レースキーごとに結果を積み上げるバッファ。SE/HR/RAレコードが個別に届くたび、
        // このレースの最新状態でWordPressへ再送する（Eventually Consistentな即時反映）。
        private readonly Dictionary<string, RaceResult> _buffers = new Dictionary<string, RaceResult>();

        public RaceResultService(IRaceDataSource source, WordPressClient wp, TimeSpan pollInterval)
        {
            _source = source;
            _wp = wp;
            _pollInterval = pollInterval;
        }

        // ポーリングが生きているのに未確定レコードは何もログを出さないため、外から見ると
        // 「待機中」と「詰まっている」の区別がつかない。一定周期ごとに状況を出力する。
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

        // レース一覧を取り直す間隔。起動時に1回だけ取得する作りだと、
        // その時点でまだ当日のレースが配信されていなければ「0件」で即終了し、
        // その日は一切反映されないまま終わってしまう（実機で本日分が0件のまま
        // 終了する事象が発生）。定期的に取り直して拾い直す。
        // 一覧に現れてから監視対象に入るまでの遅れがそのまま反映の遅れになるため、
        // 短すぎない範囲で細かく取り直す。
        private static readonly TimeSpan RediscoverInterval = TimeSpan.FromMinutes(10);

        // その日の監視を打ち切る時刻。地方競馬のナイター（概ね21時台まで）を
        // 見終えてから終了し、翌日のトリガーを妨げないようにする。
        // 中断からの再開判定にも使うため公開する。
        public static readonly TimeSpan DailyCutoff = TimeSpan.FromHours(23.5);

        public async Task RunWatchLoopAsync(DateTime targetDate, CancellationToken ct)
        {
            // 確定済みのレースキー。取り直しのたびに再登録されるのを防ぐ。
            var completed = new HashSet<string>();
            var pending = new List<RaceKey>();

            var lastHeartbeat = DateTime.Now;
            var lastDiscovery = DateTime.MinValue;
            var cutoff = targetDate.Date.Add(DailyCutoff);

            while (!ct.IsCancellationRequested && DateTime.Now < cutoff)
            {
                // 起動直後と、以降は一定間隔でレース一覧を取り直す。
                // 開催途中で追加されたレースや、起動が早すぎた場合も拾える。
                if (DateTime.Now - lastDiscovery >= RediscoverInterval)
                {
                    lastDiscovery = DateTime.Now;
                    MergeDiscoveredRaces(targetDate, pending, completed);
                }

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (ct.IsCancellationRequested) break;

                    var raceKey = pending[i];
                    bool confirmed;
                    try
                    {
                        confirmed = await CheckAndPublishRaceAsync(raceKey);
                    }
                    catch (Exception ex)
                    {
                        // このレースの監視で失敗しても、他のレースの監視は続ける。
                        Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 監視中にエラー（次回リトライ）: {ex.Message}");
                        confirmed = false;
                    }

                    if (confirmed)
                    {
                        Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} 払戻確定・反映完了。監視対象から除外。");
                        completed.Add(raceKey.AsSlug());
                        pending.RemoveAt(i);
                    }
                }

                // ここで「未確定が0になったら終了」としてはいけない。
                // レース一覧(RAレコード)は当日ぶんが一度に揃うとは限らず、開催の進行に
                // 合わせて順次配信される。実機で確認: 起動時に園田1Rしか一覧に無く、
                // それを反映した時点で未確定0・確定1となり監視を終了してしまい、
                // 以降のレースが1件も反映されないまま1日が終わった。
                // 未確定が0でも打ち切り時刻まで待機し、取り直しで現れたレースを拾う。

                if (DateTime.Now - lastHeartbeat >= HeartbeatInterval)
                {
                    // メモリ使用量も出す。JVGets/NVGetsへ渡すバッファの扱いを誤ると
                    // 1レコードごとに解放されない領域が積み上がり、数時間後に
                    // ヒープ破損でプロセスごと落ちる（実機で発生）。
                    // 値が単調に増え続けていないかを、この行だけで追えるようにしておく。
                    var workingSetMb = Environment.WorkingSet / 1024d / 1024d;
                    Console.WriteLine(
                        $"[{_source.SourceName}] 監視中... 未確定{pending.Count}件 / 確定済み{completed.Count}件 " +
                        $"（{DateTime.Now:HH:mm:ss}時点、メモリ{workingSetMb:0}MB）。");
                    lastHeartbeat = DateTime.Now;
                }

                await Task.Delay(_pollInterval, ct);
            }

            // 1日の締めとして結果を1行にまとめる。ログ末尾を見るだけで、
            // その日きちんと反映できたのか／取りこぼしたのかが判断できるようにする。
            if (completed.Count == 0 && pending.Count > 0)
            {
                Console.WriteLine(
                    $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 監視終了: " +
                    $"[要確認] 監視対象{pending.Count}件に対し確定は0件でした。");
            }
            else
            {
                Console.WriteLine(
                    $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 監視終了: " +
                    $"確定{completed.Count}件 / 未確定のまま{pending.Count}件。");
            }
        }

        /// <summary>当日のレース一覧を取り直し、未確定かつ未登録のものを監視対象に足す。
        /// 取得に失敗しても、既に監視中のレースは止めない。</summary>
        private void MergeDiscoveredRaces(DateTime targetDate, List<RaceKey> pending, HashSet<string> completed)
        {
            List<RaceKey> discovered;
            try
            {
                discovered = DiscoverTodaysRaceKeys(targetDate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_source.SourceName}] レース一覧の取り直しに失敗（監視は継続）: {ex.Message}");
                return;
            }

            var known = new HashSet<string>(pending.Select(k => k.AsSlug()));
            var added = 0;

            foreach (var raceKey in discovered)
            {
                var slug = raceKey.AsSlug();
                if (completed.Contains(slug) || known.Contains(slug)) continue;

                pending.Add(raceKey);
                known.Add(slug);
                added++;
            }

            // レース一覧は開催の進行に合わせて増えていくため、毎回の取得総数を残す。
            // 「一覧に何件見えているのか」が分からないと、反映漏れの切り分けができない。
            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} レース一覧を取得: {discovered.Count}件" +
                (added > 0 ? $"（うち{added}件を監視対象に追加）" : "（追加なし）") +
                $" 未確定{pending.Count}件 / 確定済み{completed.Count}件");
        }

        /// <summary>
        /// 0B12（速報レース情報・成績確定後）は、1レースにつき段階的に4回配信される
        /// （JV-Data仕様書 データ提供タイミングより）:
        ///   払戻･3着まで確定 → 払戻･5着まで確定 → 払戻･全馬着順確定 → 払戻･全馬着順+コーナ通過順
        /// このときSE(馬毎レース情報)のデータ区分は 3→4→5→6 と進み、仕様書の特記事項に
        /// 「区分が3(3着まで確定)・4(5着まで確定)の場合、該当馬のみの情報を返す
        /// （順位が確定していない馬の情報は返さない）」と明記されている。
        ///
        /// 実機で確認: 払戻(HR)を受け取った時点で監視終了としていたため、最初の
        /// 「3着まで確定」で打ち切ってしまい、12頭立てのレースが着順3件・コーナー0件で
        /// 確定扱いになっていた。全馬着順+コーナ通過順（区分6）以降まで待つ必要がある。
        /// </summary>
        private const string DataKubunAllFinishersWithCorner = "6";
        private const string DataKubunFinalResult = "7";

        private static bool IsCompleteResult(string dataKubun) =>
            dataKubun == DataKubunAllFinishersWithCorner || dataKubun == DataKubunFinalResult;

        /// <summary>指定レースの確定状況をJVRTOpen("0B12", レースキー)で確認し、
        /// データがあれば取得・WordPress反映する（段階的に届くため、その時点の最新内容で都度反映）。
        /// 全馬着順+コーナ通過順まで確定したらtrueを返す（＝このレースはもう監視不要）。</summary>
        private async Task<bool> CheckAndPublishRaceAsync(RaceKey raceKey)
        {
            int rc = _source.OpenRealtime("0B12", raceKey.AsJvRealtimeKey());
            if (rc == -1)
            {
                // JV-Linkインターフェース仕様書のコード表より: -1は「該当データ無し」＝まだ未確定。
                // ただし同仕様書に「-1の場合もJVCloseを呼び出して取り込み処理を終了してください」と
                // 明記されている。実機で確認: ここでCloseを呼ばずにreturnすると、次回以降の
                // OpenRealtime呼び出しが全て「-202 前回のOpenに対してCloseが呼ばれていない
                // （オープン中）」で失敗し続ける。
                _source.Close();
                return false;
            }
            if (rc != 0)
            {
                // 例外を投げる前にもCloseを呼び、次回以降の呼び出しが-202で
                // 連鎖的に失敗しないようにする。
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} OpenRealtime failed: {rc}");
            }

            var result = GetOrCreate(raceKey);
            bool gotAnyRecord = false;
            bool isComplete = false;
            var seenDataKubun = new SortedSet<string>();

            // Open成功後は、読み込みループの途中で例外が発生した場合でも必ずCloseが
            // 呼ばれるようtry/finallyで保護する（-202連鎖を防ぐため）。
            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue; // ファイル切り替わり（正常）
                    if (size == -3)
                    {
                        await Task.Delay(500);
                        continue;
                    }
                    if (size < 0)
                        throw new InvalidOperationException($"{_source.SourceName} Read failed: {size}");

                    var dataKubun = JvRecordParser.GetDataKubun(buffer);

                    switch (JvRecordParser.GetRecordTypeId(buffer))
                    {
                        case "SE":
                        {
                            var (_, entry) = JvRecordParser.ParseRaceResult(buffer);
                            // 段階配信のたびに同じ馬が再送されるため、馬番で上書きマージする。
                            result.Entries.RemoveAll(e => e.Umaban == entry.Umaban);
                            result.Entries.Add(entry);
                            gotAnyRecord = true;
                            seenDataKubun.Add(dataKubun);
                            if (IsCompleteResult(dataKubun)) isComplete = true;
                            break;
                        }
                        case "HR":
                        {
                            var (_, payouts) = JvRecordParser.ParsePayouts(buffer);
                            result.Payouts = payouts;
                            gotAnyRecord = true;
                            break;
                        }
                        case "RA":
                        {
                            var (_, cornerPassage) = JvRecordParser.ParseCornerPassage(buffer);
                            // コーナー通過順位は区分6以降でのみ設定される。空で上書きしない。
                            if (cornerPassage.Count > 0)
                                result.CornerPassage = cornerPassage;
                            gotAnyRecord = true;
                            seenDataKubun.Add(dataKubun);
                            if (IsCompleteResult(dataKubun)) isComplete = true;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            // rc=0でもレコードが1件も無い場合は反映不要（無駄なWordPress更新を避ける）。
            if (!gotAnyRecord) return false;

            // 地方競馬（UmaConn）はSEレコードの単勝オッズ・単勝人気順を初期値のまま返すため、
            // 速報オッズ("0B31")から補完する（実機で0B31には実値が入っていることを確認済み）。
            // 中央競馬はSEに実値が入るので、その場合はここを通らない＝挙動を変えない。
            if (result.Entries.Any(e => e.TanshoOdds <= 0 && e.Ninki <= 0))
                MergeTanshoOddsFromRealtime(raceKey, result);

            result.Entries.Sort((a, b) => a.Umaban.CompareTo(b.Umaban));

            // 内容が前回から変わっていなければWordPressへは送られない。その場合ログも出さない
            // （速報段階で止まっているレースが毎回同じ行を出力し続けるのを防ぐ）。
            var published = await _wp.PublishRaceResultAsync(result);
            if (published)
            {
                Console.WriteLine(
                    $"[{_source.SourceName}] {raceKey.AsSlug()} 反映（着順{result.Entries.Count}件, " +
                    $"払戻{result.Payouts.Count}件, コーナー{result.CornerPassage.Count}件, " +
                    $"データ区分[{string.Join(",", seenDataKubun)}]{(isComplete ? " 確定" : " 速報・続報待ち")}）");
            }

            return isComplete;
        }

        /// <summary>速報オッズ("0B31" 単複枠)から単勝オッズ・単勝人気順を取得し、
        /// 値が入っていない出走馬に補完する。取得できなくても結果本体の反映は続行する。</summary>
        private void MergeTanshoOddsFromRealtime(RaceKey raceKey, RaceResult result)
        {
            Dictionary<int, JvRecordParser.TanshoOdds> byUmaban = null;

            try
            {
                int rc = _source.OpenRealtime(RealtimeOddsDataSpec, raceKey.AsJvRealtimeKey());
                if (rc != 0)
                {
                    _source.Close();
                    return; // -1（該当データ無し）等。オッズ無しのまま反映する。
                }

                try
                {
                    while (true)
                    {
                        int size = _source.Read(out var buffer, out _);
                        if (size == 0) break;
                        if (size == -1) continue;
                        if (size == -3) { Thread.Sleep(500); continue; }
                        if (size < 0) break;

                        if (JvRecordParser.GetRecordTypeId(buffer) != "O1") continue;

                        var (_, parsed) = JvRecordParser.ParseTanshoOdds(buffer);
                        if (parsed.Count > 0) byUmaban = parsed;
                    }
                }
                finally
                {
                    _source.Close();
                }
            }
            catch (Exception ex)
            {
                // オッズは補助情報。取得に失敗しても着順・払戻の反映は止めない。
                Console.WriteLine($"[{_source.SourceName}] {raceKey.AsSlug()} オッズ取得失敗（オッズ無しで反映）: {ex.Message}");
                return;
            }

            if (byUmaban == null) return;

            foreach (var entry in result.Entries)
            {
                if (!byUmaban.TryGetValue(entry.Umaban, out var odds)) continue;
                // SE側に実値があればそちらを尊重し、無い項目だけ埋める。
                if (entry.TanshoOdds <= 0) entry.TanshoOdds = odds.Odds;
                if (entry.Ninki <= 0) entry.Ninki = odds.Ninki;
            }
        }

        /// <summary>当日のレース一覧（キーのみ）を取得する。
        /// 予想生成でも同じ列挙が必要なため、実装は RaceDiscovery に共通化している。</summary>
        private List<RaceKey> DiscoverTodaysRaceKeys(DateTime targetDate)
            => RaceDiscovery.ForDate(_source, targetDate);

        private RaceResult GetOrCreate(RaceKey key)
        {
            var slug = key.AsSlug();
            if (!_buffers.TryGetValue(slug, out var result))
            {
                result = new RaceResult { Key = key };
                _buffers[slug] = result;
            }
            return result;
        }
    }
}
