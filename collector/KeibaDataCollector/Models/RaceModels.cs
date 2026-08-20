using System;
using System.Collections.Generic;

namespace KeibaDataCollector.Models
{
    /// <summary>レース一意キー（開催日+競馬場+R番号）。JV-Dataのレースキー体系に合わせて調整する。</summary>
    public class RaceKey
    {
        public string TrackCode { get; set; }
        public DateTime RaceDate { get; set; }
        public int RaceNumber { get; set; }

        public string AsSlug() => $"{RaceDate:yyyyMMdd}-{TrackCode}-{RaceNumber}R";

        /// <summary>JVRTOpenのkey引数（レース単位）形式。JV-Linkインターフェース仕様書の
        /// 対応表（払戻確定等のイベントが返すキー）に合わせた"YYYYMMDDJJRR"形式。</summary>
        public string AsJvRealtimeKey() => $"{RaceDate:yyyyMMdd}{TrackCode}{RaceNumber:D2}";
    }

    /// <summary>朝一取得する出走表（番組表）1頭分。画像の出馬表相当。</summary>
    public class RaceCardEntry
    {
        public int Waku { get; set; }
        public int Umaban { get; set; }
        public string HorseName { get; set; }
        public string SexAge { get; set; }
        public double Kinryo { get; set; }
        public string JockeyName { get; set; }
        public string TrainerName { get; set; }
    }

    /// <summary>確定後に取得する着順結果1頭分。添付画像の「結果・払戻」テーブル相当。</summary>
    public class RaceResultEntry
    {
        public int Chakujun { get; set; }
        public int Waku { get; set; }
        public int Umaban { get; set; }
        public string HorseName { get; set; }
        public string SexAge { get; set; }
        public double Kinryo { get; set; }
        public string JockeyName { get; set; }
        public string Time { get; set; }
        public string ChakusaText { get; set; }
        public int Ninki { get; set; }
        public double TanshoOdds { get; set; }
        public double Ushi3F { get; set; }
        public string TrainerName { get; set; }
        public int BataijuuZengo { get; set; }
        public int BataijuuZogen { get; set; }
    }

    /// <summary>払戻金1券種分。</summary>
    public class PayoutEntry
    {
        public string TicketType { get; set; } // 単勝/複勝/枠連/馬連/ワイド/馬単/3連複/3連単
        public string Combination { get; set; }
        public int Amount { get; set; }
        public int Ninki { get; set; }
    }

    public class RaceResult
    {
        public RaceKey Key { get; set; }
        public List<RaceResultEntry> Entries { get; set; } = new List<RaceResultEntry>();
        public List<PayoutEntry> Payouts { get; set; } = new List<PayoutEntry>();
        public List<string> CornerPassage { get; set; } = new List<string>(); // 1角:"2,9,11-1,4,8,10,6,3,7-5" 等
    }
}
