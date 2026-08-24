/**
 * 競馬予想カスタムビルダー: レース選択 → 指標の重み付け → My総合指数ランキングの即時計算。
 *
 * 計算はすべてブラウザ内で完結させる（サーバーへの再計算リクエストは発生しない）。
 * サーバー側は「このレースのデータを返してよいか」の可否判定だけを行う。
 */
(function () {
    'use strict';

    var LEVELS = [
        { mult: 0, label: 'OFF' },
        { mult: 1.0, label: '通常' },
        { mult: 1.5, label: '重視' },
        { mult: 2.0, label: '最重要' },
    ];

    var MARKS = ['◎', '○', '▲', '△', '☆'];
    var MARK_LABELS = ['本命推奨', '対抗候補', '単穴特注', '連下候補', '穴馬注視'];

    var root = document.querySelector('.hrc-builder');
    if (!root || typeof hrcConfig === 'undefined') {
        return;
    }

    var step2 = root.querySelector('#hrc-step2');
    var step3 = root.querySelector('#hrc-step3');
    var resultEl = root.querySelector('#hrc-result');
    var lockedNotice = root.querySelector('#hrc-locked-notice');
    var unlockBtn = root.querySelector('#hrc-line-unlock-btn');
    var factorButtons = root.querySelectorAll('.hrc-factor-btn');

    var currentHorses = null;

    function factorLabel(btn) {
        var level = LEVELS[parseInt(btn.dataset.level, 10)];
        btn.textContent = btn.dataset.label + (level.mult > 0 ? ' (x' + level.mult + ')' : '');
        btn.classList.toggle('is-on', level.mult > 0);
    }

    factorButtons.forEach(factorLabel);

    factorButtons.forEach(function (btn) {
        btn.addEventListener('click', function () {
            var next = (parseInt(btn.dataset.level, 10) + 1) % LEVELS.length;
            btn.dataset.level = next;
            factorLabel(btn);
            renderRanking();
        });
    });

    function currentWeights() {
        var weights = {};
        factorButtons.forEach(function (btn) {
            weights[btn.dataset.key] = LEVELS[parseInt(btn.dataset.level, 10)].mult;
        });
        return weights;
    }

    function renderRanking() {
        if (!currentHorses) {
            return;
        }
        var weights = currentWeights();

        // 加重平均で算出する（合計ではない）。合計だと、たまたま算出可能なファクターが
        // 多い馬（母集団不足等でnullになりにくい馬）が、実際の質と関係なく高得点になって
        // しまうため（例: 6ファクター中4つ算出できた馬 と 6つとも算出できた馬 を単純合計で
        // 比べると、後者は「データが揃っていた」だけで加点され続けてしまう）。
        // ONにした（重み>0の）ファクターのうち、その馬でnullでないものだけを使い、
        // 重み付き平均＝Σ(値×重み) / Σ(重み) を取ることで、算出できたファクターの数に
        // 関係なく公平に比較できるようにする。
        var scored = currentHorses.map(function (h) {
            var weightedSum = 0;
            var weightTotal = 0;
            Object.keys(weights).forEach(function (key) {
                var base = h[key];
                var weight = weights[key];
                if (weight > 0 && typeof base === 'number') {
                    weightedSum += base * weight;
                    weightTotal += weight;
                }
            });
            var score = weightTotal > 0 ? weightedSum / weightTotal : 0;
            return { horse: h, score: score };
        });

        scored.sort(function (a, b) {
            return b.score - a.score;
        });

        var rows = scored.map(function (s, i) {
            var mark = i < MARKS.length ? MARKS[i] : '';
            var markLabel = i < MARK_LABELS.length ? MARK_LABELS[i] : '';
            return '<tr>' +
                '<td>' + (i + 1) + '位</td>' +
                '<td>' + s.horse.umaban + '</td>' +
                '<td>' + escapeHtml(s.horse.horseName) + '</td>' +
                '<td>' + s.score.toFixed(1) + ' 点</td>' +
                '<td>' + mark + ' ' + markLabel + '</td>' +
                '</tr>';
        }).join('');

        resultEl.innerHTML =
            '<table class="hrc-result-table">' +
            '<thead><tr><th>順位</th><th>馬番</th><th>馬名</th><th>My総合指数</th><th>評価判定</th></tr></thead>' +
            '<tbody>' + rows + '</tbody>' +
            '</table>';
    }

    function escapeHtml(s) {
        var div = document.createElement('div');
        div.textContent = s || '';
        return div.innerHTML;
    }

    root.querySelectorAll('.hrc-race-btn').forEach(function (btn) {
        btn.addEventListener('click', function () {
            root.querySelectorAll('.hrc-race-btn').forEach(function (b) {
                b.classList.remove('is-active');
            });
            btn.classList.add('is-active');

            var raceKey = btn.dataset.raceKey;
            fetch(hrcConfig.restBase + '/race-data?race_key=' + encodeURIComponent(raceKey))
                .then(function (res) {
                    if (res.status === 403) {
                        step2.hidden = true;
                        step3.hidden = true;
                        lockedNotice.hidden = false;
                        return null;
                    }
                    lockedNotice.hidden = true;
                    return res.json();
                })
                .then(function (data) {
                    if (!data) {
                        return;
                    }
                    currentHorses = data.horses;
                    step2.hidden = false;
                    step3.hidden = false;
                    renderRanking();
                });
        });
    });

    if (unlockBtn) {
        unlockBtn.addEventListener('click', function () {
            var url = hrcConfig.restBase + '/line/login-url?redirect=' + encodeURIComponent(hrcConfig.currentUrl);
            fetch(url)
                .then(function (res) {
                    return res.json();
                })
                .then(function (data) {
                    if (data && data.url) {
                        window.location.href = data.url;
                    }
                });
        });
    }

    // ログインチャネルに公式アカウントを紐付けられず、LINE側の自動友だち追加プロンプトが
    // 使えないため、ログイン直後の1回だけこちらで案内を出す。
    (function initAddFriendBanner() {
        var params = new URLSearchParams(window.location.search);
        if (params.get('hrc_unlocked') !== '1' || !hrcConfig.addFriendUrl) {
            return;
        }

        var banner = root.querySelector('#hrc-add-friend-banner');
        var link = root.querySelector('#hrc-add-friend-link');
        var dismiss = root.querySelector('#hrc-add-friend-dismiss');
        if (banner && link) {
            link.href = hrcConfig.addFriendUrl;
            banner.hidden = false;
            if (dismiss) {
                dismiss.addEventListener('click', function () {
                    banner.hidden = true;
                });
            }
        }

        params.delete('hrc_unlocked');
        var query = params.toString();
        var cleanUrl = window.location.pathname + (query ? '?' + query : '') + window.location.hash;
        window.history.replaceState({}, '', cleanUrl);
    })();
})();
