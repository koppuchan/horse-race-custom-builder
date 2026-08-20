using System;

namespace KeibaDataCollector.Models
{
    /// <summary>
    /// 6ファクター算出のために蓄積する、1レース1頭分の履歴データ。
    ///
    /// RaceCardEntry/RaceResultEntry（WordPressへ送るその日限りの表示用データ）とは別に、
    /// こちらは複数年分をローカルSQLiteに溜め続けて統計を取るための行。
    /// 表示用モデルと統計用モデルを分けているのは、WordPress側のJSON形状（camelCase・
    /// 表示に必要な項目のみ）と、集計に必要な項目（血統コード・トラック種別・馬場状態等）が
    /// 一致しないため。
    /// </summary>
    public class HistoricalRaceEntry
    {
        public string KettoNum { get; set; }        // 血統登録番号（馬の一意キー）
        public DateTime RaceDate { get; set; }
        public string TrackCode { get; set; }        // 競馬場コード（JyoCD）
        public string TrackSurfaceCode { get; set; }  // トラックコード（芝/ダート等）
        public int Distance { get; set; }             // 距離(m)
        public int Waku { get; set; }
        public int Umaban { get; set; }
        public string JockeyCode { get; set; }
        public string TrainerCode { get; set; }
        public int Chakujun { get; set; }             // 確定着順（0=非確定/取消等）
        public double? TanshoOdds { get; set; }
        public double? Agari3F { get; set; }          // 後3ハロンタイム(秒)
        public string CornerPassage4 { get; set; }    // 最終コーナー通過順位（生値。展開分析用）
    }

    /// <summary>坂路調教・ウッドチップ調教の1回分。両者はコース長・ハロン数が異なるため
    /// 生のラップ配列のまま保持し、正規化（最後1F相当の抽出等）は集計側で行う。</summary>
    public class TrainingLapEntry
    {
        public string KettoNum { get; set; }
        public DateTime ChokyoDate { get; set; }
        public TrainingCourse Course { get; set; }
        public string TresenKubun { get; set; }       // トレセン区分（栗東/美浦）

        /// <summary>ゴールに近い方から200mごとのラップ秒（例: [ラスト1F, その前の1F, ...]）。
        /// 坂路は4分割（800M-0M）、ウッドチップは最大10分割（2000M-0M）。
        /// 空文字列（未計測区間）はnullのまま保持する。</summary>
        public double?[] LapTimesSeconds { get; set; }
    }

    public enum TrainingCourse
    {
        Slope,      // 坂路調教（HC）
        WoodChip,   // ウッドチップ調教（WC）
    }

    /// <summary>血統：ある馬の父・母父（HansyokuNum＝繁殖登録番号）。
    /// JV-Data「17.産駒マスタ」のHansyokuNum[14]は3代血統を固定順で持つ
    /// （0:父 1:母 2:父父 3:父母 4:母父 5:母母 ...）。この並びはJRA-VANの
    /// 複数の公開ツール解説で一致しているが、一次仕様書そのものでは未確認のため、
    /// 実データ投入後に既知の馬で必ず検算すること（README参照）。</summary>
    public class PedigreeLink
    {
        public string KettoNum { get; set; }
        public string SireHansyokuNum { get; set; }          // 父
        public string BroodmareSireHansyokuNum { get; set; } // 母父

        /// <summary>対象馬の生年月日。バックフィル時の取得範囲確認用
        /// （BLODをNormalオプションで取っても全履歴になっているか不明なため、
        /// 産駒の生年分布で検算する）。DBには保存せず、ログ集計にのみ使う。</summary>
        public DateTime BirthDate { get; set; }
    }

    /// <summary>繁殖馬マスタ（HN）: 繁殖登録番号→馬名。父・母父の表示名を引くために使う。</summary>
    public class BroodstockName
    {
        public string HansyokuNum { get; set; }
        public string Bamei { get; set; }
    }
}
