using System;
using System.Collections.Generic;
using System.Linq;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>朝一バッチ: 当日の番組表（出走表）を取得しWordPressへ反映する。</summary>
    public class RaceCardService
    {
        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;

        public RaceCardService(IRaceDataSource source, WordPressClient wp)
        {
            _source = source;
            _wp = wp;
        }

        // option=2(今週データ)は「直近の未来のレースに関するデータ」に取得範囲をサーバー側で
        // 絞ってくれるため、fromtimeは実際の対象範囲決定には使われない（更新差分の再開用途）。
        // 出馬表は開催日より前（火・水曜等）に公開されるため、fromtimeを「今日0時」にすると
        // 「今日0時以降に新規提供されたデータ」しか拾えず、既に公開済みの当日レース分を
        // 取りこぼす。そのため十分に過去の固定値を指定し、option=2の範囲を丸ごと取得する。
        private const string EarlyAnchorFromTime = "19860101000000";

        public void RunMorningBatch(DateTime targetDate, string trackCode)
        {
            // dataspec "RACE" は番組表系レコードを想定。JV-Linkインターフェース仕様書のJVOpen
            // option早見表(option=2:今週データ)で dataspec="RACE" 指定可であることを確認済み。
            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                // JV-Linkインターフェース仕様書のコード表より: -1は「該当データ無し」であり異常ではない
                // （指定期間に開催が無い等）。JVCloseを呼んで正常終了する。
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 該当データなし（開催が無い等）。");
                return;
            }
            if (open.ReturnCode < 0)
            {
                // 例外を投げる前にもCloseを呼ぶ。呼ばずに抜けると、JV-Linkインターフェース
                // 仕様書の-202（前回のOpenに対してCloseが呼ばれていない）により、次回以降の
                // Open呼び出しがすべて失敗し続けてしまう（実機で確認済みの問題パターン）。
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} Open failed: {open.ReturnCode}");
            }

            var entriesByRace = new Dictionary<string, List<RaceCardEntry>>();
            var raceKeys = new Dictionary<string, RaceKey>();

            int totalRecords = 0;
            int seRecords = 0;
            var otherDates = new HashSet<string>();
            var typeCounts = new Dictionary<string, int>();

            // Open成功後は、読み込みループの途中で例外が発生した場合でも必ずCloseが
            // 呼ばれるようtry/finallyで保護する（上記と同じ-202連鎖を防ぐため）。
            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue; // ファイル切り替わり（正常、JVRead仕様書より）
                    if (size == -3)
                    {
                        // ファイルダウンロード中（JVRead仕様書より）。busyループにならないよう少し待つ。
                        System.Threading.Thread.Sleep(500);
                        continue;
                    }
                    if (size < 0)
                        throw new InvalidOperationException($"{_source.SourceName} Read failed: {size}");
                    totalRecords++;

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    typeCounts[typeId] = typeCounts.TryGetValue(typeId, out var c) ? c + 1 : 1;

                    if (typeId != "SE") continue;
                    seRecords++;

                    RaceKey raceKey;
                    RaceCardEntry entry;
                    try
                    {
                        (raceKey, entry) = JvRecordParser.ParseRaceCard(buffer);
                    }
                    catch (Exception ex)
                    {
                        // 1レコードのパース失敗で情報源全体を止めない。原因調査用に文字列長も残す。
                        Console.WriteLine(
                            $"[{_source.SourceName}] SEレコードのパース失敗（このレコードのみスキップ）: " +
                            $"生文字列長={buffer.Length}, エラー={ex.Message}");
                        continue;
                    }

                    // option=2は「今週データ」全体を返しうるため、朝一バッチの対象日以外は捨てる。
                    if (raceKey.RaceDate.Date != targetDate.Date)
                    {
                        otherDates.Add(raceKey.RaceDate.ToString("yyyy-MM-dd"));
                        continue;
                    }

                    var slug = raceKey.AsSlug();
                    if (!entriesByRace.TryGetValue(slug, out var list))
                    {
                        list = new List<RaceCardEntry>();
                        entriesByRace[slug] = list;
                        raceKeys[slug] = raceKey;
                    }
                    list.Add(entry);
                }
            }
            finally
            {
                _source.Close();
            }

            // 取得内容の内訳。データが届いているか／対象日の絞り込みが効いているかを
            // 運用時に確認できるよう、通常ログとして残す。
            var typeBreakdown = string.Join(", ", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"));
            Console.WriteLine(
                $"[{_source.SourceName}] 取得内訳: 全レコード{totalRecords}件, SEレコード{seRecords}件, " +
                $"対象日({targetDate:yyyy-MM-dd})以外の日付={string.Join(",", otherDates)}, " +
                $"種別内訳=[{typeBreakdown}]");

            int published = 0, failed = 0;
            foreach (var slug in entriesByRace.Keys)
            {
                var entries = entriesByRace[slug];
                entries.Sort((a, b) => a.Umaban.CompareTo(b.Umaban));

                // 1レースの送信失敗で、残りのレースまで巻き添えにしない
                // （PredictionServiceが以前から採っている方針に揃える）。
                //
                // 対策前は例外がそのまま上まで飛んでいたため、WordPressが1回でも
                // 503を返すと、そこから後ろのレースの出走表が一切作られないまま
                // バッチが異常終了していた。実際に発生し、船橋10R以降と笠松の
                // 全10レースの出走表が丸ごと欠けた（カスタムビルダー側では
                // 「馬名が表示されない」という形で表面化した）。
                // WordPressClient側でも一時的なエラーは再送するので、ここまで
                // 来るのは再送しても駄目だった場合だけ。
                try
                {
                    _wp.UpsertRaceCardAsync(raceKeys[slug], entries).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {slug} 出走表の反映に失敗（このレースのみスキップして続行）: {ex.Message}");
                    continue;
                }

                published++;
                Console.WriteLine($"[{_source.SourceName}] {slug} 出走表 {entries.Count}頭 反映完了");
            }

            var failedNote = failed > 0 ? $"（{failed}レースは送信失敗）" : "";
            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 出走表 {published}レース 反映完了{failedNote}");

            // 失敗があった場合は終了コードに残す（成功扱いで見逃すと、
            // 出走表が欠けたまま誰も気づかないため）。
            if (failed > 0)
                throw new InvalidOperationException(
                    $"{failed}レースの出走表反映に失敗しました（他のレースは反映済み）。");
        }
    }
}
