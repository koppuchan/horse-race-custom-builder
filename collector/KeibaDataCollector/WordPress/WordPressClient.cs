using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using KeibaDataCollector.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace KeibaDataCollector.WordPress
{
    /// <summary>
    /// WordPress REST API 経由でカスタム投稿タイプ "race" を作成・更新するクライアント。
    /// 認証は Application Passwords（WP標準機能、WP 5.6+）を使用する。
    ///
    /// 前提（WordPress側で別途必要な準備。src/wordpress-plugin/keiba-race-sync が対応）:
    ///  - カスタム投稿タイプ "race" を show_in_rest=true で登録
    ///  - race_key, race_card, race_result, payouts, corner_passage を
    ///    register_post_meta で REST 経由の読み書きを許可
    ///  - 対象ユーザーでアプリケーションパスワードを発行し、環境変数 WordPressAppPassword に設定
    ///
    /// race_card/race_result/payouts/corner_passage は、WPのREST metaスキーマ検証が
    /// 任意ネスト構造の配列を安定して受け付けないため、camelCaseキーのJSON文字列として送る
    /// （WP側は register_post_meta の type を "string" で登録し、表示時にjson_decodeする）。
    /// </summary>
    public class WordPressClient
    {
        private static readonly JsonSerializerSettings CamelCaseSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
        };

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public WordPressClient(string baseUrl, string username, string applicationPassword)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient();
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{applicationPassword}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        public async Task UpsertRaceCardAsync(RaceKey key, List<RaceCardEntry> raceCardEntries)
        {
            var existing = await FindPostByRaceKeyAsync(key.AsSlug());

            // 既に結果が入っている投稿には出走表のタイトルを被せない。
            // 朝一バッチを結果反映後に再実行した場合（手動での試運転や、出馬表の訂正が
            // 後から届いた場合など）、タイトルだけ「出走表」に戻り、本文は結果表という
            // 不整合になるため。表示側は race_result があれば結果表を出す作りなので、
            // タイトルもそれに合わせる。
            var suffix = existing != null && existing.HasRaceResult ? "結果" : "出走表";

            var payload = new
            {
                title = $"{key.RaceDate:yyyy/MM/dd} {key.TrackCode} {key.RaceNumber}R {suffix}",
                status = "publish",
                meta = new
                {
                    race_key = key.AsSlug(),
                    race_card = JsonConvert.SerializeObject(raceCardEntries, CamelCaseSettings),
                }
            };
            await SendAsync(existing?.Id, payload);
        }

        /// <summary>
        /// 予想生成でこのレースを飛ばしてよいか。
        ///
        /// 飛ばす条件は2つ。
        ///  ・既に「揃った」予想が入っている（最初に取れた値を残すため上書きしない）
        ///  ・既に結果が出ている
        ///
        /// 後者は速さのためだけではない。終わったレースのオッズは確定オッズなので、
        /// それで印を付けると「結果を見てから当てた予想」を載せることになる。
        /// 発走前に出せなかったレースは、予想なしのままにしておくのが正しい。
        ///
        /// 「揃った」を条件にしているのは、印が欠けたまま固定されるのを防ぐため。
        /// 実際に大井2R（5頭立て）で、オッズ配信が始まった直後の不完全な状態を掴み、
        /// ◎1頭だけの予想が確定してしまった（2026-08-12）。
        /// 欠けている場合は次の回で取り直して上書きする。
        /// </summary>
        public async Task<bool> ShouldSkipPredictionAsync(RaceKey key, int requiredMarks)
        {
            var existing = await FindPostByRaceKeyAsync(key.AsSlug());
            if (existing == null) return false;
            return existing.HasRaceResult || existing.PredictionMarkCount >= requiredMarks;
        }

        /// <summary>
        /// 予想印（馬番 => ◎○▲△）をWordPressへ反映する。
        ///
        /// predictions だけを送る。出走表や結果を一緒に送ると、予想生成の時点では
        /// まだ確定していない項目を空で上書きしてしまう。
        /// 投稿がまだ無い場合は新規作成する（朝一バッチより先に走っても取りこぼさない）。
        /// LINE限定フラグはサイト側で設定する項目のため、こちらからは一切送らない
        /// ＝毎朝の予想更新で消えることはない。
        ///
        /// 既にある予想と同数以上でなければ書き込まない。これで
        ///  ・揃った予想が、あとから発走直前のオッズで塗り替えられることを防ぎ
        ///  ・欠けた予想は、揃った内容で上書きできる
        /// の両方を満たす。戻り値は実際に書き込んだかどうか。
        /// </summary>
        public async Task<bool> UpsertPredictionsAsync(RaceKey key, Dictionary<int, string> marks)
        {
            var existing = await FindPostByRaceKeyAsync(key.AsSlug());
            // 既にある予想より少ない（＝より不完全な）内容で上書きしない。
            if (existing != null && existing.PredictionMarkCount >= marks.Count) return false;

            var suffix = existing != null && existing.HasRaceResult ? "結果" : "出走表";

            // キーは馬番。JSONでは文字列キーになるため、表示側は文字列・数値の両方を見る作り。
            var byUmaban = new Dictionary<string, string>();
            foreach (var mark in marks)
                byUmaban[mark.Key.ToString()] = mark.Value;

            var payload = new
            {
                title = $"{key.RaceDate:yyyy/MM/dd} {key.TrackCode} {key.RaceNumber}R {suffix}",
                status = "publish",
                meta = new
                {
                    race_key = key.AsSlug(),
                    predictions = JsonConvert.SerializeObject(byUmaban),
                }
            };
            await SendAsync(existing?.Id, payload);
            return true;
        }

        /// <summary>6ファクター（hrc_factors、馬番=>{paramBias等}）をWordPressへ反映する。
        /// predictionsと同じ「馬番文字列キー」の取り決め。race_card等の既存メタは一切送らない
        /// （UpsertPredictionsAsyncと同じ理由: 別バッチが持つ項目を上書きしない）。
        /// nullのファクターは送らない（未算出であることが分かるよう、フィールド自体を省く）。</summary>
        /// <returns>反映したらtrue。対象の投稿がまだ無くて見送った場合はfalse。</returns>
        public async Task<bool> UpsertFactorsAsync(RaceKey key, Dictionary<int, FactorScores> scoresByUmaban)
        {
            var existing = await FindPostByRaceKeyAsync(key.AsSlug());

            // 投稿が無い場合は「作らずに見送る」。ここで新規作成してしまうと、出走表(race_card)が
            // 空のまま hrc_factors だけを持つ投稿ができてしまう。
            // フロント側は馬名・馬番を race_card から組み立てているため、そういう投稿は
            // レース選択欄には出るのに中身が空、という状態になる
            // （実機で発生: 笠松の全10レースがこの状態になり、「出走馬名が表示されない」と
            //  お客様からご指摘をいただいた。朝一バッチが出走表を作る前に score が走ると起きる）。
            //
            // 出走表を作るのは朝一バッチ(UpsertRaceCardAsync)の役目で、score はあくまで
            // 既存のレース投稿に6ファクターを重ねるだけ、という責務分担にしておく。
            // 見送ったレースは、朝一バッチが出走表を作った後に score を再実行すれば反映される
            // （scoreは1日に複数回動かす前提のため、次の実行で自然に拾える）。
            if (existing == null)
            {
                return false;
            }

            var suffix = existing.HasRaceResult ? "結果" : "出走表";

            var byUmaban = new Dictionary<string, object>();
            foreach (var kv in scoresByUmaban)
            {
                var s = kv.Value;
                var fields = new Dictionary<string, double>();
                if (s.ParamBias.HasValue) fields["paramBias"] = s.ParamBias.Value;
                if (s.ParamPace.HasValue) fields["paramPace"] = s.ParamPace.Value;
                if (s.ParamAgariQ.HasValue) fields["paramAgariQ"] = s.ParamAgariQ.Value;
                if (s.ParamJockeyRoi.HasValue) fields["paramJockeyRoi"] = s.ParamJockeyRoi.Value;
                if (s.ParamPedigreeFit.HasValue) fields["paramPedigreeFit"] = s.ParamPedigreeFit.Value;
                if (s.ParamTrainingAcc.HasValue) fields["paramTrainingAcc"] = s.ParamTrainingAcc.Value;
                byUmaban[kv.Key.ToString()] = fields;
            }

            var payload = new
            {
                title = $"{key.RaceDate:yyyy/MM/dd} {key.TrackCode} {key.RaceNumber}R {suffix}",
                status = "publish",
                meta = new
                {
                    race_key = key.AsSlug(),
                    hrc_factors = JsonConvert.SerializeObject(byUmaban),
                }
            };
            await SendAsync(existing.Id, payload);
            return true;
        }

        // レースキーごとに、最後に送信した内容を保持する。watchモードは確定するまで同じレースを
        // 繰り返しポーリングするため、これが無いと内容が1文字も変わっていなくても
        // ポーリング間隔ごとにWordPressへ書き込み続けてしまう（実機で確認: 速報段階のまま
        // 止まっているレースが20秒ごとに同一内容で更新され、投稿の最終更新日時だけが
        // 無意味に進み続けていた）。
        private readonly Dictionary<string, string> _lastPublishedResult = new Dictionary<string, string>();

        /// <summary>レース結果をWordPressへ反映する。前回送信時から内容が変わっていない場合は
        /// 何もせず false を返す（無駄なAPI呼び出しと投稿更新を避けるため）。</summary>
        public async Task<bool> PublishRaceResultAsync(RaceResult result)
        {
            var slug = result.Key.AsSlug();
            var raceResultJson = JsonConvert.SerializeObject(result.Entries, CamelCaseSettings);
            var payoutsJson = JsonConvert.SerializeObject(result.Payouts, CamelCaseSettings);
            var cornerPassageJson = JsonConvert.SerializeObject(result.CornerPassage, CamelCaseSettings);

            var signature = string.Join("", raceResultJson, payoutsJson, cornerPassageJson);
            if (_lastPublishedResult.TryGetValue(slug, out var previous) && previous == signature)
                return false;

            var existing = await FindPostByRaceKeyAsync(slug);
            var payload = new
            {
                title = $"{result.Key.RaceDate:yyyy/MM/dd} {result.Key.TrackCode} {result.Key.RaceNumber}R 結果",
                status = "publish",
                meta = new
                {
                    race_key = slug,
                    race_result = raceResultJson,
                    payouts = payoutsJson,
                    corner_passage = cornerPassageJson,
                }
            };
            await SendAsync(existing?.Id, payload);

            // 送信に成功した場合のみ記録する（失敗時は次回リトライさせたいため）。
            _lastPublishedResult[slug] = signature;
            return true;
        }

        /// <summary>race_key が一致する既存投稿を探す。無ければ null。</summary>
        private async Task<ExistingRacePost> FindPostByRaceKeyAsync(string raceKeySlug)
        {
            // race_key をmeta_queryで検索できるようWordPress側にカスタムRESTフィルタを用意している
            // （src/wordpress-plugin/keiba-race-sync の rest_race_query フィルタ）。
            var response = await _http.GetAsync($"{_baseUrl}/wp-json/wp/v2/race?meta_key=race_key&meta_value={raceKeySlug}");
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync();
            var posts = JsonConvert.DeserializeObject<WpPost[]>(body);
            if (posts == null || posts.Length == 0) return null;

            var post = posts[0];

            return new ExistingRacePost
            {
                Id = post.Id,
                // メタは未設定だと "" や "[]"、"{}" になりうるため、中身があるかどうかで判定する。
                HasRaceResult = HasContent(post.Meta?.RaceResult),
                PredictionMarkCount = CountPredictionMarks(post.Meta?.Predictions),
            };
        }

        private static bool HasContent(string metaValue)
        {
            if (string.IsNullOrWhiteSpace(metaValue)) return false;
            var trimmed = metaValue.Trim();
            return trimmed != "[]" && trimmed != "{}";
        }

        /// <summary>予想メタ（馬番 => 印）に入っている印の数。未設定なら0。</summary>
        private static int CountPredictionMarks(string metaValue)
        {
            if (!HasContent(metaValue)) return 0;
            try
            {
                var marks = JsonConvert.DeserializeObject<Dictionary<string, string>>(metaValue);
                return marks?.Count ?? 0;
            }
            catch (JsonException)
            {
                // 壊れた値が入っていても取得処理は止めない。0件扱いにして次の回で上書きさせる。
                return 0;
            }
        }

        private class ExistingRacePost
        {
            public int Id { get; set; }
            public bool HasRaceResult { get; set; }
            public int PredictionMarkCount { get; set; }
        }

        // 一時的なサーバー側エラーで送信が失敗したときの再試行回数と間隔。
        private const int SendMaxAttempts = 4;
        private static readonly TimeSpan SendRetryDelay = TimeSpan.FromSeconds(10);

        /// <summary>このHTTPステータスなら「サーバー側の一時的な不調」とみなして再送する。
        ///
        /// 実機で発生: 共有ホスティング(ConoHa WING)が混雑時に 503 Service Unavailable を返すことがあり、
        /// 既存システムの朝一バッチがこれを1回踏んだだけで、その後の全レースの出走表が
        /// 作られないまま異常終了していた（笠松が丸ごと欠けた原因。同日の別バッチでも503を確認）。
        /// この手のエラーは数秒後に再送すれば通ることがほとんどなので、諦める前に粘る。
        ///
        /// 認証エラー(401/403)やバリデーションエラー(400)は再送しても結果が変わらないため対象外。</summary>
        private static bool IsTransientHttpStatus(System.Net.HttpStatusCode status) =>
            status == System.Net.HttpStatusCode.ServiceUnavailable      // 503
            || status == System.Net.HttpStatusCode.BadGateway           // 502
            || status == System.Net.HttpStatusCode.GatewayTimeout       // 504
            || status == System.Net.HttpStatusCode.RequestTimeout       // 408
            || (int)status == 429;                                      // Too Many Requests

        private async Task SendAsync(int? existingId, object payload)
        {
            var json = JsonConvert.SerializeObject(payload);

            var path = existingId.HasValue
                ? $"{_baseUrl}/wp-json/wp/v2/race/{existingId.Value}"
                : $"{_baseUrl}/wp-json/wp/v2/race";

            for (int attempt = 1; ; attempt++)
            {
                // StringContentは送信のたびに作り直す（使い回すと2回目以降でストリームが
                // 既に読み終わった状態になり、再送が必ず失敗する）。
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await _http.PostAsync(path, content);
                }
                catch (HttpRequestException ex) when (attempt < SendMaxAttempts)
                {
                    // 接続断など。これも一時的なことが多いので同様に再送する。
                    Console.WriteLine(
                        $"[WordPress] 送信に失敗（{attempt}/{SendMaxAttempts}回目、通信エラー）。" +
                        $"{SendRetryDelay.TotalSeconds:0}秒後に再送します: {ex.Message}");
                    await Task.Delay(SendRetryDelay);
                    continue;
                }

                if (response.IsSuccessStatusCode) return;

                var body = await response.Content.ReadAsStringAsync();

                if (IsTransientHttpStatus(response.StatusCode) && attempt < SendMaxAttempts)
                {
                    Console.WriteLine(
                        $"[WordPress] 送信に失敗（{attempt}/{SendMaxAttempts}回目、{(int)response.StatusCode} " +
                        $"{response.StatusCode}）。サーバー側の一時的な不調とみなし、" +
                        $"{SendRetryDelay.TotalSeconds:0}秒後に再送します。");
                    await Task.Delay(SendRetryDelay);
                    continue;
                }

                throw new InvalidOperationException($"WordPress API failed ({response.StatusCode}): {body}");
            }
        }

        private class WpPost
        {
            [JsonProperty("id")]
            public int Id { get; set; }

            [JsonProperty("meta")]
            public WpPostMeta Meta { get; set; }
        }

        private class WpPostMeta
        {
            [JsonProperty("race_result")]
            public string RaceResult { get; set; }

            [JsonProperty("predictions")]
            public string Predictions { get; set; }
        }
    }
}
