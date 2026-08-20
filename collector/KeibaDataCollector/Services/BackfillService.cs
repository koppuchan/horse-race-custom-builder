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
    /// （RACE/SLOP/WOOD/BLOD）が実際に読めるかを軽い範囲で確認してから流すこと。
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

        /// <summary>レース系（RA/SE）の過去データを取り込む。
        /// RAレコードでレースごとの距離・トラック種別を先に押さえ、直後に続くSEレコードに
        /// 反映する想定（JV-Dataは通常レース単位でRA→SE→HRの順に流れる）。
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

            // レースキー(日付+場コード+R番号)ごとの距離・トラック種別。RA到着時に埋め、SE処理時に参照する。
            var raceInfoByKey = new Dictionary<string, (int Distance, string TrackSurfaceCode)>();

            int totalRecords = 0, seRecords = 0, raRecords = 0, missingRaInfo = 0;
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
                        raceInfoByKey[key] = (SafeInt(ra.Kyori), Trim(ra.TrackCD));
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
                            raceInfo = (0, string.Empty);
                        }

                        var entry = new HistoricalRaceEntry
                        {
                            KettoNum = Trim(se.KettoNum),
                            RaceDate = ParseRaceDate(se.id.Year, se.id.MonthDay),
                            TrackCode = Trim(se.id.JyoCD),
                            TrackSurfaceCode = raceInfo.TrackSurfaceCode,
                            Distance = raceInfo.Distance,
                            Waku = SafeInt(se.Wakuban),
                            Umaban = SafeInt(se.Umaban),
                            JockeyCode = Trim(se.KisyuCode),
                            TrainerCode = Trim(se.ChokyosiCode),
                            Chakujun = SafeInt(se.KakuteiJyuni),
                            TanshoOdds = SafeOddsTenths(se.Odds),
                            Agari3F = SafeOddsTenths(se.HaronTimeL3),
                            CornerPassage4 = null, // コーナー通過はRA側の配列。必要になれば別テーブルに分離する。
                        };

                        if (!string.IsNullOrEmpty(entry.KettoNum) && entry.RaceDate != DateTime.MinValue)
                            _store.UpsertRaceEntry(entry);
                    }

                    if (totalRecords % BatchSize == 0)
                    {
                        batch.Dispose();
                        batch = _store.BeginBatch();
                        Console.WriteLine($"[{_source.SourceName}] RACE進捗: {totalRecords}件処理（SE:{seRecords} RA:{raRecords}）");
                    }
                }
            }
            finally
            {
                batch.Dispose();
                _source.Close();
            }

            Console.WriteLine(
                $"[{_source.SourceName}] RACE取り込み完了: 全{totalRecords}件, SE={seRecords}, RA={raRecords}, " +
                $"RA情報未取得のままのSE={missingRaInfo}（RAより先にSEが来た/開催情報が別範囲だった等の可能性）");
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

        /// <summary>血統（BLOD dataspec, "SK"産駒マスタ・"HN"繁殖馬マスタ）を取り込む。</summary>
        public void BackfillPedigree()
        {
            // BackfillTrainingと同じ理由でSetup(3)を使う。実機確認で、Normal(1)だと
            // SK:8,302件（≒1年分の新規産駒登録数）しか取れず、1986年以降の全履歴には
            // 遠く及ばなかった。
            var open = _source.Open("BLOD", BackfillFromTime, DataOption.Setup);
            if (open.ReturnCode == -1)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] BLOD: 該当データなし（このソースでは提供されていない可能性）。");
                return;
            }
            if (open.ReturnCode < 0)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] BLOD Open失敗: {open.ReturnCode}（血統データのみスキップして続行）");
                return;
            }

            Console.WriteLine($"[{_source.SourceName}] BLOD 全履歴取得を開始します。");

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
                        var link = JvFactorRecordParser.ParseOffspringPedigree(buffer);
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
                        var name = JvFactorRecordParser.ParseBroodstockName(buffer);
                        if (!string.IsNullOrEmpty(name.HansyokuNum))
                            _store.UpsertBroodstockName(name);
                    }

                    if (totalRecords % BatchSize == 0)
                    {
                        batch.Dispose();
                        batch = _store.BeginBatch();
                        Console.WriteLine($"[{_source.SourceName}] BLOD進捗: {totalRecords}件処理（SK:{skCount} HN:{hnCount}）");
                    }
                }
            }
            finally
            {
                batch.Dispose();
                _source.Close();
            }

            var birthYearRange = minBirthYear.HasValue ? $" 産駒の生年範囲=[{minBirthYear}〜{maxBirthYear}]" : "";
            Console.WriteLine($"[{_source.SourceName}] BLOD取り込み完了: 全{totalRecords}件, SK={skCount}, HN={hnCount}{birthYearRange}");
        }

        private static string RaceInfoKey(string year, string monthDay, string jyoCd, string raceNum) =>
            $"{Trim(year)}-{Trim(monthDay)}-{Trim(jyoCd)}-{Trim(raceNum)}";

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
