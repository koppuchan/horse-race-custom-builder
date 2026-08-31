using System;
using System.Collections.Generic;
using System.Linq;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 当日の出走馬について6ファクターを算出し、WordPressのhrc_factorsへ送信する。
    ///
    /// WordPress側の race_card（RaceCardService経由で既に送信済み）には血統登録番号(KettoNum)が
    /// 含まれていない（表示に不要なため元々持たせていない）。そのため、ここではWordPress経由ではなく
    /// RaceCardServiceと同じ"RACE"データ種別を当日分だけ直接開き、SEレコードからKettoNumを
    /// 取り出してHistoricalDataStoreと突き合わせる。
    ///
    /// FactorScoringServiceが返すスコアはローカルSQLiteの蓄積状況に依存する。血統(⑤)がまだ
    /// 0件（BLOD取得の問題が未解決）の間は、⑤は全馬nullのまま送信される
    /// （nullのフィールドはJSON自体に含めない。WordPress側は欠けたキーとして扱える）。
    /// </summary>
    public class FactorPublishService
    {
        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;
        private readonly FactorScoringService _scoring;

        // RaceCardServiceと同じ理由（出馬表は開催日より前に公開されるため）。
        private const string EarlyAnchorFromTime = "19860101000000";

        public FactorPublishService(IRaceDataSource source, WordPressClient wp, FactorScoringService scoring)
        {
            _source = source;
            _wp = wp;
            _scoring = scoring;
        }

        public void RunForToday(DateTime targetDate)
        {
            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 該当データなし（開催が無い等）。");
                return;
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} RACE Open failed: {open.ReturnCode}");
            }

            // レースキー(slug)ごとの距離・トラック種別。RA到着時に埋め、SE処理時に参照する
            // （BackfillServiceのBackfillRaceEntriesと同じ、RA→SEの到着順を前提にした組み方）。
            var raceInfoByKey = new Dictionary<string, (int Distance, string TrackSurfaceCode)>();
            var entriesByRace = new Dictionary<string, List<(int Umaban, FactorScoringInput Input)>>();
            var raceKeys = new Dictionary<string, RaceKey>();

            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { System.Threading.Thread.Sleep(500); continue; }
                    if (size < 0)
                        throw new InvalidOperationException($"{_source.SourceName} Read failed: {size}");

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);

                    if (typeId == "RA")
                    {
                        var ra = new JV_RA_RACE();
                        ra.SetDataB(ref buffer);
                        var (raceKey, ok) = TryBuildRaceKey(ra.id.Year, ra.id.MonthDay, ra.id.JyoCD, ra.id.RaceNum, targetDate);
                        if (!ok) continue;
                        raceInfoByKey[raceKey.AsSlug()] = (SafeInt(ra.Kyori), Trim(ra.TrackCD));
                    }
                    else if (typeId == "SE")
                    {
                        var se = new JV_SE_RACE_UMA();
                        se.SetDataB(ref buffer);
                        var (raceKey, ok) = TryBuildRaceKey(se.id.Year, se.id.MonthDay, se.id.JyoCD, se.id.RaceNum, targetDate);
                        if (!ok) continue;

                        var slug = raceKey.AsSlug();
                        if (!raceInfoByKey.TryGetValue(slug, out var raceInfo))
                            continue; // 距離が分からない馬は集計不能なのでスキップ。

                        var kettoNum = Trim(se.KettoNum);
                        var umaban = SafeInt(se.Umaban);
                        if (string.IsNullOrEmpty(kettoNum) || umaban <= 0) continue;

                        var input = new FactorScoringInput
                        {
                            KettoNum = kettoNum,
                            TrackCode = raceKey.TrackCode,
                            Distance = raceInfo.Distance,
                            TrackSurfaceCode = raceInfo.TrackSurfaceCode,
                            Waku = SafeInt(se.Wakuban),
                            JockeyCode = Trim(se.KisyuCode),
                        };

                        if (!entriesByRace.TryGetValue(slug, out var list))
                        {
                            list = new List<(int, FactorScoringInput)>();
                            entriesByRace[slug] = list;
                            raceKeys[slug] = raceKey;
                        }
                        list.Add((umaban, input));
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            int published = 0, skipped = 0, failed = 0;
            foreach (var slug in entriesByRace.Keys)
            {
                // スコア計算自体も1レース単位で保護する。以前はここが素通しで、
                // FactorScoringService内の未知の例外（実機で発生: 特定コース条件の
                // 母集団が0件になりSUM集計がNULLを返してInvalidCastExceptionになった
                // ケース）が起きると、その日のこのソースの残り全レースが処理されずに
                // 巻き添えで終了していた（中京が丸ごと・新潟の一部が欠けた原因）。
                // 直接の原因はFactorScoringService側で個別に直したが、同じ壊れ方を
                // 二度としないよう、ここでもレース単位に隔離しておく。
                var scores = new Dictionary<int, FactorScores>();
                try
                {
                    foreach (var (umaban, input) in entriesByRace[slug])
                        scores[umaban] = _scoring.Compute(input);
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {slug} 6ファクターの計算に失敗（このレースのみスキップして続行）: {ex.Message}");
                    continue;
                }

                // 1レースの送信失敗で、残りのレースまで巻き添えにしない。
                // 既存システムの朝一バッチはここで例外を上まで投げてしまい、WordPressが
                // 503を1回返しただけで、その後の全レースの出走表が作られないまま
                // 異常終了していた（笠松が丸ごと欠けた原因）。同じ壊れ方をしないよう、
                // レース単位で捕まえて次へ進む。WordPressClient側でも再送はするので、
                // ここまで来るのは再送しても駄目だった場合だけ。
                bool applied;
                try
                {
                    applied = _wp.UpsertFactorsAsync(raceKeys[slug], scores).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {slug} 6ファクターの反映に失敗（このレースのみスキップして続行）: {ex.Message}");
                    continue;
                }

                if (!applied)
                {
                    // 出走表がまだWordPressに無いレース。ここで投稿を作ると馬名の無い
                    // 空のレースができてしまうため送信しない（WordPressClient側のコメント参照）。
                    skipped++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {slug} 出走表がWordPressにまだ無いため6ファクターの反映を見送りました" +
                        "（朝一バッチで出走表が作られた後、次回のscore実行で反映されます）");
                    continue;
                }

                published++;
                var withAny = scores.Count(kv => HasAnyScore(kv.Value));
                Console.WriteLine(
                    $"[{_source.SourceName}] {slug} 6ファクター反映完了: {scores.Count}頭中{withAny}頭に" +
                    "何らかのスコアあり（血統・調教等、母集団不足やデータ未取得のものはnullのまま）");
            }

            var notes = new List<string>();
            if (skipped > 0) notes.Add($"{skipped}レースは出走表未作成のため見送り");
            if (failed > 0) notes.Add($"{failed}レースは送信失敗");
            var note = notes.Count > 0 ? $"（{string.Join("、", notes)}）" : "";
            Console.WriteLine(
                $"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 6ファクター算出 {published}レース 完了{note}");

            // 送信失敗があった日は、次回のscore実行で拾い直せるよう終了コードに残す。
            if (failed > 0)
                throw new InvalidOperationException(
                    $"{failed}レースの反映に失敗しました（他のレースは反映済み）。次回のscore実行で再試行されます。");
        }

        private static bool HasAnyScore(FactorScores s) =>
            s.ParamBias.HasValue || s.ParamPace.HasValue || s.ParamAgariQ.HasValue ||
            s.ParamJockeyRoi.HasValue || s.ParamPedigreeFit.HasValue || s.ParamTrainingAcc.HasValue;

        private static (RaceKey Key, bool Ok) TryBuildRaceKey(string year, string monthDay, string jyoCd, string raceNum, DateTime targetDate)
        {
            var m = Trim(monthDay);
            var month = m.Length >= 2 ? SafeInt(m.Substring(0, 2)) : 0;
            var day = m.Length >= 4 ? SafeInt(m.Substring(2, 2)) : 0;
            var y = SafeInt(year);

            DateTime date;
            try
            {
                date = y > 0 && month > 0 && day > 0 ? new DateTime(y, month, day) : DateTime.MinValue;
            }
            catch (ArgumentOutOfRangeException)
            {
                return (null, false);
            }
            if (date.Date != targetDate.Date) return (null, false);

            return (new RaceKey { TrackCode = Trim(jyoCd), RaceDate = date, RaceNumber = SafeInt(raceNum) }, true);
        }

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
