using System;
using System.Collections.Generic;
using KeibaDataCollector.Data;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 6ファクター算出のための過去データを一括取得し、ローカルSQLite（HistoricalDataStore）へ
    /// 蓄積する。DataOption.Setup（全履歴）でJVOpenするため、初回は非常に時間がかかる
    /// （中央+地方で数年分、件数は数十万〜数百万レコード規模になりうる）。
    ///
    /// 実行前に必ず"probe"コマンド（DataSpecProbeService）で、対象データ種別
    /// （RACE/SLOP/WOOD/BLOD/BLDN）が実際に読めるかを軽い範囲で確認してから流すこと。
    /// dataspec名やレコードの並び順の一部（血統の父・母父インデックス等）はコード内コメントの通り
    /// 未検証の前提を含むため、いきなり全件流す前に少量で検算するのが安全。
    /// </summary>
    public class BackfillService
    {
        private readonly IRaceDataSource _source;
        private readonly HistoricalDataStore _store;

        // お客様の要望（過去3年分あれば十分）に合わせて絞り込む。
        // 実機確認では option=Normal(1) は直近1年、option=Setup(3) は
        // dataspecの提供開始日（SLOP:2003年〜等）から全件、という挙動だった。
        // fromtimeがSetupモードの絞り込みに実際に効くかは未検証だが、開発者コミュニティには
        // 「option=3でも指定したfromtimeより古いデータは除外される」という報告がある。
        // 3年分に絞れば、1年で約55万件（坂路調教）だった実測値から、23年分(1986〜)の
        // 代わりに3年分（約165万件）程度に収まる想定。fromtimeが効かず全件返ってきた場合は、
        // 取り込み時にRaceDate等でフィルタする対応に切り替えること
        // （BackfillFromTimeが効いているかは、日付範囲ログで必ず確認する）。
        private static readonly string BackfillFromTime =
            DateTime.Today.AddYears(-3).ToString("yyyyMMddHHmmss");

        // この件数ごとにトランザクションをコミットし、進捗をログに出す。
        // 1件ずつコミットすると数百万件規模でfsyncがボトルネックになり現実的な時間で終わらない。
        private const int BatchSize = 5000;

        public BackfillService(IRaceDataSource source, HistoricalDataStore store)
        {
            _source = source;
            _store = store;
        }

        /// <summary>レース系（RA/SE/HR）の過去データを取り込む。
        /// RAレコードでレースごとの距離・トラック種別を先に押さえ、直後に続くSEレコードに
        /// 反映する想定（JV-Dataは通常レース単位でRA→SE→HRの順に流れる）。
        /// HR（払戻）は複勝払戻額だけを、対応するSE由来の行にUPDATEで反映する
        /// （④騎手コース回収率の「回収率」に払戻額が必要なため。単勝回収率はtansho_odds×
        /// 着順1から計算できるので、単勝払戻は別途持たなくてよい）。
        /// SEがRAより先に来た場合（並び順の想定外れ）は距離・トラック種別が空のまま保存され、
        /// 件数を最後にログへ出す（無言で欠落させない）。</summary>
        public void BackfillRaceEntries()
        {
            var open = _source.Open("RACE", BackfillFromTime, DataOption.Setup);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] RACE: 該当データなし。");
                return;
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                throw new InvalidOperationException($"{_source.SourceName} RACE Open failed: {open.ReturnCode}");
            }

            Console.WriteLine($"[{_source.SourceName}] RACE 全履歴取得を開始します（ダウンロード対象 {open.DownloadCount}ファイル）。");

            // レースキー(日付+場コード+R番号)ごとの距離・トラック種別・最も早いコーナーの通過順位
            // （先頭からの馬番配列。②テン速度・展開用）。RA到着時に埋め、SE処理時に参照する。
            var raceInfoByKey = new Dictionary<string, (int Distance, string TrackSurfaceCode, int[] EarliestCornerOrder)>();

            // HR（払戻）はここに溜めるだけにして、ストリーム読み込みが全部終わった後にまとめて
            // race_entriesへ反映する。理由: 実機診断で「HRはRA→SE→HRの順で来る」という前提が
            // 誤りだと判明した（同日内でHRがRA/SEより先に届くケースがあり、その場で
            // UPDATEしようとしても対象のrace_entries行がまだ存在せず0件更新のまま終わっていた。
            // 実際、最初の5000件処理時点でSE:0 RA:0 HR:264というログが出ていた）。
            // ストリーム全体を読み終えた後ならrace_entriesは確実に埋まっているため、
            // 順序に依存せずに反映できる。
            var pendingFukusho = new List<(DateTime RaceDate, string TrackCode, int RaceNumber, int Umaban, double Amount)>();

            int totalRecords = 0, seRecords = 0, raRecords = 0, hrRecords = 0, missingRaInfo = 0;
            int hrAnyPayoutEntries = 0, hrFukushoSeen = 0, hrFukushoUnparseable = 0, hrFukushoSampleLogged = 0;
            var batch = _store.BeginBatch();
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
                    totalRecords++;

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);

                    if (typeId == "RA")
                    {
                        raRecords++;
                        var ra = new JV_RA_RACE();
                        ra.SetDataB(ref buffer);
                        var key = RaceInfoKey(ra.id.Year, ra.id.MonthDay, ra.id.JyoCD, ra.id.RaceNum);
                        raceInfoByKey[key] = (SafeInt(ra.Kyori), Trim(ra.TrackCD), JvFactorRecordParser.ParseEarliestCornerOrder(ra));
                    }
                    else if (typeId == "SE")
                    {
                        seRecords++;
                        var se = new JV_SE_RACE_UMA();
                        se.SetDataB(ref buffer);

                        var key = RaceInfoKey(se.id.Year, se.id.MonthDay, se.id.JyoCD, se.id.RaceNum);
                        if (!raceInfoByKey.TryGetValue(key, out var raceInfo))
                        {
                            missingRaInfo++;
                            raceInfo = (0, string.Empty, Array.Empty<int>());
                        }

                        var umaban = SafeInt(se.Umaban);
                        var entry = new HistoricalRaceEntry
                        {
                            KettoNum = Trim(se.KettoNum),
                            RaceDate = ParseRaceDate(se.id.Year, se.id.MonthDay),
                            TrackCode = Trim(se.id.JyoCD),
                            RaceNumber = SafeInt(se.id.RaceNum),
                            TrackSurfaceCode = raceInfo.TrackSurfaceCode,
                            Distance = raceInfo.Distance,
                            Waku = SafeInt(se.Wakuban),
                            Umaban = umaban,
                            JockeyCode = Trim(se.KisyuCode),
                            TrainerCode = Trim(se.ChokyosiCode),
                            Chakujun = SafeInt(se.KakuteiJyuni),
                            TanshoOdds = SafeOddsTenths(se.Odds),
                            Agari3F = SafeOddsTenths(se.HaronTimeL3),
                            CornerPassage4 = null, // コーナー通過はRA側の配列。必要になれば別テーブルに分離する。
                            EarlyPositionRatio = ComputeEarlyPositionRatio(raceInfo.EarliestCornerOrder, umaban),
                        };

                        if (!string.IsNullOrEmpty(entry.KettoNum) && entry.RaceDate != DateTime.MinValue)
                            _store.UpsertRaceEntry(entry);
                    }
                    else if (typeId == "HR")
                    {
                        hrRecords++;
                        var hr = new JV_HR_PAY();
                        hr.SetDataB(ref buffer);

                        // 複勝払戻だけを反映する（単勝回収率はrace_entriesのtansho_odds×着順1から
                        // 計算できるため、複勝以外の券種は6ファクターの集計には不要）。
                        // ここではDBに書かず、pendingFukushoに積むだけ（上のコメント参照）。
                        var (payoutRaceKey, payouts) = JvRecordParser.ParsePayouts(buffer);
                        hrAnyPayoutEntries += payouts.Count;
                        foreach (var payout in payouts)
                        {
                            if (payout.TicketType != "複勝") continue;
                            hrFukushoSeen++;

                            if (hrFukushoSampleLogged < 5)
                            {
                                hrFukushoSampleLogged++;
                                Console.WriteLine(
                                    $"[{_source.SourceName}] 複勝払戻サンプル: race={payoutRaceKey.AsSlug()} " +
                                    $"Combination=\"{payout.Combination}\" Amount={payout.Amount} Ninki={payout.Ninki}");
                            }

                            if (!int.TryParse(payout.Combination, out var umaban) || umaban <= 0)
                            {
                                hrFukushoUnparseable++;
                                continue;
                            }

                            pendingFukusho.Add((payoutRaceKey.RaceDate, payoutRaceKey.TrackCode,
                                payoutRaceKey.RaceNumber, umaban, payout.Amount));
                        }
                    }

                    if (totalRecords % BatchSize == 0)
                    {
                        batch.Dispose();
                        batch = _store.BeginBatch();
                        Console.WriteLine($"[{_source.SourceName}] RACE進捗: {totalRecords}件処理（SE:{seRecords} RA:{raRecords} HR:{hrRecords}）");
                    }
                }
            }
            finally
            {
                batch.Dispose();
                _source.Close();
            }

            Console.WriteLine(
                $"[{_source.SourceName}] RACE取り込み完了: 全{totalRecords}件, SE={seRecords}, RA={raRecords}, HR={hrRecords}, " +
                $"RA情報未取得のままのSE={missingRaInfo}（RAより先にSEが来た/開催情報が別範囲だった等の可能性）");
            Console.WriteLine(
                $"[{_source.SourceName}] HR診断: HR内の全券種払戻エントリ数={hrAnyPayoutEntries}件, " +
                $"うち複勝エントリ={hrFukushoSeen}件, 複勝のうち馬番がパースできなかった数={hrFukushoUnparseable}件, " +
                $"反映待ちキュー件数={pendingFukusho.Count}件");

            // ストリームを読み終えたので、race_entriesは全件揃っている。ここでまとめて反映する。
            int fukushoUpdated = 0, fukushoNoMatch = 0, applied = 0;
            batch = _store.BeginBatch();
            try
            {
                foreach (var p in pendingFukusho)
                {
                    var affected = _store.UpdateFukushoPayout(p.RaceDate, p.TrackCode, p.RaceNumber, p.Umaban, p.Amount);
                    if (affected > 0) fukushoUpdated += affected; else fukushoNoMatch++;
                    applied++;

                    if (applied % BatchSize == 0)
                    {
                        batch.Dispose();
                        batch = _store.BeginBatch();
                        Console.WriteLine($"[{_source.SourceName}] 複勝払戻の反映: {applied}/{pendingFukusho.Count}件処理");
                    }
                }
            }
            finally
            {
                batch.Dispose();
            }

            Console.WriteLine(
                $"[{_source.SourceName}] 複勝払戻の反映完了: 実際に反映した行={fukushoUpdated}件, " +
                $"対象行が見つからなかった数={fukushoNoMatch}件（取消・除外馬等でrace_entries行自体が無い場合を含む）");
        }

        /// <summary>坂路調教（SLOP dataspec, "HC"レコード）を取り込む。</summary>
        public void BackfillSlopeTraining() => BackfillTraining("SLOP", "HC");

        /// <summary>ウッドチップ調教（WOOD dataspec, "WC"レコード）を取り込む。</summary>
        public void BackfillWoodChipTraining() => BackfillTraining("WOOD", "WC");

        private void BackfillTraining(string dataSpec, string expectedTypeId)
        {
            // 実機確認: DataOption.Normal(1)は「有効なoption」ではあるものの、
            // 実際には直近およそ1年分しか返さなかった（日付範囲ログで確認）。
            // 全履歴（SLOPは2003年以降、WOODは2021年以降）を取るにはSetup(3)が必要。
            var open = _source.Open(dataSpec, BackfillFromTime, DataOption.Setup);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {dataSpec}: 該当データなし（このソースでは提供されていない可能性）。");
                return;
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {dataSpec} Open失敗: {open.ReturnCode}（このデータ種別のみスキップして続行）");
                return;
            }

            Console.WriteLine($"[{_source.SourceName}] {dataSpec} 全履歴取得を開始します。");

            int totalRecords = 0, matched = 0;
            DateTime? minDate = null, maxDate = null;
            var batch = _store.BeginBatch();
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
                    totalRecords++;

                    if (JvRecordParser.GetRecordTypeId(buffer) != expectedTypeId) continue;
                    matched++;

                    var entry = expectedTypeId == "HC"
                        ? JvFactorRecordParser.ParseSlopeTraining(buffer)
                        : JvFactorRecordParser.ParseWoodChipTraining(buffer);

                    if (!string.IsNullOrEmpty(entry.KettoNum) && entry.ChokyoDate != DateTime.MinValue)
                    {
                        _store.UpsertTrainingLap(entry);
                        if (!minDate.HasValue || entry.ChokyoDate < minDate) minDate = entry.ChokyoDate;
                        if (!maxDate.HasValue || entry.ChokyoDate > maxDate) maxDate = entry.ChokyoDate;
                    }

                    if (totalRecords % BatchSize == 0)
                    {
                        batch.Dispose();
                        batch = _store.BeginBatch();
                        Console.WriteLine($"[{_source.SourceName}] {dataSpec}進捗: {totalRecords}件処理（{expectedTypeId}一致:{matched}）");
                    }
                }
            }
            finally
            {
                batch.Dispose();
                _source.Close();
            }

            var dateRange = minDate.HasValue ? $" 日付範囲=[{minDate:yyyy-MM-dd}〜{maxDate:yyyy-MM-dd}]" : "";
            Console.WriteLine($"[{_source.SourceName}] {dataSpec}取り込み完了: 全{totalRecords}件中{expectedTypeId}={matched}件{dateRange}");
        }

        // BLODの提供範囲（1986年以降〜2023-08-08より前）を漏れなく取るためのfromtime。
        // BackfillFromTime（実行日の3年前）を使うと、現在出走している馬の大半が
        // それより前に生まれている（＝血統登録済みである）ため血統が取れない
        // （実機診断: BLDNのみ backfill した結果、596頭中32頭＝約5%しか血統が付かなかった。
        // 残り95%は2023-08-08より前に登録された馬で、BLOD側にしかデータが無かった）。
        private const string PedigreeLegacyFromTime = "19860101000000";

        /// <summary>血統（"SK"産駒マスタ・"HN"繁殖馬マスタ）を取り込む。
        ///
        /// dataspecが2つに分かれている理由（JRA-VAN公式JV-Data仕様書 Ver.4.9.0.1
        /// 「データ種別一覧」シート・「変更履歴」シートで確認済み）:
        /// 「蓄積系ソフト用 血統情報」は、2023年8月8日の18.繁殖馬マスタ項目拡張
        /// （繁殖登録番号・父馬繁殖登録番号・母馬繁殖登録番号が8バイト→10バイト、
        /// 19.産駒マスタの生産者コードも6バイト→8バイト、3代血統繁殖登録番号も8→10バイト）
        /// を境に、`BLOD`（それ以前・旧8バイト形式のデータのみ）と`BLDN`（それ以降・
        /// 新10バイト形式のデータのみ）に分かれている。「1986年以降の繁殖馬情報」という
        /// 提供範囲は両方合わせて初めて成立する。
        ///
        /// 当初BLDNだけをBackfillFromTime（実行日の3年前）でSetup Openしていたが、
        /// これだと2023-08-08以降に登録された（＝生まれの遅い）馬の血統しか取れず、
        /// 実機で596頭中32頭（約5%）しか血統適性・妙味のスコアが付かなかった。
        /// 現在出走する馬の大半は2023-08-08より前に生まれている＝血統登録もそれより前のため、
        /// BLOD側を別途、広い範囲（1986年以降＝PedigreeLegacyFromTime）で取り込む必要がある。
        /// BLODは旧8バイト形式のため、新形式向けのJV_HN_HANSYOKU/JV_SK_SANKUではなく、
        /// 専用の旧形式構造体（JV_HN_HANSYOKU_OLD/JV_SK_SANKU_OLD、JVData_Struct.cs）で
        /// パースする（バイト位置がずれるフィールドを混同しないため）。</summary>
        public void BackfillPedigree()
        {
            BackfillPedigreeSpec("BLDN", BackfillFromTime, isLegacyFormat: false);
            BackfillPedigreeSpec("BLOD", PedigreeLegacyFromTime, isLegacyFormat: true);
        }

        private void BackfillPedigreeSpec(string dataSpec, string fromTime, bool isLegacyFormat)
        {
            // BackfillTrainingと同じ理由でSetup(3)を使う。
            var open = _source.Open(dataSpec, fromTime, DataOption.Setup);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {dataSpec}: 該当データなし（このソースでは提供されていない可能性）。");
                return;
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] {dataSpec} Open失敗: {open.ReturnCode}（血統データのみスキップして続行）");
                return;
            }

            Console.WriteLine($"[{_source.SourceName}] {dataSpec} 全履歴取得を開始します（{(isLegacyFormat ? "旧8バイト形式" : "新10バイト形式")}）。");

            int totalRecords = 0, skCount = 0, hnCount = 0;
            int? minBirthYear = null, maxBirthYear = null;
            var batch = _store.BeginBatch();
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
                    totalRecords++;

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    if (typeId == "SK")
                    {
                        skCount++;
                        var link = isLegacyFormat
                            ? JvFactorRecordParser.ParseOffspringPedigreeLegacy(buffer)
                            : JvFactorRecordParser.ParseOffspringPedigree(buffer);
                        if (!string.IsNullOrEmpty(link.KettoNum))
                            _store.UpsertPedigreeLink(link);

                        if (link.BirthDate != DateTime.MinValue)
                        {
                            var y = link.BirthDate.Year;
                            if (!minBirthYear.HasValue || y < minBirthYear) minBirthYear = y;
                            if (!maxBirthYear.HasValue || y > maxBirthYear) maxBirthYear = y;
                        }
                    }
                    else if (typeId == "HN")
                    {
                        hnCount++;
                        var name = isLegacyFormat
                            ? JvFactorRecordParser.ParseBroodstockNameLegacy(buffer)
                            : JvFactorRecordParser.ParseBroodstockName(buffer);
                        if (!string.IsNullOrEmpty(name.HansyokuNum))
                            _store.UpsertBroodstockName(name);
                    }

                    if (totalRecords % BatchSize == 0)
                    {
                        batch.Dispose();
                        batch = _store.BeginBatch();
                        Console.WriteLine($"[{_source.SourceName}] {dataSpec}進捗: {totalRecords}件処理（SK:{skCount} HN:{hnCount}）");
                    }
                }
            }
            finally
            {
                batch.Dispose();
                _source.Close();
            }

            var birthYearRange = minBirthYear.HasValue ? $" 産駒の生年範囲=[{minBirthYear}〜{maxBirthYear}]" : "";
            Console.WriteLine($"[{_source.SourceName}] {dataSpec}取り込み完了: 全{totalRecords}件, SK={skCount}, HN={hnCount}{birthYearRange}");
        }

        private static string RaceInfoKey(string year, string monthDay, string jyoCd, string raceNum) =>
            $"{Trim(year)}-{Trim(monthDay)}-{Trim(jyoCd)}-{Trim(raceNum)}";

        /// <summary>最も早いコーナーの通過順位配列における、この馬番の順位を0〜1に正規化する
        /// （0=先頭で通過、1=最後尾で通過）。配列に馬番が無い、または頭数が1頭以下なら
        /// null（②テン速度・展開はこの馬について算出不能として扱う）。</summary>
        private static double? ComputeEarlyPositionRatio(int[] earliestCornerOrder, int umaban)
        {
            if (earliestCornerOrder == null || earliestCornerOrder.Length <= 1 || umaban <= 0) return null;
            var index = Array.IndexOf(earliestCornerOrder, umaban);
            if (index < 0) return null;
            return (double)index / (earliestCornerOrder.Length - 1);
        }

        private static DateTime ParseRaceDate(string year, string monthDay)
        {
            var y = SafeInt(year);
            var md = Trim(monthDay);
            var m = md.Length >= 2 ? SafeInt(md.Substring(0, 2)) : 0;
            var d = md.Length >= 4 ? SafeInt(md.Substring(2, 2)) : 0;
            try
            {
                return y > 0 && m > 0 && d > 0 ? new DateTime(y, m, d) : DateTime.MinValue;
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        private static double? SafeOddsTenths(string s)
        {
            var t = Trim(s);
            if (t.Length == 0 || !int.TryParse(t, out var v) || v == 0) return null;
            return v / 10.0;
        }

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
