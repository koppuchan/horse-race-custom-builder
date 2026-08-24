<?php
/**
 * Plugin Name: 競馬予想カスタムビルダー
 * Description: 既存の Keiba Race Sync（race カスタム投稿タイプ）のデータに、プロ厳選6ファクターを重ね合わせ、
 *              ユーザーが重み付けした「My総合指数」をクライアント側で即時算出・表示する。LINEログインで全レース解放。
 * Version: 0.4.0
 */

if (!defined('ABSPATH')) {
    exit;
}

define('HRC_VERSION', '0.4.0');
define('HRC_ASSET_VER', '0.4.0');
define('HRC_FACTOR_KEYS', array(
    'param_bias', 'param_pace', 'param_agari_q',
    'param_jockey_roi', 'param_pedigree_fit', 'param_training_acc',
));

/**
 * このプラグインは race カスタム投稿タイプ自体は登録しない（Keiba Race Sync 側の責務）。
 * race_card / race_key など既存メタを読むだけで、新規に書き込むのは hrc_factors と hrc_free_race のみ。
 * 別バッチが race_card を書き換えても衝突しないよう、6ファクターは独立したメタキーに保持する。
 */
// 優先度20で登録する（既定の10より遅らせる）。'init'に同じ優先度で複数のプラグインが
// フックしている場合、WordPressはプラグインの読み込み順（基本的にフォルダ名のアルファベット順）で
// 実行する。"horse-race-custom-builder" は "keiba-race-sync" より辞書順で先に来るため、
// 既定の優先度のままだと race 投稿タイプが登録される前にここが走ってしまい、
// post_type_exists('race') が false になって何も登録されない
// （実機で発生: register_post_metaが効かず、REST APIのmetaにhrc_factorsが一切出ず、
//  WordPressClientからの書き込みは200 OKを返すのに中身が保存されないという不具合になった）。
add_action('init', function () {
    if (!post_type_exists('race')) {
        return;
    }

    register_post_meta('race', 'hrc_factors', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'hrc_sanitize_json_meta',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    // その日の無料公開レースを手動指定するためのフラグ（line_only と対になる考え方）。
    // 発走時刻データが収集アプリ側に無いため、時刻順の完全自動判定ができない。
    // 未指定の日は「当日いちばん早く投稿されたレース」を暫定の無料レースとして扱う。
    register_post_meta('race', 'hrc_free_race', array(
        'type' => 'boolean',
        'single' => true,
        'show_in_rest' => true,
        'default' => false,
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));
}, 20);

function hrc_sanitize_json_meta($value)
{
    if (is_array($value) || is_object($value)) {
        return wp_json_encode($value);
    }
    $value = (string) $value;
    json_decode($value);
    return json_last_error() === JSON_ERROR_NONE ? $value : '{}';
}

function hrc_decode_meta($post_id, $key, $default = array())
{
    $raw = get_post_meta($post_id, $key, true);
    if (empty($raw)) {
        return $default;
    }
    $decoded = json_decode($raw, true);
    return is_array($decoded) ? $decoded : $default;
}

/* ------------------------------------------------------------------------- *
 * 管理画面：レース編集画面に「無料公開レース」チェックボックスを追加
 * ------------------------------------------------------------------------- */

add_action('add_meta_boxes', function () {
    add_meta_box(
        'hrc_free_race_box',
        'カスタムビルダー：無料公開設定',
        'hrc_render_free_race_box',
        'race',
        'side'
    );
});

function hrc_render_free_race_box($post)
{
    wp_nonce_field('hrc_save_free_race', 'hrc_free_race_nonce');
    $checked = (bool) get_post_meta($post->ID, 'hrc_free_race', true);
    echo '<label><input type="checkbox" name="hrc_free_race" value="1" ' . checked($checked, true, false) . '> ';
    echo 'このレースを本日の無料公開レースにする</label>';
    echo '<p class="description">未指定の日は、当日最初に投稿されたレースが自動的に無料公開になります。</p>';
}

add_action('save_post_race', function ($post_id) {
    if (!isset($_POST['hrc_free_race_nonce']) || !wp_verify_nonce($_POST['hrc_free_race_nonce'], 'hrc_save_free_race')) {
        return;
    }
    if (!current_user_can('edit_post', $post_id)) {
        return;
    }
    if (!empty($_POST['hrc_free_race'])) {
        update_post_meta($post_id, 'hrc_free_race', 1);
    } else {
        delete_post_meta($post_id, 'hrc_free_race');
    }
});

/* ------------------------------------------------------------------------- *
 * 設定画面：LINEログインのチャネル情報
 * ------------------------------------------------------------------------- */

add_action('admin_menu', function () {
    add_options_page(
        '競馬カスタムビルダー設定',
        '競馬カスタムビルダー',
        'manage_options',
        'hrc-settings',
        'hrc_render_settings_page'
    );
});

add_action('admin_init', function () {
    register_setting('hrc_settings', 'hrc_line_channel_id', array('sanitize_callback' => 'sanitize_text_field'));
    register_setting('hrc_settings', 'hrc_line_channel_secret', array('sanitize_callback' => 'sanitize_text_field'));
    register_setting('hrc_settings', 'hrc_line_add_friend_url', array('sanitize_callback' => 'esc_url_raw'));
});

function hrc_render_settings_page()
{
    if (!current_user_can('manage_options')) {
        return;
    }
    ?>
    <div class="wrap">
        <h1>競馬カスタムビルダー設定</h1>
        <p>LINEログイン連携用のチャネル情報を入力してください（LINE Developers Console で発行したもの）。</p>
        <form method="post" action="options.php">
            <?php settings_fields('hrc_settings'); ?>
            <table class="form-table">
                <tr>
                    <th><label for="hrc_line_channel_id">Channel ID</label></th>
                    <td><input type="text" id="hrc_line_channel_id" name="hrc_line_channel_id"
                            value="<?php echo esc_attr(get_option('hrc_line_channel_id')); ?>" class="regular-text"></td>
                </tr>
                <tr>
                    <th><label for="hrc_line_channel_secret">Channel Secret</label></th>
                    <td><input type="password" id="hrc_line_channel_secret" name="hrc_line_channel_secret"
                            value="<?php echo esc_attr(get_option('hrc_line_channel_secret')); ?>" class="regular-text"></td>
                </tr>
                <tr>
                    <th><label for="hrc_line_add_friend_url">友だち追加URL</label></th>
                    <td><input type="text" id="hrc_line_add_friend_url" name="hrc_line_add_friend_url"
                            value="<?php echo esc_attr(get_option('hrc_line_add_friend_url')); ?>" class="regular-text"
                            placeholder="https://line.me/R/ti/p/@xxxxxxx">
                        <p class="description">
                            公式アカウントのBasic ID（例: @153opnml）から作れます。LINEログインチャネルに
                            Messaging APIチャネルを紐付けられなかったため、ログイン時の自動友だち追加プロンプトの
                            代わりに、ログイン直後にこのURLへの案内を表示します。
                        </p></td>
                </tr>
            </table>
            <p>コールバックURL（LINE Developers Console側にこの値を設定してください）：<br>
                <code><?php echo esc_url(rest_url('hrc/v1/line/callback')); ?></code></p>
            <?php submit_button(); ?>
        </form>
    </div>
    <?php
}

/* ------------------------------------------------------------------------- *
 * アンロック状態の管理（署名付きCookie）
 *
 * LINEログイン成功後にこのCookieを発行し、以後のリクエストで検証する。
 * 値そのものを信用せず、サーバー側の秘密鍵（wp_salt）で署名して改ざんを防ぐ。
 * ------------------------------------------------------------------------- */

define('HRC_UNLOCK_COOKIE', 'hrc_unlocked');
define('HRC_UNLOCK_TTL', 60 * 60 * 24 * 30); // 30日。友だち追加済みなら毎回LINE認証させる必要はないため長めに保持。

function hrc_issue_unlock_cookie($line_user_id)
{
    $expires = time() + HRC_UNLOCK_TTL;
    $payload = $line_user_id . '|' . $expires;
    $signature = hash_hmac('sha256', $payload, wp_salt('auth'));
    $value = $payload . '|' . $signature;

    setcookie(HRC_UNLOCK_COOKIE, $value, $expires, COOKIEPATH, COOKIE_DOMAIN, is_ssl(), true);
}

function hrc_is_unlocked()
{
    if (empty($_COOKIE[HRC_UNLOCK_COOKIE])) {
        return false;
    }
    $parts = explode('|', wp_unslash($_COOKIE[HRC_UNLOCK_COOKIE]));
    if (count($parts) !== 3) {
        return false;
    }
    list($line_user_id, $expires, $signature) = $parts;

    if ((int) $expires < time()) {
        return false;
    }
    $expected = hash_hmac('sha256', $line_user_id . '|' . $expires, wp_salt('auth'));
    return hash_equals($expected, $signature);
}

/* ------------------------------------------------------------------------- *
 * LINEログイン：認可URLの発行とコールバック処理
 *
 * TODO: Channel ID / Secret が設定画面に入力され、かつLINE Developers Console側で
 *       このログインチャネルに公式アカウントの「Add friend option」がAggressiveで
 *       紐付けられて初めて、ログイン時の友だち追加導線が機能する（プロバイダーが
 *       同一である必要あり。現在確認中）。
 * ------------------------------------------------------------------------- */

add_action('rest_api_init', function () {
    register_rest_route('hrc/v1', '/line/login-url', array(
        'methods' => 'GET',
        'callback' => 'hrc_rest_line_login_url',
        'permission_callback' => '__return_true',
    ));

    register_rest_route('hrc/v1', '/line/callback', array(
        'methods' => 'GET',
        'callback' => 'hrc_rest_line_callback',
        'permission_callback' => '__return_true',
    ));

    register_rest_route('hrc/v1', '/race-data', array(
        'methods' => 'GET',
        'callback' => 'hrc_rest_race_data',
        'permission_callback' => '__return_true',
        'args' => array(
            'race_key' => array('required' => true),
        ),
    ));
});

function hrc_rest_line_login_url(WP_REST_Request $request)
{
    $channel_id = get_option('hrc_line_channel_id');
    if (empty($channel_id)) {
        return new WP_Error('hrc_not_configured', 'LINEログインが未設定です。', array('status' => 500));
    }

    $state = wp_generate_password(24, false);
    setcookie('hrc_line_state', $state, time() + 600, COOKIEPATH, COOKIE_DOMAIN, is_ssl(), true);

    // 戻り先（アンロックしたいレースのページ）をstateと一緒に往復させたいところだが、
    // stateはCSRF検証専用に単純な乱数のままにし、戻り先はセッションではなくクエリで渡す。
    $redirect_after = $request->get_param('redirect') ? esc_url_raw($request->get_param('redirect')) : home_url('/');
    setcookie('hrc_line_redirect', $redirect_after, time() + 600, COOKIEPATH, COOKIE_DOMAIN, is_ssl(), true);

    $params = array(
        'response_type' => 'code',
        'client_id' => $channel_id,
        'redirect_uri' => rest_url('hrc/v1/line/callback'),
        'state' => $state,
        'scope' => 'openid profile',
        // Messaging APIチャネルをこのログインチャネルに紐付けられていないため、現状は無視される
        // （紐付けが完了すれば追加のコードなしでそのまま効き始める）。それまでの間は
        // hrc_rest_line_callback 側でログイン直後に友だち追加を案内している。
        'bot_prompt' => 'aggressive',
    );

    return array('url' => 'https://access.line.me/oauth2/v2.1/authorize?' . http_build_query($params));
}

function hrc_rest_line_callback(WP_REST_Request $request)
{
    $code = $request->get_param('code');
    $state = $request->get_param('state');
    $expected_state = isset($_COOKIE['hrc_line_state']) ? wp_unslash($_COOKIE['hrc_line_state']) : '';
    $redirect_after = isset($_COOKIE['hrc_line_redirect']) ? wp_unslash($_COOKIE['hrc_line_redirect']) : home_url('/');

    if (empty($code) || empty($state) || !hash_equals($expected_state, $state)) {
        wp_die('認証に失敗しました。お手数ですが最初からやり直してください。');
    }

    $channel_id = get_option('hrc_line_channel_id');
    $channel_secret = get_option('hrc_line_channel_secret');

    $response = wp_remote_post('https://api.line.me/oauth2/v2.1/token', array(
        'body' => array(
            'grant_type' => 'authorization_code',
            'code' => $code,
            'redirect_uri' => rest_url('hrc/v1/line/callback'),
            'client_id' => $channel_id,
            'client_secret' => $channel_secret,
        ),
    ));

    if (is_wp_error($response)) {
        wp_die('LINEとの通信に失敗しました。');
    }

    $body = json_decode(wp_remote_retrieve_body($response), true);
    if (empty($body['id_token'])) {
        wp_die('LINE認証に失敗しました。');
    }

    // id_token(JWT)のpayload部分からsub（LINEユーザーID）だけを取り出す。
    // 署名検証はLINEのトークンエンドポイントをHTTPS経由で直接叩いて得た応答であるため省略している
    // （中間者を信用する前提はトークンエンドポイントへのリクエスト自体にも既にある）。
    $jwt_parts = explode('.', $body['id_token']);
    $payload = json_decode(base64_decode(strtr($jwt_parts[1], '-_', '+/')), true);
    $line_user_id = isset($payload['sub']) ? $payload['sub'] : '';

    if (empty($line_user_id)) {
        wp_die('LINEユーザー情報の取得に失敗しました。');
    }

    hrc_issue_unlock_cookie($line_user_id);

    // ログイン時の自動友だち追加プロンプトが使えないため、ログイン直後の1回だけ
    // フロント側で友だち追加を案内するためのフラグをURLに付与して戻す。
    $redirect_after = add_query_arg('hrc_unlocked', '1', $redirect_after);

    wp_safe_redirect($redirect_after);
    exit;
}

/* ------------------------------------------------------------------------- *
 * レースデータAPI：無料レース or アンロック済みの場合のみ、出走馬 × 6ファクターを返す
 * ------------------------------------------------------------------------- */

function hrc_get_todays_free_race_key()
{
    $ymd = wp_date('Ymd');

    $flagged = get_posts(array(
        'post_type' => 'race',
        'post_status' => 'publish',
        'posts_per_page' => 1,
        'no_found_rows' => true,
        'meta_query' => array(
            array('key' => 'race_key', 'value' => $ymd . '-', 'compare' => 'LIKE'),
            array('key' => 'hrc_free_race', 'value' => '1'),
        ),
    ));
    if (!empty($flagged)) {
        return get_post_meta($flagged[0]->ID, 'race_key', true);
    }

    // 手動指定が無ければ、当日の最小の競馬場コード × 最小のレース番号を暫定の無料レースとする。
    //
    // 投稿日時（post_date）で「一番早く投稿されたレース」を拾う実装を最初に試したが、
    // 実データで検証したところ 20260819-30-12R（門別12R）が最速で投稿されており、
    // 収集アプリの処理順は発走順とは無関係だと分かった。投稿順を無料レース判定に
    // 使うのは誤りなので、race_key から機械的に決まる「最小の場コード・最小レース番号」に変更する。
    // これも本当の発走時刻順ではないが、少なくとも「関係ない後半のレース」が
    // 無料枠になる事故は起きない。
    if (!function_exists('keiba_race_sync_get_races_by_track')) {
        return null;
    }
    $tracks = keiba_race_sync_get_races_by_track($ymd);
    if (empty($tracks)) {
        return null;
    }
    $first_track = reset($tracks);
    $first_race = reset($first_track['races']);
    return $first_race ? $first_race['race_key'] : null;
}

function hrc_rest_race_data(WP_REST_Request $request)
{
    $race_key = $request->get_param('race_key');

    $posts = get_posts(array(
        'post_type' => 'race',
        'post_status' => 'publish',
        'posts_per_page' => 1,
        'no_found_rows' => true,
        'meta_query' => array(
            array('key' => 'race_key', 'value' => $race_key),
        ),
    ));
    if (empty($posts)) {
        return new WP_Error('hrc_not_found', '指定されたレースが見つかりません。', array('status' => 404));
    }
    $post_id = $posts[0]->ID;

    $is_free = ($race_key === hrc_get_todays_free_race_key());
    if (!$is_free && !hrc_is_unlocked()) {
        return new WP_Error(
            'hrc_locked',
            'このレースの診断はLINE登録で解放されます。',
            array('status' => 403)
        );
    }

    $entries = hrc_decode_meta($post_id, 'race_card', array());
    $factors = hrc_decode_meta($post_id, 'hrc_factors', array());

    $horses = array();
    foreach ($entries as $e) {
        $umaban = isset($e['umaban']) ? (string) (int) $e['umaban'] : null;
        if ($umaban === null) {
            continue;
        }
        $f = isset($factors[$umaban]) ? $factors[$umaban] : array();

        $horse = array(
            'umaban' => (int) $umaban,
            'waku' => isset($e['waku']) ? (int) $e['waku'] : null,
            'horseName' => isset($e['horseName']) ? $e['horseName'] : '',
        );
        foreach (HRC_FACTOR_KEYS as $key) {
            $camel = lcfirst(str_replace('_', '', ucwords($key, '_'))); // param_bias -> paramBias
            $horse[$camel] = isset($f[$camel]) ? (float) $f[$camel] : null;
        }
        $horses[] = $horse;
    }

    return array(
        'raceKey' => $race_key,
        'isFree' => $is_free,
        'horses' => $horses,
    );
}

/* ------------------------------------------------------------------------- *
 * ショートコード [keiba_custom_builder]
 * ------------------------------------------------------------------------- */

add_action('wp_enqueue_scripts', function () {
    global $post;
    if (!is_a($post, 'WP_Post') || !has_shortcode($post->post_content, 'keiba_custom_builder')) {
        return;
    }

    wp_enqueue_style('hrc-style', plugins_url('assets/custom-builder.css', __FILE__), array(), HRC_ASSET_VER);
    wp_enqueue_script('hrc-script', plugins_url('assets/custom-builder.js', __FILE__), array(), HRC_ASSET_VER, true);

    wp_localize_script('hrc-script', 'hrcConfig', array(
        'restBase' => esc_url_raw(rest_url('hrc/v1')),
        'freeRaceKey' => hrc_get_todays_free_race_key(),
        'currentUrl' => (is_ssl() ? 'https://' : 'http://') . $_SERVER['HTTP_HOST'] . $_SERVER['REQUEST_URI'],
        'addFriendUrl' => esc_url_raw(get_option('hrc_line_add_friend_url')),
    ));
});

add_shortcode('keiba_custom_builder', function ($atts) {
    if (!function_exists('keiba_race_sync_get_races_by_track')) {
        return '<p class="keiba-selector-empty">Keiba Race Sync プラグインが有効化されていません。</p>';
    }

    $ymd = wp_date('Ymd');
    $tracks = keiba_race_sync_get_races_by_track($ymd);
    $free_race_key = hrc_get_todays_free_race_key();

    ob_start();
    ?>
    <div class="hrc-builder">
        <div class="hrc-step" id="hrc-step1">
            <h3>STEP 1: 分析レースを選択</h3>
            <?php if (empty($tracks)): ?>
                <p class="keiba-selector-empty">本日のレースはまだ公開されていません。</p>
            <?php else: ?>
                <?php foreach ($tracks as $track): ?>
                    <div class="hrc-track-group">
                        <span class="hrc-track-name"><?php echo esc_html($track['name']); ?></span>
                        <?php foreach ($track['races'] as $race): ?>
                            <?php $is_free = ($race['race_key'] === $free_race_key); ?>
                            <button type="button" class="hrc-race-btn <?php echo $is_free ? 'is-free' : 'is-locked'; ?>"
                                data-race-key="<?php echo esc_attr($race['race_key']); ?>">
                                <?php echo esc_html($race['number']); ?>R
                                <?php echo $is_free ? '<span class="hrc-badge">無料</span>' : '<span class="hrc-badge">🔒</span>'; ?>
                            </button>
                        <?php endforeach; ?>
                    </div>
                <?php endforeach; ?>
            <?php endif; ?>
        </div>

        <div class="hrc-step" id="hrc-step2" hidden>
            <h3>STEP 2: 重視するプロ厳選指標を選択（クリックで配分調整）</h3>
            <div class="hrc-factors">
                <button type="button" class="hrc-factor-btn" data-key="paramBias" data-label="枠＆馬場バイアス" data-level="2">枠＆馬場バイアス</button>
                <button type="button" class="hrc-factor-btn" data-key="paramPace" data-label="テン速度＆展開" data-level="2">テン速度＆展開</button>
                <button type="button" class="hrc-factor-btn" data-key="paramAgariQ" data-label="上がり3F質＆末脚" data-level="3">上がり3F質＆末脚</button>
                <button type="button" class="hrc-factor-btn" data-key="paramJockeyRoi" data-label="騎手コース回収率" data-level="0">騎手コース回収率</button>
                <button type="button" class="hrc-factor-btn" data-key="paramPedigreeFit" data-label="血統適性＆妙味" data-level="0">血統適性＆妙味</button>
                <button type="button" class="hrc-factor-btn" data-key="paramTrainingAcc" data-label="調教＆加速ラップ" data-level="0">調教＆加速ラップ</button>
            </div>
        </div>

        <div class="hrc-step" id="hrc-step3" hidden>
            <h3>STEP 3: 診断結果（My総合指数ランキング）</h3>
            <div id="hrc-result"></div>
        </div>

        <div id="hrc-locked-notice" class="hrc-locked-notice" hidden>
            <p>全レースの診断機能は、公式LINE友だち追加で即時アンロックされます。</p>
            <button type="button" id="hrc-line-unlock-btn">LINEで無料アンロック</button>
        </div>

        <div id="hrc-add-friend-banner" class="hrc-add-friend-banner" hidden>
            <p>ログインありがとうございます！最後に公式LINEの友だち追加をお願いします。</p>
            <a href="#" id="hrc-add-friend-link" target="_blank" rel="noopener">友だち追加はこちら</a>
            <button type="button" id="hrc-add-friend-dismiss" aria-label="閉じる">×</button>
        </div>
    </div>
    <?php
    return ob_get_clean();
});
