using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using KeibaDataCollector.Data;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// HistoricalDataStoreに蓄積した過去3年分のデータから、6ファクターを0〜100点
    /// （偏差値スタイル：母集団平均=50、標準偏差10点あたり）に正規化して算出する。
    ///
    /// クライアント様からいただいた基準（枠番別連対率・回収率、コース別騎手複勝率・回収率、
    /// 種牡馬コース別複勝率・回収率、直近の上がり3F、最終追切時計＋加速ラップ）に対応させている。
    ///
    /// サンプル数が少なすぎる集団（HAVING句のMinSample未満）は信頼できないため、
    /// 数値を無理に出さずnullを返す。0点や50点で埋めると「本当に平均的」と「データが無い」が
    /// 区別できなくなり、後で見返したときに誤解を招くため。
    /// </summary>
    public class FactorScoringService
    {
        private readonly SQLiteConnection _conn;

        // 集団の統計として信用する最小サンプル数。これ未満のグループはHAVINGで除外し、
        // 該当馬の枠・騎手・種牡馬がこの中に無ければnullを返す。
        private const int MinGroupSample = 20;

        // 個体の直近成績として見る件数（近走）。
        private const int RecentRunsWindow = 5;

        // ②テン速度・展開用。クライアント基準の「近3走」に合わせる（③の直近5走とは別窓）。
        private const int RecentPaceRunsWindow = 3;

        public FactorScoringService(HistoricalDataStore store)
        {
            _conn = store.Connection;
        }

        public FactorScores Compute(FactorScoringInput input)
        {
            return new FactorScores
            {
                ParamBias = ComputeWakuBias(input.TrackCode, input.Distance, input.TrackSurfaceCode, input.Waku),
                ParamPace = ComputePaceScore(input.KettoNum, input.TrackCode, input.Distance, input.TrackSurfaceCode),
                ParamAgariQ = ComputeAgariQuality(input.KettoNum, input.TrackSurfaceCode),
                ParamJockeyRoi = ComputeJockeyRoi(input.TrackCode, input.Distance, input.JockeyCode),
                ParamPedigreeFit = ComputePedigreeFit(input.KettoNum, input.TrackCode, input.Distance),
                ParamTrainingAcc = ComputeTrainingAcceleration(input.KettoNum),
            };
        }

        /// <summary>①枠・馬場バイアス: 当該コース(track×distance×surface)における
        /// 枠番別の連対率を、同条件の全枠番の分布の中で偏差値化する。</summary>
        private double? ComputeWakuBias(string trackCode, int distance, string surfaceCode, int waku)
        {
            var rates = new Dictionary<int, double>();
            using (var cmd = new SQLiteCommand(@"
                SELECT waku, COUNT(*) total,
                       SUM(CASE WHEN chakujun BETWEEN 1 AND 2 THEN 1 ELSE 0 END) rentai
                FROM race_entries
                WHERE track_code=@track AND distance=@distance AND track_surface_code=@surface
                      AND chakujun > 0 AND waku BETWEEN 1 AND 8
                GROUP BY waku
                HAVING COUNT(*) >= @minSample;", _conn))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                cmd.Parameters.AddWithValue("@distance", distance);
                cmd.Parameters.AddWithValue("@surface", surfaceCode);
                cmd.Parameters.AddWithValue("@minSample", MinGroupSample);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        rates[r.GetInt32(0)] = (double)r.GetInt64(2) / r.GetInt64(1);
                }
            }

            if (!rates.TryGetValue(waku, out var thisRate) || rates.Count < 2) return null;
            return ToDeviationScore(thisRate, rates.Values, invert: false);
        }

        /// <summary>②テン速度・展開: クライアント基準「近3走の前半3Fタイム・脚質実績からの
        /// 先行有利度」に対応。個体ごとの前半3Fタイムはrace_entriesに持っていないため、
        /// 代わりに「最も早いコーナーでの通過順位」（0=先頭通過, 1=最後尾通過に正規化した
        /// early_position_ratio。BackfillService参照）を脚質実績の代理指標として使う。
        ///
        /// 1. 対象馬の直近3走のearly_position_ratio平均＝horseStyle（小さいほど先行タイプ）。
        /// 2. 当該コース(track×distance×surface)で、複勝圏内(1〜3着)に入った馬の方が
        ///    それ以外の馬よりも平均して前目を通過しているかを比較する。前目の方が良ければ
        ///    「このコースは先行有利」と判定する。
        /// 3. horseStyleを、当該コースのearly_position_ratio分布内で偏差値化する。
        ///    先行有利なコースでは値が小さい（前目）ほど高得点になるよう反転する。</summary>
        private double? ComputePaceScore(string kettoNum, string trackCode, int distance, string surfaceCode)
        {
            double? horseStyle = null;
            using (var cmd = new SQLiteCommand(@"
                SELECT AVG(early_position_ratio) FROM (
                    SELECT early_position_ratio FROM race_entries
                    WHERE ketto_num=@ketto AND early_position_ratio IS NOT NULL
                    ORDER BY race_date DESC LIMIT @n
                );", _conn))
            {
                cmd.Parameters.AddWithValue("@ketto", kettoNum);
                cmd.Parameters.AddWithValue("@n", RecentPaceRunsWindow);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value) horseStyle = Convert.ToDouble(result);
            }
            if (!horseStyle.HasValue) return null;

            double? placedAvg = null, restAvg = null;
            int placedCount = 0, restCount = 0;
            using (var cmd = new SQLiteCommand(@"
                SELECT
                    AVG(CASE WHEN chakujun BETWEEN 1 AND 3 THEN early_position_ratio END) placed_avg,
                    SUM(CASE WHEN chakujun BETWEEN 1 AND 3 THEN 1 ELSE 0 END) placed_count,
                    AVG(CASE WHEN chakujun > 3 THEN early_position_ratio END) rest_avg,
                    SUM(CASE WHEN chakujun > 3 THEN 1 ELSE 0 END) rest_count
                FROM race_entries
                WHERE track_code=@track AND distance=@distance AND track_surface_code=@surface
                      AND chakujun > 0 AND early_position_ratio IS NOT NULL;", _conn))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                cmd.Parameters.AddWithValue("@distance", distance);
                cmd.Parameters.AddWithValue("@surface", surfaceCode);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        if (!r.IsDBNull(0)) placedAvg = r.GetDouble(0);
                        placedCount = r.GetInt32(1);
                        if (!r.IsDBNull(2)) restAvg = r.GetDouble(2);
                        restCount = r.GetInt32(3);
                    }
                }
            }
            if (placedCount < MinGroupSample || restCount < MinGroupSample || !placedAvg.HasValue || !restAvg.HasValue)
                return null;

            // 複勝圏内の馬の方が前目(値が小さい)を通過していれば、このコースは先行有利。
            var trackFavorsFront = placedAvg.Value < restAvg.Value;

            var (mean, stddev, popCount) = GetPopulationMeanStdDev(
                "SELECT early_position_ratio FROM race_entries WHERE track_code=@track AND distance=@distance " +
                "AND track_surface_code=@surface AND early_position_ratio IS NOT NULL",
                p => {
                    p.AddWithValue("@track", trackCode);
                    p.AddWithValue("@distance", distance);
                    p.AddWithValue("@surface", surfaceCode);
                });
            if (popCount < MinGroupSample || stddev <= 0) return null;

            return ToDeviationScore(horseStyle.Value, mean, stddev, invert: trackFavorsFront);
        }

        /// <summary>③上がり3F質・末脚: 直近走の上がり3Fタイム平均を、同じ馬場種別（芝/ダート）
        /// 全体の分布内で偏差値化する（タイムは短いほど良いので反転する）。
        /// 距離をまたいで比較するのは粗いが、上がり3Fは距離差の影響が相対的に小さいため許容する。</summary>
        private double? ComputeAgariQuality(string kettoNum, string surfaceCode)
        {
            double? recentAvg = null;
            using (var cmd = new SQLiteCommand(@"
                SELECT AVG(agari_3f) FROM (
                    SELECT agari_3f FROM race_entries
                    WHERE ketto_num=@ketto AND agari_3f IS NOT NULL AND agari_3f > 0
                    ORDER BY race_date DESC LIMIT @n
                );", _conn))
            {
                cmd.Parameters.AddWithValue("@ketto", kettoNum);
                cmd.Parameters.AddWithValue("@n", RecentRunsWindow);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value) recentAvg = Convert.ToDouble(result);
            }
            if (!recentAvg.HasValue) return null;

            var (mean, stddev, count) = GetPopulationMeanStdDev(
                "SELECT agari_3f FROM race_entries WHERE track_surface_code=@surface AND agari_3f IS NOT NULL AND agari_3f > 0",
                p => p.AddWithValue("@surface", surfaceCode));
            if (count < MinGroupSample || stddev <= 0) return null;

            return ToDeviationScore(recentAvg.Value, mean, stddev, invert: true);
        }

        /// <summary>④騎手コース回収率: 当該コース(track×distance)における騎手の複勝回収率を、
        /// 同条件で騎乗歴のある全騎手の分布内で偏差値化する。
        /// 複勝率ではなく回収率を主指標にしている（クライアント基準の「回収率」に対応）。</summary>
        private double? ComputeJockeyRoi(string trackCode, int distance, string jockeyCode)
        {
            if (string.IsNullOrEmpty(jockeyCode)) return null;

            var rois = new Dictionary<string, double>();
            using (var cmd = new SQLiteCommand(@"
                SELECT jockey_code, COUNT(*) total, SUM(COALESCE(fukusho_payout, 0)) totalPayout
                FROM race_entries
                WHERE track_code=@track AND distance=@distance AND chakujun > 0
                      AND jockey_code IS NOT NULL AND jockey_code != ''
                GROUP BY jockey_code
                HAVING COUNT(*) >= @minSample;", _conn))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                cmd.Parameters.AddWithValue("@distance", distance);
                cmd.Parameters.AddWithValue("@minSample", MinGroupSample);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var total = r.GetInt64(1);
                        var payout = r.GetDouble(2);
                        rois[r.GetString(0)] = payout / (total * 100.0); // 100円あたりの回収率（1.0=収支トントン）
                    }
                }
            }

            if (!rois.TryGetValue(jockeyCode, out var thisRoi) || rois.Count < 2) return null;
            return ToDeviationScore(thisRoi, rois.Values, invert: false);
        }

        /// <summary>⑤血統適性・妙味: 種牡馬（父）の当該コース(track×distance)における産駒複勝率を、
        /// 同条件で産駒が走った全種牡馬の分布内で偏差値化する。
        /// 母父も同様に計算し、平均する（父だけだと種牡馬側のサンプルに偏りが出やすいため）。
        /// pedigree_linksが空の場合（血統データ未取得）は常にnullを返す。</summary>
        private double? ComputePedigreeFit(string kettoNum, string trackCode, int distance)
        {
            string sire = null, broodmareSire = null;
            using (var cmd = new SQLiteCommand(
                "SELECT sire_hansyoku_num, broodmare_sire_hansyoku_num FROM pedigree_links WHERE ketto_num=@ketto;", _conn))
            {
                cmd.Parameters.AddWithValue("@ketto", kettoNum);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        sire = r.IsDBNull(0) ? null : r.GetString(0);
                        broodmareSire = r.IsDBNull(1) ? null : r.GetString(1);
                    }
                }
            }
            if (string.IsNullOrEmpty(sire) && string.IsNullOrEmpty(broodmareSire)) return null;

            var sireScore = string.IsNullOrEmpty(sire) ? (double?)null
                : ComputeSireLineScore(trackCode, distance, sire);
            var bmsScore = string.IsNullOrEmpty(broodmareSire) ? (double?)null
                : ComputeSireLineScore(trackCode, distance, broodmareSire);

            if (sireScore.HasValue && bmsScore.HasValue) return (sireScore.Value + bmsScore.Value) / 2.0;
            return sireScore ?? bmsScore;
        }

        private double? ComputeSireLineScore(string trackCode, int distance, string hansyokuNum)
        {
            var rates = new Dictionary<string, double>();
            using (var cmd = new SQLiteCommand(@"
                SELECT pl.sire_hansyoku_num, COUNT(*) total,
                       SUM(CASE WHEN re.chakujun BETWEEN 1 AND 3 THEN 1 ELSE 0 END) placed
                FROM race_entries re
                JOIN pedigree_links pl ON re.ketto_num = pl.ketto_num
                WHERE re.track_code=@track AND re.distance=@distance AND re.chakujun > 0
                GROUP BY pl.sire_hansyoku_num
                HAVING COUNT(*) >= @minSample;", _conn))
            {
                cmd.Parameters.AddWithValue("@track", trackCode);
                cmd.Parameters.AddWithValue("@distance", distance);
                cmd.Parameters.AddWithValue("@minSample", MinGroupSample);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        rates[r.GetString(0)] = (double)r.GetInt64(2) / r.GetInt64(1);
                }
            }

            if (!rates.TryGetValue(hansyokuNum, out var thisRate) || rates.Count < 2) return null;
            return ToDeviationScore(thisRate, rates.Values, invert: false);
        }

        /// <summary>⑥調教・加速ラップ: 直近の調教（坂路 or ウッドチップ、新しい方）のラスト1F
        /// タイムを、同じコース種別全体の分布内で偏差値化し、さらに「加速ラップ」
        /// （ゴールに近づくほどラップが速くなっているか）を加点/減点する。
        /// 坂路とウッドチップはコース長が違うためタイム水準も異なる。混ぜて母集団を
        /// 取ると不公平になるため、対象馬と同じcourse種別の中だけで比較する。</summary>
        private double? ComputeTrainingAcceleration(string kettoNum)
        {
            TrainingCourse? course = null;
            double? lastFurlong = null;
            double[] laps = null;

            using (var cmd = new SQLiteCommand(@"
                SELECT course, lap_times_seconds FROM training_laps
                WHERE ketto_num=@ketto ORDER BY chokyo_date DESC LIMIT 1;", _conn))
            {
                cmd.Parameters.AddWithValue("@ketto", kettoNum);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        course = (TrainingCourse)Enum.Parse(typeof(TrainingCourse), r.GetString(0));
                        laps = r.GetString(1).Split(',')
                            .Select(s => double.TryParse(s, out var v) ? v : (double?)null)
                            .Where(v => v.HasValue).Select(v => v.Value).ToArray();
                        if (laps.Length > 0) lastFurlong = laps[0]; // 先頭=ゴールに一番近い1F
                    }
                }
            }
            if (!course.HasValue || !lastFurlong.HasValue) return null;

            var (mean, stddev, count) = GetPopulationMeanStdDev(
                "SELECT CAST(lap_times_seconds AS TEXT) FROM training_laps WHERE course=@course;",
                p => p.AddWithValue("@course", course.Value.ToString()),
                extractFirstLap: true);
            if (count < MinGroupSample || stddev <= 0) return null;

            var baseScore = ToDeviationScore(lastFurlong.Value, mean, stddev, invert: true); // タイムは短いほど良い

            // 加速ラップ判定: 各区間が、その前（ゴールから遠い方）の区間より速くなっているか。
            // 配列はゴールに近い順（laps[0]=ラスト1F）なので、後ろの要素ほどゴールから遠い。
            // 「加速している」＝ laps[i] < laps[i+1]（ゴールに近づくほど速い）が続いているほど良い。
            int accelSegments = 0, totalSegments = 0;
            for (int i = 0; i < laps.Length - 1; i++)
            {
                totalSegments++;
                if (laps[i] < laps[i + 1]) accelSegments++;
            }
            var accelBonus = totalSegments > 0 ? (accelSegments / (double)totalSegments - 0.5) * 10 : 0; // ±5点

            return Clamp(baseScore + accelBonus, 0, 100);
        }

        /// <summary>複数サンプル（例: 各枠番の連対率）から、対象値の偏差値スタイルスコアを返す。</summary>
        private static double ToDeviationScore(double value, IEnumerable<double> population, bool invert)
        {
            var list = population as IList<double> ?? population.ToList();
            var mean = list.Average();
            var variance = list.Select(v => (v - mean) * (v - mean)).Average();
            var stddev = Math.Sqrt(variance);
            if (stddev <= 0) return 50;
            return ToDeviationScore(value, mean, stddev, invert);
        }

        private static double ToDeviationScore(double value, double mean, double stddev, bool invert)
        {
            var z = (value - mean) / stddev;
            if (invert) z = -z;
            return Clamp(50 + 10 * z, 0, 100);
        }

        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

        /// <summary>SQLiteに標準偏差の集計関数が無いため、AVG(x)とAVG(x*x)から
        /// 分散=E[x^2]-E[x]^2 で計算する（大量行を素直にAVG(x*x)できるSQL文向け）。
        /// extractFirstLap=trueのときは、"12.5,12.1,11.8"のようなCSV文字列列から
        /// 先頭の値だけをC#側で取り出して母集団にする（training_lapsのlap_times_secondsのため）。</summary>
        private (double Mean, double StdDev, int Count) GetPopulationMeanStdDev(
            string sql, Action<SQLiteParameterCollection> bind, bool extractFirstLap = false)
        {
            var values = new List<double>();
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                bind(cmd.Parameters);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (r.IsDBNull(0)) continue;
                        if (extractFirstLap)
                        {
                            var first = r.GetString(0).Split(',').FirstOrDefault();
                            if (double.TryParse(first, out var v)) values.Add(v);
                        }
                        else
                        {
                            values.Add(r.GetDouble(0));
                        }
                    }
                }
            }
            if (values.Count == 0) return (0, 0, 0);
            var mean = values.Average();
            var variance = values.Select(v => (v - mean) * (v - mean)).Average();
            return (mean, Math.Sqrt(variance), values.Count);
        }
    }
}
