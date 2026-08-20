using System;
using System.Linq;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// 6ファクター算出用に新規追加したレコード種別のパーサー。
    /// JvRecordParser.cs と同様、バイト位置はすべてJVData_Struct.csのSetDataB()実装に従う
    /// （独自推測なし）。既存のJvRecordParser.csを肥大化させないよう、6ファクター専用として分離。
    /// </summary>
    public static class JvFactorRecordParser
    {
        /// <summary>"HC"レコード（坂路調教）をパースする。</summary>
        public static TrainingLapEntry ParseSlopeTraining(string rawRecord)
        {
            var hc = new JV_HC_HANRO();
            hc.SetDataB(ref rawRecord);

            // ラップは「ゴールに近い方から」の並び: LapTime1(200-0M) が最終1F相当。
            var laps = new[]
            {
                ParseSeconds(hc.LapTime1),
                ParseSeconds(hc.LapTime2),
                ParseSeconds(hc.LapTime3),
                ParseSeconds(hc.LapTime4),
            };

            return new TrainingLapEntry
            {
                KettoNum = Trim(hc.KettoNum),
                ChokyoDate = ParseYmd(hc.ChokyoDate),
                Course = TrainingCourse.Slope,
                TresenKubun = Trim(hc.TresenKubun),
                LapTimesSeconds = laps,
            };
        }

        /// <summary>"WC"レコード（ウッドチップ調教）をパースする。</summary>
        public static TrainingLapEntry ParseWoodChipTraining(string rawRecord)
        {
            var wc = new JV_WC_WOOD();
            wc.SetDataB(ref rawRecord);

            var laps = new[]
            {
                ParseSeconds(wc.LapTime1),
                ParseSeconds(wc.LapTime2),
                ParseSeconds(wc.LapTime3),
                ParseSeconds(wc.LapTime4),
                ParseSeconds(wc.LapTime5),
                ParseSeconds(wc.LapTime6),
                ParseSeconds(wc.LapTime7),
                ParseSeconds(wc.LapTime8),
                ParseSeconds(wc.LapTime9),
                ParseSeconds(wc.LapTime10),
            };

            return new TrainingLapEntry
            {
                KettoNum = Trim(wc.KettoNum),
                ChokyoDate = ParseYmd(wc.ChokyoDate),
                Course = TrainingCourse.WoodChip,
                TresenKubun = Trim(wc.TresenKubun),
                LapTimesSeconds = laps,
            };
        }

        /// <summary>"SK"レコード（産駒マスタ）から父・母父の繁殖登録番号を取り出す。
        ///
        /// HansyokuNum[14]の並び順（0:父 1:母 2:父父 3:父母 4:母父 5:母母...）は
        /// JRA-VAN公式の一次仕様書内では記載箇所を特定できなかった（複数の公開ツール解説では
        /// 一致）。血統適性ロジックを実装する前に、既知の馬（例:近年の有名馬）のKettoNumで
        /// このパーサーの出力を実際の父・母父と突き合わせて必ず検算すること。
        /// ここが違うと「父」と「母父」を取り違えたまま集計してしまう。</summary>
        public static PedigreeLink ParseOffspringPedigree(string rawRecord)
        {
            var sk = new JV_SK_SANKU();
            sk.SetDataB(ref rawRecord);

            return new PedigreeLink
            {
                KettoNum = Trim(sk.KettoNum),
                SireHansyokuNum = sk.HansyokuNum != null && sk.HansyokuNum.Length > 0 ? Trim(sk.HansyokuNum[0]) : string.Empty,
                BroodmareSireHansyokuNum = sk.HansyokuNum != null && sk.HansyokuNum.Length > 4 ? Trim(sk.HansyokuNum[4]) : string.Empty,
                BirthDate = ParseYmd(sk.BirthDate),
            };
        }

        /// <summary>"HN"レコード（繁殖馬マスタ）から繁殖登録番号と馬名を取り出す。</summary>
        public static BroodstockName ParseBroodstockName(string rawRecord)
        {
            var hn = new JV_HN_HANSYOKU();
            hn.SetDataB(ref rawRecord);

            return new BroodstockName
            {
                HansyokuNum = Trim(hn.HansyokuNum),
                Bamei = Trim(hn.Bamei),
            };
        }

        /// <summary>4桁のラップタイム文字列（例:"125"=12.5秒、末尾1桁が小数第1位）を秒数に変換する。
        /// 空白・未計測（"----"等）はnullを返す。</summary>
        private static double? ParseSeconds(string raw)
        {
            var t = Trim(raw);
            if (t.Length == 0 || !t.All(char.IsDigit)) return null;
            if (!int.TryParse(t, out var v) || v == 0) return null;
            return v / 10.0;
        }

        private static DateTime ParseYmd(YMD ymd)
        {
            var y = SafeInt(ymd.Year);
            var m = SafeInt(ymd.Month);
            var d = SafeInt(ymd.Day);
            try
            {
                return y > 0 && m > 0 && d > 0 ? new DateTime(y, m, d) : DateTime.MinValue;
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
