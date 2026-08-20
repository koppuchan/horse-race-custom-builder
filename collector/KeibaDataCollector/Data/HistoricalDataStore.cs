using System;
using System.Data;
using System.IO;
using System.Data.SQLite;
using KeibaDataCollector.Models;

namespace KeibaDataCollector.Data
{
    /// <summary>
    /// 6ファクターの統計に使う履歴データをローカルSQLiteに蓄積する。
    ///
    /// WordPress側に何年分ものレース結果を貯めて集計するのは無理がある
    /// （表示用サイトを分析用DBに転用することになり、パフォーマンスにも影響する）ため、
    /// 収集アプリと同じVPS上にSQLiteファイルを持ち、集計はすべてこちら側で完結させる。
    /// WordPressへは、集計済みの0〜100点スコア（hrc_factors）だけを送る想定。
    ///
    /// 3年分・中央+地方競馬という規模を想定し、素朴なORMは使わずADO.NETを直接使う
    /// （数百万行規模になりうるため、余計な抽象化のオーバーヘッドを避ける）。
    /// </summary>
    public class HistoricalDataStore : IDisposable
    {
        private readonly SQLiteConnection _conn;

        public HistoricalDataStore(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var isNew = !File.Exists(dbPath);
            _conn = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            _conn.Open();

            if (isNew)
                Console.WriteLine($"[HistoricalDataStore] 新規DBを作成: {dbPath}");

            EnsureSchema();
        }

        private void EnsureSchema()
        {
            Exec(@"
                CREATE TABLE IF NOT EXISTS race_entries (
                    ketto_num TEXT NOT NULL,
                    race_date TEXT NOT NULL,
                    track_code TEXT NOT NULL,
                    track_surface_code TEXT,
                    distance INTEGER,
                    waku INTEGER,
                    umaban INTEGER,
                    jockey_code TEXT,
                    trainer_code TEXT,
                    chakujun INTEGER,
                    tansho_odds REAL,
                    agari_3f REAL,
                    corner_passage_4 TEXT,
                    PRIMARY KEY (ketto_num, race_date, track_code, distance)
                );
                CREATE INDEX IF NOT EXISTS idx_race_entries_track ON race_entries(track_code, distance, track_surface_code);
                CREATE INDEX IF NOT EXISTS idx_race_entries_jockey ON race_entries(jockey_code, track_code, distance);
                CREATE INDEX IF NOT EXISTS idx_race_entries_ketto ON race_entries(ketto_num);

                CREATE TABLE IF NOT EXISTS training_laps (
                    ketto_num TEXT NOT NULL,
                    chokyo_date TEXT NOT NULL,
                    course TEXT NOT NULL,
                    tresen_kubun TEXT,
                    lap_times_seconds TEXT NOT NULL,
                    PRIMARY KEY (ketto_num, chokyo_date, course)
                );
                CREATE INDEX IF NOT EXISTS idx_training_laps_ketto ON training_laps(ketto_num);

                CREATE TABLE IF NOT EXISTS pedigree_links (
                    ketto_num TEXT PRIMARY KEY,
                    sire_hansyoku_num TEXT,
                    broodmare_sire_hansyoku_num TEXT
                );

                CREATE TABLE IF NOT EXISTS broodstock_names (
                    hansyoku_num TEXT PRIMARY KEY,
                    bamei TEXT
                );
            ");
        }

        /// <summary>1件upsertする。バックフィル中は数十万〜数百万件になるため、
        /// 呼び出し側でトランザクションにまとめて使うこと（BeginBatch参照）。</summary>
        public void UpsertRaceEntry(HistoricalRaceEntry e)
        {
            Exec(@"
                INSERT INTO race_entries
                    (ketto_num, race_date, track_code, track_surface_code, distance, waku, umaban,
                     jockey_code, trainer_code, chakujun, tansho_odds, agari_3f, corner_passage_4)
                VALUES
                    (@ketto_num, @race_date, @track_code, @track_surface_code, @distance, @waku, @umaban,
                     @jockey_code, @trainer_code, @chakujun, @tansho_odds, @agari_3f, @corner_passage_4)
                ON CONFLICT(ketto_num, race_date, track_code, distance) DO UPDATE SET
                    track_surface_code=excluded.track_surface_code,
                    waku=excluded.waku,
                    umaban=excluded.umaban,
                    jockey_code=excluded.jockey_code,
                    trainer_code=excluded.trainer_code,
                    chakujun=excluded.chakujun,
                    tansho_odds=excluded.tansho_odds,
                    agari_3f=excluded.agari_3f,
                    corner_passage_4=excluded.corner_passage_4;
            ",
                p => {
                    p.AddWithValue("@ketto_num", e.KettoNum);
                    p.AddWithValue("@race_date", e.RaceDate.ToString("yyyy-MM-dd"));
                    p.AddWithValue("@track_code", e.TrackCode);
                    p.AddWithValue("@track_surface_code", (object)e.TrackSurfaceCode ?? DBNull.Value);
                    p.AddWithValue("@distance", e.Distance);
                    p.AddWithValue("@waku", e.Waku);
                    p.AddWithValue("@umaban", e.Umaban);
                    p.AddWithValue("@jockey_code", (object)e.JockeyCode ?? DBNull.Value);
                    p.AddWithValue("@trainer_code", (object)e.TrainerCode ?? DBNull.Value);
                    p.AddWithValue("@chakujun", e.Chakujun);
                    p.AddWithValue("@tansho_odds", (object)e.TanshoOdds ?? DBNull.Value);
                    p.AddWithValue("@agari_3f", (object)e.Agari3F ?? DBNull.Value);
                    p.AddWithValue("@corner_passage_4", (object)e.CornerPassage4 ?? DBNull.Value);
                });
        }

        public void UpsertTrainingLap(TrainingLapEntry e)
        {
            Exec(@"
                INSERT INTO training_laps (ketto_num, chokyo_date, course, tresen_kubun, lap_times_seconds)
                VALUES (@ketto_num, @chokyo_date, @course, @tresen_kubun, @laps)
                ON CONFLICT(ketto_num, chokyo_date, course) DO UPDATE SET
                    tresen_kubun=excluded.tresen_kubun,
                    lap_times_seconds=excluded.lap_times_seconds;
            ",
                p => {
                    p.AddWithValue("@ketto_num", e.KettoNum);
                    p.AddWithValue("@chokyo_date", e.ChokyoDate.ToString("yyyy-MM-dd"));
                    p.AddWithValue("@course", e.Course.ToString());
                    p.AddWithValue("@tresen_kubun", (object)e.TresenKubun ?? DBNull.Value);
                    p.AddWithValue("@laps", string.Join(",", Array.ConvertAll(e.LapTimesSeconds, v => v.HasValue ? v.Value.ToString("0.0") : "")));
                });
        }

        public void UpsertPedigreeLink(PedigreeLink e)
        {
            Exec(@"
                INSERT INTO pedigree_links (ketto_num, sire_hansyoku_num, broodmare_sire_hansyoku_num)
                VALUES (@ketto_num, @sire, @bms)
                ON CONFLICT(ketto_num) DO UPDATE SET
                    sire_hansyoku_num=excluded.sire_hansyoku_num,
                    broodmare_sire_hansyoku_num=excluded.broodmare_sire_hansyoku_num;
            ",
                p => {
                    p.AddWithValue("@ketto_num", e.KettoNum);
                    p.AddWithValue("@sire", (object)e.SireHansyokuNum ?? DBNull.Value);
                    p.AddWithValue("@bms", (object)e.BroodmareSireHansyokuNum ?? DBNull.Value);
                });
        }

        public void UpsertBroodstockName(BroodstockName e)
        {
            Exec(@"
                INSERT INTO broodstock_names (hansyoku_num, bamei)
                VALUES (@num, @name)
                ON CONFLICT(hansyoku_num) DO UPDATE SET bamei=excluded.bamei;
            ",
                p => {
                    p.AddWithValue("@num", e.HansyokuNum);
                    p.AddWithValue("@name", e.Bamei);
                });
        }

        /// <summary>大量upsert中はSQLiteの自動コミット（1文ごとにfsync）が致命的に遅いため、
        /// バックフィル時はこれで包んで数千件単位でまとめてコミットすること。</summary>
        public IDisposable BeginBatch()
        {
            var tx = _conn.BeginTransaction();
            return new BatchScope(tx);
        }

        private class BatchScope : IDisposable
        {
            private readonly SQLiteTransaction _tx;
            private bool _done;
            public BatchScope(SQLiteTransaction tx) { _tx = tx; }
            public void Commit() { _tx.Commit(); _done = true; }
            public void Dispose() { if (!_done) _tx.Commit(); }
        }

        private void Exec(string sql, Action<SQLiteParameterCollection> bind = null)
        {
            using (var cmd = new SQLiteCommand(sql, _conn))
            {
                bind?.Invoke(cmd.Parameters);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>蓄積済みデータの件数・日付範囲をコンソールに出す。
        /// DBファイルそのものをやり取りする代わりに、統計だけを確認できるようにするための
        /// 調査用コマンド（Program.csの"dbstats"から呼ぶ）。</summary>
        public void PrintStats()
        {
            Console.WriteLine("=== race_entries（過去のレース結果。枠バイアス・上がり3F等の集計元） ===");
            using (var cmd = new SQLiteCommand(
                "SELECT COUNT(*), MIN(race_date), MAX(race_date), COUNT(DISTINCT ketto_num) FROM race_entries;", _conn))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                    Console.WriteLine($"件数={r.GetInt64(0)}, 日付範囲=[{ReadOrNull(r, 1)}〜{ReadOrNull(r, 2)}], 対象馬数={r.GetInt64(3)}");
            }
            Console.WriteLine("競馬場コード別内訳（上位20）:");
            using (var cmd = new SQLiteCommand(
                "SELECT track_code, COUNT(*) c, MIN(race_date), MAX(race_date) FROM race_entries " +
                "GROUP BY track_code ORDER BY c DESC LIMIT 20;", _conn))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    Console.WriteLine($"  場コード={r.GetString(0)}: {r.GetInt64(1)}件 [{ReadOrNull(r, 2)}〜{ReadOrNull(r, 3)}]");
            }

            Console.WriteLine("=== training_laps（坂路・ウッドチップ調教） ===");
            using (var cmd = new SQLiteCommand(
                "SELECT course, COUNT(*), MIN(chokyo_date), MAX(chokyo_date) FROM training_laps GROUP BY course;", _conn))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    Console.WriteLine($"  {r.GetString(0)}: {r.GetInt64(1)}件 [{ReadOrNull(r, 2)}〜{ReadOrNull(r, 3)}]");
            }

            Console.WriteLine("=== pedigree_links / broodstock_names（血統） ===");
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM pedigree_links;", _conn))
                Console.WriteLine($"  pedigree_links: {cmd.ExecuteScalar()}件");
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM broodstock_names;", _conn))
                Console.WriteLine($"  broodstock_names: {cmd.ExecuteScalar()}件");
            Console.WriteLine("  （血統は生年月日を保存していないため、日付範囲はbackfill実行時のログでのみ確認可）");
        }

        private static string ReadOrNull(SQLiteDataReader r, int i) => r.IsDBNull(i) ? "N/A" : r.GetString(i);

        public void Dispose()
        {
            _conn?.Close();
            _conn?.Dispose();
        }
    }
}
