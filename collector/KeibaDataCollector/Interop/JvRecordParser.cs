using System;
using System.Collections.Generic;
using System.Linq;
using KeibaDataCollector.Models;
using static KeibaDataCollector.Interop.JvDataSdk.JVData_Struct;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JVRead/UCRead で得られる固定長レコード文字列を、レコード種別ID（先頭2バイト。
    /// "RA"=レース詳細, "SE"=馬毎レース情報, "HR"=払戻 等）で判別してパースする層。
    ///
    /// バイト位置の裏付けは JRA-VAN公式SDK付属の JVData_Struct.cs
    /// （Interop/JvDataSdk/JVData_Struct.cs、実機のSDKダウンロードから取得・配置済み）の
    /// SetDataB() 実装をそのまま利用する。したがって着順・払戻金額などのオフセットを
    /// 独自に推測している箇所はない。
    /// </summary>
    public static class JvRecordParser
    {
        public static string GetRecordTypeId(string rawRecord)
        {
            if (string.IsNullOrEmpty(rawRecord) || rawRecord.Length < 2)
                return string.Empty;
            return rawRecord.Substring(0, 2);
        }

        /// <summary>レコード共通の「データ区分」（3バイト目、JVData_Struct.csのRECORD_ID.DataKubun相当）を返す。
        /// RA/SEの速報成績では 3:3着まで確定 → 4:5着まで確定 → 5:全馬着順確定 →
        /// 6:全馬着順+コーナ通過順 → 7:成績(月曜) と段階的に更新されるため、
        /// どの段階まで確定したかの判定に使う。</summary>
        public static string GetDataKubun(string rawRecord)
        {
            if (string.IsNullOrEmpty(rawRecord) || rawRecord.Length < 3)
                return string.Empty;
            return rawRecord.Substring(2, 1);
        }

        /// <summary>"SE"レコード（馬毎レース情報）を朝一の出走表項目としてパースする。</summary>
        public static (RaceKey Key, RaceCardEntry Entry) ParseRaceCard(string rawRecord)
        {
            var se = new JV_SE_RACE_UMA();
            se.SetDataB(ref rawRecord);

            var entry = new RaceCardEntry
            {
                Waku = SafeInt(se.Wakuban),
                Umaban = SafeInt(se.Umaban),
                HorseName = Trim(se.Bamei),
                SexAge = FormatSexAge(se.SexCD, se.Barei),
                Kinryo = SafeTenths(se.Futan),
                JockeyName = Trim(se.KisyuRyakusyo),
                TrainerName = Trim(se.ChokyosiRyakusyo),
            };

            return (ExtractRaceKey(se.id), entry);
        }

        /// <summary>"SE"レコード（馬毎レース情報、確定後）を着順結果としてパースする。
        ///
        /// 地方競馬（UmaConn）では、単勝オッズ・単勝人気順・後3(4)ハロンタイムが
        /// 常に仕様書の初期値で返る（バイト位置のズレではなく、SEレコードに載っていない）。
        /// このうちオッズと人気順は速報オッズ("0B31")から取得できるため
        /// RaceResultService側で補完する。後3ハロンは他種別にも無いため取得不可。
        /// </summary>
        public static (RaceKey Key, RaceResultEntry Entry) ParseRaceResult(string rawRecord)
        {
            var se = new JV_SE_RACE_UMA();
            se.SetDataB(ref rawRecord);

            var entry = new RaceResultEntry
            {
                Chakujun = SafeInt(se.KakuteiJyuni),
                Waku = SafeInt(se.Wakuban),
                Umaban = SafeInt(se.Umaban),
                HorseName = Trim(se.Bamei),
                SexAge = FormatSexAge(se.SexCD, se.Barei),
                Kinryo = SafeTenths(se.Futan),
                JockeyName = Trim(se.KisyuRyakusyo),
                Time = FormatTime(se.Time),
                ChakusaText = FormatChakusa(se.ChakusaCD),
                Ninki = SafeInt(se.Ninki),
                TanshoOdds = SafeTenths(se.Odds),
                Ushi3F = SafeTenths(se.HaronTimeL3),
                TrainerName = Trim(se.ChokyosiRyakusyo),
                BataijuuZengo = SafeInt(se.BaTaijyu),
                BataijuuZogen = FormatZogen(se.ZogenFugo, se.ZogenSa),
            };

            return (ExtractRaceKey(se.id), entry);
        }

        /// <summary>"HR"レコード（払戻）をパースする。1レコードに全券種分がまとまっているため
        /// 複数件の<see cref="PayoutEntry"/>を返す。</summary>
        public static (RaceKey Key, List<PayoutEntry> Payouts) ParsePayouts(string rawRecord)
        {
            var hr = new JV_HR_PAY();
            hr.SetDataB(ref rawRecord);

            var payouts = new List<PayoutEntry>();
            AddPayInfo1(payouts, "単勝", hr.PayTansyo, FormatSingleUmaban);
            AddPayInfo1(payouts, "複勝", hr.PayFukusyo, FormatSingleUmaban);
            // 枠連はPAY_INFO1を使うが、フィールドの意味が単勝・複勝と異なる。
            // JV-Data仕様書「４．払戻」より、<枠連払戻>の組番は2バイトで1桁ずつが枠番
            // （枠は1〜8のため）。単勝・複勝の「馬番」2バイトとは区切り方が違うので、
            // そのまま出すと "68" のようになり馬連の "6-10" と表記が揃わない。
            AddPayInfo1(payouts, "枠連", hr.PayWakuren, FormatWakurenKumi);
            AddPayInfo2(payouts, "馬連", hr.PayUmaren);
            AddPayInfo2(payouts, "ワイド", hr.PayWide);
            AddPayInfo2(payouts, "馬単", hr.PayUmatan);
            AddPayInfo3(payouts, "3連複", hr.PaySanrenpuku);
            AddPayInfo4(payouts, "3連単", hr.PaySanrentan);

            return (ExtractRaceKey(hr.id), payouts);
        }

        /// <summary>
        /// "O1"レコード（オッズ1・単複枠）から、馬番ごとの単勝オッズと単勝人気順を取り出す。
        ///
        /// 地方競馬（UmaConn）は、成績レコード(SE)の単勝オッズ・単勝人気順を初期値のまま返す
        /// （実機で確認）。一方、速報オッズ("0B31"/"0B30")では実値を配信している。
        /// そのため結果ページのオッズ・人気はこちらから補完する。
        /// </summary>
        public static (RaceKey Key, Dictionary<int, TanshoOdds> ByUmaban) ParseTanshoOdds(string rawRecord)
        {
            var o1 = new JV_O1_ODDS_TANFUKUWAKU();
            o1.SetDataB(ref rawRecord);

            var byUmaban = new Dictionary<int, TanshoOdds>();
            foreach (var t in o1.OddsTansyoInfo)
            {
                var umaban = SafeInt(t.Umaban);
                if (umaban <= 0) continue;

                // 仕様書「７．オッズ1（単複枠）」より、オッズ欄には数値以外も入りうる。
                //   "0000":無投票 / "----":発売前取消 / "****":発売後取消 / 空白:登録なし
                // 人気順欄も同様に '--' / '**' / 空白 がありうるため、数値以外は採用しない。
                var oddsRaw = Trim(t.Odds);
                if (oddsRaw.Length == 0 || !oddsRaw.All(char.IsDigit)) continue;

                var oddsValue = SafeInt(oddsRaw);
                if (oddsValue <= 0) continue; // 無投票

                var ninkiRaw = Trim(t.Ninki);
                var ninki = ninkiRaw.Length > 0 && ninkiRaw.All(char.IsDigit) ? SafeInt(ninkiRaw) : 0;

                byUmaban[umaban] = new TanshoOdds
                {
                    // 999.9倍形式（末尾1桁が小数第1位）
                    Odds = oddsValue / 10.0,
                    Ninki = ninki,
                };
            }

            return (ExtractRaceKey(o1.id), byUmaban);
        }

        /// <summary>単勝オッズと単勝人気順の組。</summary>
        public class TanshoOdds
        {
            public double Odds { get; set; }
            public int Ninki { get; set; }
        }

        /// <summary>"RA"レコード（レース詳細）からコーナー通過順位を取り出す。</summary>
        public static (RaceKey Key, List<string> CornerPassage) ParseCornerPassage(string rawRecord)
        {
            var ra = new JV_RA_RACE();
            ra.SetDataB(ref rawRecord);

            var passage = new List<string>();
            foreach (var corner in ra.CornerInfo)
            {
                var jyuni = Trim(corner.Jyuni);
                if (!string.IsNullOrEmpty(jyuni))
                    passage.Add(jyuni);
            }

            return (ExtractRaceKey(ra.id), passage);
        }

        /// <summary>着差コード(3バイト: 1バイト目=整数部の馬身数 or 特殊コード、2-3バイト目=分数)を
        /// 表示文字列に変換する。JV-Data仕様書コード表(2102.着差コード)に基づく。
        /// 「短クビ」「短アタマ」も仕様書上"クビ""アタマ"と同一コードのため区別できず、
        /// そのまま"クビ""アタマ"として表示する。</summary>
        private static string FormatChakusa(string code)
        {
            var c = (code ?? string.Empty).PadRight(3).Substring(0, 3);
            var first = c[0];

            switch (first)
            {
                case 'A': return "アタマ";
                case 'D': return "同着";
                case 'H': return "ハナ";
                case 'K': return "クビ";
                case 'T': return "大差";
                case 'Z': return "10馬身";
            }

            var wholePart = first >= '1' && first <= '9' ? first - '0' : 0;
            string fraction;
            switch (c.Substring(1, 2))
            {
                case "12": fraction = "1/2"; break;
                case "14": fraction = "1/4"; break;
                case "34": fraction = "3/4"; break;
                default: fraction = null; break;
            }

            if (wholePart == 0 && fraction == null) return string.Empty;
            if (wholePart == 0) return $"{fraction}馬身";
            if (fraction == null) return $"{wholePart}馬身";
            return $"{wholePart} {fraction}馬身";
        }

        private static RaceKey ExtractRaceKey(RACE_ID id)
        {
            var monthDay = Trim(id.MonthDay);
            var month = monthDay.Length >= 2 ? SafeInt(monthDay.Substring(0, 2)) : 0;
            var day = monthDay.Length >= 4 ? SafeInt(monthDay.Substring(2, 2)) : 0;
            var year = SafeInt(id.Year);

            DateTime date;
            try
            {
                date = year > 0 && month > 0 && day > 0 ? new DateTime(year, month, day) : DateTime.MinValue;
            }
            catch (ArgumentOutOfRangeException)
            {
                date = DateTime.MinValue;
            }

            return new RaceKey
            {
                TrackCode = Trim(id.JyoCD),
                RaceDate = date,
                RaceNumber = SafeInt(id.RaceNum),
            };
        }

        private static void AddPayInfo1(
            List<PayoutEntry> list, string ticketType, PAY_INFO1[] entries, Func<string, string> formatCombination)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = formatCombination(e.Umaban),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        /// <summary>単勝・複勝の的中馬番(2桁ゼロ埋め、例:"06")を"6"に整形する。</summary>
        private static string FormatSingleUmaban(string umaban)
        {
            var n = SafeInt(umaban);
            return n > 0 ? n.ToString() : Trim(umaban);
        }

        /// <summary>枠連の組番(2バイト、1桁ずつが枠番。例:"68")を"6-8"に整形する。</summary>
        private static string FormatWakurenKumi(string kumi)
        {
            var digits = Trim(kumi);
            if (digits.Length != 2) return digits;
            return $"{digits[0]}-{digits[1]}";
        }

        private static void AddPayInfo2(List<PayoutEntry> list, string ticketType, PAY_INFO2[] entries)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = FormatKumi(e.Kumi),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        private static void AddPayInfo3(List<PayoutEntry> list, string ticketType, PAY_INFO3[] entries)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = FormatKumi(e.Kumi),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        private static void AddPayInfo4(List<PayoutEntry> list, string ticketType, PAY_INFO4[] entries)
        {
            foreach (var e in entries)
            {
                if (IsEmptyPay(e.Pay)) continue;
                list.Add(new PayoutEntry
                {
                    TicketType = ticketType,
                    Combination = FormatKumi(e.Kumi),
                    Amount = SafeInt(e.Pay),
                    Ninki = SafeInt(e.Ninki),
                });
            }
        }

        private static bool IsEmptyPay(string pay) => SafeInt(pay) <= 0;

        /// <summary>2桁ずつの馬番連結（例:"0609"）を"6-9"のようなハイフン区切りに整形する。</summary>
        private static string FormatKumi(string kumi)
        {
            var digits = Trim(kumi);
            if (string.IsNullOrEmpty(digits) || digits.Length % 2 != 0)
                return digits;

            var parts = new List<string>();
            for (int i = 0; i < digits.Length; i += 2)
                parts.Add(SafeInt(digits.Substring(i, 2)).ToString());

            return string.Join("-", parts);
        }

        /// <summary>走破タイム4桁("1325")を"1:32.5"形式に整形する。</summary>
        private static string FormatTime(string raw)
        {
            var t = Trim(raw);
            if (t.Length < 4) return string.Empty;

            var minutes = t.Substring(0, 1);
            var seconds = t.Substring(1, 2);
            var tenths = t.Substring(3, 1);
            return $"{minutes}:{seconds}.{tenths}";
        }

        private static string FormatSexAge(string sexCd, string barei)
        {
            string sex;
            switch (Trim(sexCd))
            {
                case "1": sex = "牡"; break;
                case "2": sex = "牝"; break;
                case "3": sex = "セ"; break;
                default: sex = string.Empty; break;
            }

            var age = SafeInt(barei);
            return age > 0 ? $"{sex}{age}" : sex;
        }

        /// <summary>増減符号("+"/"-"/空白)と増減差(3桁)から符号付きの馬体重増減を返す。
        /// "999"（体重計不能）は0として扱う。</summary>
        private static int FormatZogen(string zogenFugo, string zogenSa)
        {
            var sa = SafeInt(zogenSa);
            if (sa == 999) return 0;
            return Trim(zogenFugo) == "-" ? -sa : sa;
        }

        /// <summary>末尾1桁を小数点として扱う数値文字列(例:"540"->54.0)を実数に変換する。</summary>
        private static double SafeTenths(string s) => SafeInt(s) / 10.0;

        private static int SafeInt(string s)
        {
            var t = Trim(s);
            return int.TryParse(t, out var v) ? v : 0;
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();
    }
}
