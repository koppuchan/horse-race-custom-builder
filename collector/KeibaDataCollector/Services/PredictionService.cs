using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KeibaDataCollector.Interop;
using KeibaDataCollector.Models;
using KeibaDataCollector.WordPress;

namespace KeibaDataCollector.Services
{
    /// <summary>
    /// 予想印（◎○▲△）を生成してWordPressへ反映する。
    ///
    /// 予想はJV-Link/UmaConnからは提供されない。お客様が別ツールで
    /// 「朝一時点のオッズから人気順に印を付ける」運用をされていたものを、
    /// こちらのデータで直接生成するようにしたもの。
    ///
    /// ルールが「人気順の上位4頭に◎○▲△」であることは、
    /// 実際に公開されていた予想26レース分と当方のオッズデータを突き合わせて確認した
    /// （全レースで必ず4頭、印の付いた馬の87%が確定人気5位以内。
    ///   ずれは朝から発走までのオッズ変動で説明がつく）。
    ///
    /// 別ツールのページを解析する案もあったが、生成側の書式が変わるだけで
    /// 予想が丸ごと消えるため採用していない。元データが同じなら自前で作るほうが安全で、
    /// ばんえい・中央競馬も自動的に対象にできる。
    ///
    /// 【重要】全レース分を一度に取ることはできない。
    /// JV-Data仕様書「（２）速報系データ」より、速報オッズ(0B30/0B31)は
    ///   提供単位     : レース毎（0B12等と違い「開催日単位」は選べない）
    ///   提供タイミング: 対象レースの勝ち馬投票券発売以降に提供
    /// つまり発売前のオッズは存在せず、データ種別を変えても解決しない。
    /// 発売開始が競馬場ごとに違うため（実測 2026-08-11: 10時台に盛岡・笠松は全レース揃う一方、
    /// 浦和・門別・金沢は12:30時点で0件）、このモードは開催時間中ずっと繰り返し実行し、
    /// 発売が始まったレースから順に埋めていく前提で作られている。
    /// </summary>
    public class PredictionService
    {
        private readonly IRaceDataSource _source;
        private readonly WordPressClient _wp;

        // 速報オッズ（単複枠）。O1レコードに単勝オッズと単勝人気順が入る。
        private const string RealtimeOddsDataSpec = "0B31";

        /// <summary>人気1位から順に割り当てる印。数を増やせば5番手以降にも付けられる。</summary>
        private static readonly string[] Marks = { "◎", "○", "▲", "△" };

        public PredictionService(IRaceDataSource source, WordPressClient wp)
        {
            _source = source;
            _wp = wp;
        }

        public async Task RunAsync(DateTime targetDate, CancellationToken ct)
        {
            var started = DateTime.Now;
            var races = RaceDiscovery.ForDate(_source, targetDate);
            Console.WriteLine($"[{_source.SourceName}] {targetDate:yyyy-MM-dd} 予想対象レース: {races.Count}件");

            int published = 0, already = 0, notOnSale = 0, oddsError = 0, failed = 0;

            foreach (var race in races)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // 先にWordPressを見て、済んでいるレースはオッズ取得ごと省く。
                    // このモードは開催時間中ずっと繰り返し走るため、逆順にすると
                    // 反映済みのレースにも毎回COM呼び出しが発生する
                    // （57レース×1日24回＝1368回）。
                    // オッズ取得は1レースあたり数十秒かかることがあり、
                    // 1回の実行が長引くほど、オッズ公開から反映までの遅れが大きくなる。
                    if (await _wp.ShouldSkipPredictionAsync(race, Marks.Length))
                    {
                        already++;
                        continue;
                    }

                    var marks = BuildMarks(race);
                    if (marks.Count == 0)
                    {
                        // JV-Linkインターフェース仕様書 p.54 のコード表より、
                        //   -1 = 該当データ無し（＝まだ発売前。正常）
                        //   それ以外の負値 = 実際のエラー（-202 オープン中 / -301 認証 など）
                        // 両方を「オッズ未提供」で片付けると、COM競合や認証切れが
                        // 発売前と同じ見え方になり切り分けられない。必ず分けて記録する。
                        if (_lastOpenReturnCode == 0 || _lastOpenReturnCode == -1)
                        {
                            notOnSale++;
                        }
                        else
                        {
                            oddsError++;
                            Console.WriteLine(
                                $"[{_source.SourceName}] {race.AsSlug()} オッズ取得エラー rc={_lastOpenReturnCode}" +
                                $"（{DescribeReturnCode(_lastOpenReturnCode)}）");
                        }
                        continue;
                    }

                    if (!await _wp.UpsertPredictionsAsync(race, marks))
                    {
                        // 直前に他の実行が書き込んだ場合。上書きしない。
                        already++;
                        continue;
                    }

                    published++;
                    Console.WriteLine(
                        $"[{_source.SourceName}] {race.AsSlug()} 予想を反映（" +
                        string.Join(" ", marks.OrderBy(m => Array.IndexOf(Marks, m.Value))
                                              .Select(m => $"{m.Value}{m.Key}")) + "）");
                }
                catch (Exception ex)
                {
                    // 1レースの失敗で当日分すべてを落とさない。
                    failed++;
                    Console.WriteLine($"[{_source.SourceName}] {race.AsSlug()} 予想の反映に失敗（このレースのみスキップ）: {ex.Message}");
                }
            }

            // 所要時間も出す。繰り返し間隔より長くなると、次の回が多重起動禁止で弾かれ、
            // オッズ公開から反映までの遅れがそのまま伸びる。調整の判断材料として必ず残す。
            Console.WriteLine(
                $"[{_source.SourceName}] 予想の反映完了: 新規{published}件 / 反映済み{already}件 / " +
                $"発売前{notOnSale}件 / オッズ取得エラー{oddsError}件 / 反映失敗{failed}件 " +
                $"（所要 {(DateTime.Now - started).TotalMinutes:0.0}分）");

            // 「1件も新規が無い」は正常な状態でも起きる（前回までに全レース反映済み）。
            // また、朝の早い時間帯はオッズがまだ配信されておらず0件が正常
            // （実測: 09:31時点で57レース中2件、10:05時点で24件）。
            //
            // 異常と言えるのは「この時刻になっても1件も予想が付いていない」場合だけ。
            // ここを単純に「0件なら失敗」にすると毎朝1本目が必ず失敗し、
            // 本当の異常を知らせる警報として機能しなくなる。
            if (published == 0 && already == 0 && races.Count > 0
                && DateTime.Now.TimeOfDay >= NoPredictionIsAbnormalAfter)
            {
                throw new InvalidOperationException(
                    $"{_source.SourceName} 予想が1件もありません" +
                    $"（対象{races.Count}件、発売前{notOnSale}件、オッズ取得エラー{oddsError}件）。");
            }
        }

        /// <summary>
        /// JVRTOpen/NVRTOpen の戻り値の意味。
        /// 出典: JV-Linkインターフェース仕様書 p.54「ＪＶＯｐｅｎ／ＪＶＲＴＯｐｅｎ」コード表。
        /// ログを読む人が仕様書を引かずに原因へ辿り着けるようにするため、文言も出す。
        /// </summary>
        private static string DescribeReturnCode(int rc)
        {
            switch (rc)
            {
                case -1:   return "該当データ無し（発売前）";
                case -111: return "dataspecパラメータが不正";
                case -114: return "keyパラメータが不正";
                case -201: return "JVInitが行われていない";
                case -202: return "前回のOpenに対してCloseが呼ばれていない（オープン中）";
                case -211: return "レジストリ内容が不正";
                case -301: return "認証エラー（利用キーを確認）";
                case -302: return "利用キーの有効期限切れ";
                case -303: return "利用キーが設定されていない";
                case -504: return "サーバーメンテナンス中";
                default:   return "仕様書p.54のコード表を参照";
            }
        }

        /// <summary>この時刻を過ぎても予想が1件も無ければ異常とみなす。</summary>
        private static readonly TimeSpan NoPredictionIsAbnormalAfter = TimeSpan.FromHours(11);

        /// <summary>
        /// 1レース分の印を作る。戻り値は 馬番 => 印。
        /// 人気順が取得できない馬（発売前取消・無投票など）は対象外になる。
        /// </summary>
        private Dictionary<int, string> BuildMarks(RaceKey race)
        {
            var byUmaban = FetchTanshoOdds(race);
            var marks = new Dictionary<int, string>();
            if (byUmaban == null) return marks;

            // 人気順が有効（1以上）の馬だけを対象に、人気の若い順へ印を割り当てる。
            // 同一人気が複数返ることは通常ないが、返ってきた場合は馬番順で安定させる。
            var ranked = byUmaban
                .Where(kv => kv.Value.Ninki > 0)
                .OrderBy(kv => kv.Value.Ninki)
                .ThenBy(kv => kv.Key)
                .ToList();

            // 印の数だけ人気順が揃うまで待つ。
            // オッズ配信が始まった直後は一部の馬しか値が入っておらず、
            // そこで確定させると印の欠けた予想が残ってしまう
            // （実測 2026-08-12: 大井2Rが5頭立てにもかかわらず◎1頭だけで固定された）。
            // 揃っていなければ空を返し、次の回で取り直す。
            if (ranked.Count < Marks.Length) return marks;

            for (int i = 0; i < Marks.Length; i++)
                marks[ranked[i].Key] = Marks[i];

            return marks;
        }

        /// <summary>
        /// オッズを取得できなかった理由。「まだ発売前」と「本当のエラー」を区別する。
        ///
        /// 以前は戻り値が0以外なら理由を問わず「オッズ未提供」として黙って進めていた。
        /// そのため、COMの競合(-202)や認証エラー(-301)が起きていても
        /// 「まだ発売前」と同じ見え方になり、切り分けができなかった。
        /// </summary>
        private int _lastOpenReturnCode;

        private Dictionary<int, JvRecordParser.TanshoOdds> FetchTanshoOdds(RaceKey race)
        {
            Dictionary<int, JvRecordParser.TanshoOdds> byUmaban = null;

            int rc = _source.OpenRealtime(RealtimeOddsDataSpec, race.AsJvRealtimeKey());
            _lastOpenReturnCode = rc;
            if (rc != 0)
            {
                // Openに対するCloseを必ず呼ぶ。呼ばずに抜けると以降のOpenが -202 で失敗し続ける。
                _source.Close();
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

                    if (JvRecordParser.GetRecordTypeId(buffer) != "O1") continue;

                    var (_, parsed) = JvRecordParser.ParseTanshoOdds(buffer);
                    if (parsed.Count > 0) byUmaban = parsed; // 後から届いたものほど新しい
                }
            }
            finally
            {
                _source.Close();
            }

            return byUmaban;
        }
    }
}
