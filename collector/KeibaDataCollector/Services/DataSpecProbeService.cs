using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 調査用モード。あるデータ源が、どのデータ種別で何を返してくるかを実際に叩いて確認する。
    ///
    /// 経緯: 地方競馬（UmaConn）の結果で単勝オッズ・単勝人気順・後3ハロンが常に初期値
    /// （0000 / 00 / 000）だった。バイト位置のズレではないこと（同レコードの着差コード=343が
    /// 正しく読める）は確認済みなので、残る可能性は「その項目をSEレコードに載せていない」。
    /// 一方、地方競馬DATAの公式案内には「オッズデータと票数データは2010年2月以降」提供と
    /// あるため、オッズは別のデータ種別（速報オッズ）で配信されている可能性が高い。
    /// 推測で実装せず、実際に取得できるかをここで確かめる。
    /// </summary>
    public class DataSpecProbeService
    {
        private const string EarlyAnchorFromTime = "19860101000000";

        // JV-Data仕様書「（２）速報系データ」より。
        private static readonly (string Spec, string Name)[] RealtimeSpecsToProbe =
        {
            ("0B31", "速報オッズ（単複枠）"),
            ("0B30", "速報オッズ（全賭式）"),
            ("0B12", "速報レース情報（成績確定後）"),
        };

        private readonly IRaceDataSource _source;

        public DataSpecProbeService(IRaceDataSource source)
        {
            _source = source;
        }

        /// <param name="raceKeySlug">
        /// "20260811-46-1R" 形式。指定するとそのレースだけを調べる。
        /// 省略すると当日の最初のレースを使う。
        ///
        /// 名指しできるようにした理由: 競馬場によってオッズの配信時刻が違い、
        /// 「取得できていない競馬場のレース」を狙って調べる必要があるため。
        /// 最初のレースだけでは、既にオッズが出ている競馬場を引いてしまい何も分からない。
        /// </param>
        public void Run(DateTime targetDate, string raceKeySlug = null)
        {
            var raceKey = string.IsNullOrWhiteSpace(raceKeySlug)
                ? FindFirstRaceOfDay(targetDate)
                : ParseSlug(raceKeySlug);

            if (raceKey == null)
            {
                Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} の対象レースが見つかりませんでした。");
                return;
            }

            Console.WriteLine($"[{_source.SourceName}] 調査対象レース: {raceKey.AsSlug()} (key={raceKey.AsJvRealtimeKey()})");

            foreach (var (spec, name) in RealtimeSpecsToProbe)
                ProbeRealtimeSpec(raceKey, spec, name);

            foreach (var (spec, name) in SetupSpecsToProbe)
                ProbeSetupSpec(spec, name);
        }

        // 6ファクター用に追加。全履歴セットアップ(DataOption.Setup)は重いため、
        // まず「今週データ」程度の軽い範囲でdataspec名とレコード種別が正しいかだけ確認する。
        // ここで0件/rc<0になった場合、全履歴バックフィルを流しても同じ結果になるだけなので、
        // 先にこちらで気づけるようにしている。
        private static readonly (string Spec, string Name)[] SetupSpecsToProbe =
        {
            ("SLOP", "坂路調教（HCレコード）"),
            ("WOOD", "ウッドチップ調教（WCレコード）"),
            ("BLDN", "血統：産駒マスタ(SK)・繁殖馬マスタ(HN)"),
        };

        private void ProbeSetupSpec(string dataSpec, string specName)
        {
            var open = _source.Open(dataSpec, EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode < 0)
            {
                _source.Close();
                Console.WriteLine(
                    $"[{_source.SourceName}] {dataSpec}({specName}): Open失敗 rc={open.ReturnCode}" +
                    (open.ReturnCode == -1 ? "（該当データ無し＝dataspec名が違うか、このソースでは非提供の可能性）" : ""));
                return;
            }

            var typeCounts = new Dictionary<string, int>();
            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { Thread.Sleep(500); continue; }
                    if (size < 0)
                    {
                        Console.WriteLine($"[{_source.SourceName}] {dataSpec}: Read失敗 {size}");
                        break;
                    }

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    typeCounts[typeId] = typeCounts.TryGetValue(typeId, out var c) ? c + 1 : 1;
                }
            }
            finally
            {
                _source.Close();
            }

            var breakdown = typeCounts.Count > 0
                ? string.Join(", ", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))
                : "（レコードなし。この期間・このソースにはデータが無いだけの可能性もあるため即NGとは限らない）";
            Console.WriteLine($"[{_source.SourceName}] {dataSpec}({specName}): rc=0 レコード種別=[{breakdown}]");
        }

        private void ProbeRealtimeSpec(RaceKey raceKey, string dataSpec, string specName)
        {
            int rc = _source.OpenRealtime(dataSpec, raceKey.AsJvRealtimeKey());
            if (rc != 0)
            {
                _source.Close();
                // -1は「該当データ無し」。それ以外はエラーコード（仕様書のコード表参照）。
                Console.WriteLine(
                    $"[{_source.SourceName}] {dataSpec}({specName}): 取得不可 rc={rc}" +
                    (rc == -1 ? "（該当データ無し＝この種別では提供されていない）" : ""));
                return;
            }

            var typeCounts = new Dictionary<string, int>();
            var oddsSamples = new List<string>();

            try
            {
                while (true)
                {
                    int size = _source.Read(out var buffer, out _);
                    if (size == 0) break;
                    if (size == -1) continue;
                    if (size == -3) { Thread.Sleep(500); continue; }
                    if (size < 0)
                    {
                        Console.WriteLine($"[{_source.SourceName}] {dataSpec}: Read失敗 {size}");
                        break;
                    }

                    var typeId = JvRecordParser.GetRecordTypeId(buffer);
                    typeCounts[typeId] = typeCounts.TryGetValue(typeId, out var c) ? c + 1 : 1;

                    // O1(単複枠オッズ)なら、実際に単勝オッズ・人気順が入っているかを見る。
                    if (typeId == "O1" && oddsSamples.Count == 0)
                        oddsSamples.AddRange(DescribeTansyoOdds(buffer));
                }
            }
            finally
            {
                _source.Close();
            }

            var breakdown = typeCounts.Count > 0
                ? string.Join(", ", typeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))
                : "（レコードなし）";
            Console.WriteLine($"[{_source.SourceName}] {dataSpec}({specName}): rc=0 レコード種別=[{breakdown}]");

            foreach (var line in oddsSamples)
                Console.WriteLine($"    {line}");
        }

        /// <summary>O1レコードから単勝オッズ・人気順を先頭数頭ぶん取り出して文字列化する。</summary>
        private static List<string> DescribeTansyoOdds(string rawRecord)
        {
            var lines = new List<string>();

            var o1 = new JV_O1_ODDS_TANFUKUWAKU();
            try
            {
                o1.SetDataB(ref rawRecord);
            }
            catch (Exception ex)
            {
                lines.Add($"O1パース失敗: {ex.Message}");
                return lines;
            }

            foreach (var t in o1.OddsTansyoInfo)
            {
                if (string.IsNullOrWhiteSpace(t.Umaban) || t.Umaban.Trim() == "00") continue;
                lines.Add($"単勝オッズ: 馬番=[{t.Umaban}] オッズ=[{t.Odds}] 人気順=[{t.Ninki}]");
                if (lines.Count >= 5) return lines;
            }

            if (lines.Count == 0)
                lines.Add("単勝オッズ: 有効な馬番が1件も入っていません（未提供の可能性）");

            return lines;
        }

        /// <summary>当日のレースを1件だけ見つける（RAレコードから）。</summary>
        /// <summary>"20260811-46-1R" を RaceKey に戻す。形式が違えば null。</summary>
        private static RaceKey ParseSlug(string slug)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                slug.Trim(), @"^(\d{8})-(\w+)-(\d+)R$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                Console.WriteLine($"レースキーの形式が不正です: {slug}（例: 20260811-46-1R）");
                return null;
            }
            return new RaceKey
            {
                RaceDate = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMdd", null),
                TrackCode = m.Groups[2].Value,
                RaceNumber = int.Parse(m.Groups[3].Value),
            };
        }

        private RaceKey FindFirstRaceOfDay(DateTime targetDate)
        {
            var open = _source.Open("RACE", EarlyAnchorFromTime, DataOption.ThisWeekAndToday);
            if (open.ReturnCode < 0)
            {
                _source.Close();
                Console.WriteLine($"[{_source.SourceName}] レース一覧の取得に失敗: {open.ReturnCode}");
                return null;
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

                    if (JvRecordParser.GetRecordTypeId(buffer) != "RA") continue;

                    try
                    {
                        var (raceKey, _) = JvRecordParser.ParseCornerPassage(buffer);
                        if (raceKey.RaceDate.Date == targetDate.Date)
                            return raceKey;
                    }
                    catch
                    {
                        // 調査用途なので、壊れたレコードは黙って読み飛ばす。
                    }
                }
            }
            finally
            {
                _source.Close();
            }

            return null;
        }
    }
}
