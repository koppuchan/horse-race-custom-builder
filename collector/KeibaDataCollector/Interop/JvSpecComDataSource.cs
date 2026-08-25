using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JV-Link / UmaConn 共通の後期バインド(late-bound)COMラッパー。
    /// 両者は同一のインターフェース仕様に準拠しているが、メソッド名のプレフィックスが異なる
    /// （JV-Link: JV*, UmaConn: NV*）。実機のCOM型情報を確認して判明した対応表:
    ///   JVInit  ⇔ NVInit,  JVOpen ⇔ NVOpen,  JVRead ⇔ NVRead,  JVClose ⇔ NVClose,
    ///   JVRTOpen ⇔ NVRTOpen,  JVSetUIProperties ⇔ NVSetUIProperties,
    ///   JVSetServiceKey ⇔ NVSetServiceKey 等（PowerShellから New-Object -ComObject で
    ///   Get-Member して両COMオブジェクトのメソッド一覧を比較し確認済み）。
    /// そのためProgIDに加えてmethodPrefix（"JV"/"NV"）を渡す。
    ///
    /// 後期バインド（Type.GetTypeFromProgID + InvokeMember）を採用している理由:
    /// Visual StudioでCOM参照を追加すると型名は実行環境ごとに生成されるインタロップアセンブリに依存するため、
    /// この環境（Windows/SDK未導入）では正確な生成型名を検証できない。ProgIDは比較的安定した文字列のため、
    /// こちらを採用して環境非依存にしている。
    /// 実行時にCOMイベント（JVWatchEvent等）を使う場合は後期バインドでは購読できないため、
    /// このクラスではポーリング方式（Read()を一定間隔で呼ぶ）を前提にしている。
    /// </summary>
    public class JvSpecComDataSource : IRaceDataSource
    {
        private readonly string _progId;
        private readonly string _methodPrefix;
        private object _com;
        private Type _type;

        public string SourceName { get; }

        public JvSpecComDataSource(string progId, string methodPrefix, string sourceName)
        {
            _progId = progId;
            _methodPrefix = methodPrefix;
            SourceName = sourceName;
        }

        // JVInit/NVInitがシェル通知アイコン(タスクトレイ)のタイムアウトで落ちたときの再試行回数と間隔。
        // 詳細はInitializeのコメント参照。
        private const int InitMaxAttempts = 4;
        private static readonly TimeSpan InitRetryDelay = TimeSpan.FromSeconds(15);

        /// <summary>
        /// JVInit/NVInitを呼ぶ。COM例外で失敗した場合は間隔を空けて数回まで再試行する。
        ///
        /// 再試行が必要な理由（実機で発生し、Web上の技術情報で裏付けを取った内容）:
        /// JV-Link/UmaConnのInitは内部でタスクトレイのアイコンを操作しており、Windowsの
        /// Shell_NotifyIcon APIを呼んでいる。このAPIはシェル(explorer)のトレイウィンドウへ
        /// SendMessageTimeout でメッセージを送る実装になっていて、タイムアウトは4秒
        /// （Windows Vista以降のバージョンでは7秒）と短い。CPUやディスクが混んでいて
        /// シェルが時間内に応答できないと、APIはFALSE（GetLastError=1460 ERROR_TIMEOUT）を返し、
        /// UmaConn側はこれを「シェル通知アイコンが削除できません」というCOMエラーとして投げてくる。
        ///
        /// 実機の症状もこれと一致していた:
        ///   - JV-Link側で数十万件をダウンロード・パースした直後にUmaConnをInitすると失敗する
        ///     （マシンが最も忙しいタイミング）
        ///   - JV-Link側が即座に失敗して何もダウンロードしなかった回は、続くUmaConnのInitは成功した
        ///   - VPS再起動直後（OS自体がまだ忙しい）も失敗した
        ///
        /// このタイムアウトは一時的な状態なので、Microsoftのドキュメント/解説でも
        /// 「しばらく待って再試行する」のが推奨される対処とされている。恒久的な失敗
        /// （ソフトウェアID不正など、Initが負の戻り値を返すケース）は再試行しても無意味なため
        /// 区別し、そちらは即座に例外にする。
        /// </summary>
        public void Initialize(string softwareId)
        {
            EnsureComObject();

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    // JVInit/NVInit(string sid) -- sidはJRA-VANソフトウェアID相当。
                    int rc = (int)Invoke("Init", softwareId);
                    if (rc < 0)
                    {
                        // APIとしては呼べたが業務的に失敗（IDが違う等）。再試行しても直らない。
                        throw new InvalidOperationException($"{SourceName} {_methodPrefix}Init failed: {rc}");
                    }
                    break;
                }
                catch (Exception ex) when (IsTransientComFailure(ex) && attempt < InitMaxAttempts)
                {
                    Console.WriteLine(
                        $"[{SourceName}] {_methodPrefix}Init がCOMエラーで失敗（{attempt}/{InitMaxAttempts}回目）。" +
                        $"シェル通知アイコンのタイムアウトなど一時的な要因の可能性が高いため、" +
                        $"{InitRetryDelay.TotalSeconds:0}秒後に再試行します: {InnermostMessage(ex)}");
                    System.Threading.Thread.Sleep(InitRetryDelay);
                }
            }

            SuppressPayoutDialog();
        }

        /// <summary>後期バインド(InvokeMember)経由のCOM例外はTargetInvocationExceptionに包まれて
        /// 飛んでくるため、内側を辿ってCOMExceptionかどうかを判定する。
        /// InvalidOperationException（上でrc&lt;0のときに自分で投げるもの）は再試行対象にしない。</summary>
        private static bool IsTransientComFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is COMException) return true;
            }
            return false;
        }

        private static string InnermostMessage(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        /// <summary>
        /// 払戻ダイアログを表示しないようにする（払戻フラグ = 1:表示しない）。
        ///
        /// タスクスケジューラからの無人実行では、モーダルダイアログが出るとユーザー操作待ちで
        /// プロセスが止まり、以降の取得が全て停止してしまうため明示的に抑止する。
        ///
        /// 実機のCOM型情報を確認して判明した点:
        ///   m_payflag は読み取り専用プロパティ（int m_payflag() {get}）で代入できない。
        ///   設定には JVSetPayFlag(int) / NVSetPayFlag(int) メソッドを使う
        ///   （JVSetSaveFlag と同じ形。JV-Link/UmaConn 双方に存在することを確認済み）。
        ///
        /// この設定はレジストリに保存され、setupダイアログの「払戻連絡を表示する」の
        /// チェックを外すのと同じ効果を持つ。
        /// なお「JRA-VANからのお知らせ」の表示有無は別設定のため、setupダイアログ側で
        /// オフにする必要がある。
        /// </summary>
        private void SuppressPayoutDialog()
        {
            try
            {
                int rc = (int)Invoke("SetPayFlag", 1);
                if (rc != 0)
                    Console.WriteLine($"[{SourceName}] 払戻ダイアログ抑止に失敗（続行）: rc={rc}");
            }
            catch (Exception ex)
            {
                // このメソッドが無い実装でもデータ取得自体は続行できるため、警告に留める。
                Console.WriteLine($"[{SourceName}] 払戻ダイアログ抑止の呼び出しに失敗（続行）: {ex.Message}");
            }
        }

        public void RunInteractiveSetup()
        {
            // JVSetUIProperties/NVSetUIProperties() -- 利用キー入力ダイアログを表示し、設定を保存する。
            // 初回のみ手動実行想定（自動実行フローには組み込まない）。
            // Initより前に呼べる想定のため、Initialize()を経由せずCOMオブジェクトだけ用意する。
            EnsureComObject();
            Invoke("SetUIProperties");
        }

        private void EnsureComObject()
        {
            if (_com != null) return;

            _type = Type.GetTypeFromProgID(_progId);
            if (_type == null)
            {
                throw new InvalidOperationException(
                    $"COMオブジェクト '{_progId}' が見つかりません。" +
                    $"{SourceName} がこのPCにインストール・登録されているか確認してください。");
            }
            _com = Activator.CreateInstance(_type);
        }

        public OpenResult Open(string dataSpec, string fromTime, DataOption option)
        {
            // JVOpen/NVOpen(string dataspec, string fromtime, int option,
            //        out int readcount, out int downloadcount, out string lastfiletimestamp)
            // readcount/downloadcount/lastfiletimestampはout引数のため、ByRefでのInvokeが必要
            // （実機で確認: ByRef指定なしだと書き戻しが行われず常に呼び出し前の値のまま）。
            var args = new object[] { dataSpec, fromTime, (int)option, 0, 0, "" };
            int rc = (int)InvokeByRef("Open", args, isByRef: new[] { false, false, false, true, true, true });

            return new OpenResult
            {
                ReturnCode = rc,
                ReadCount = SafeInt(args[3]),
                DownloadCount = SafeInt(args[4]),
                LastFileTimestamp = args[5] as string ?? string.Empty,
            };
        }

        private const int ReadBufferSize = 110000;

        public int Read(out string buffer, out string fileName)
        {
            // JVGets/NVGets(out byte[] buff, in long size, out string filename) を使う。
            // JVRead/NVRead（string版）は、実機で検証した結果、後期バインドのByRef文字列
            // マーシャリングでShift-JISバイト列が壊れる問題が確認された（BSTR化の過程で
            // 元のバイトが失われ、事後的な文字コード変換では復元できないケースがあった）。
            // JV-Linkインターフェース仕様書に「JVGetsはSJISをSJISのまま渡すことにより、
            // JV-Link内部での変換およびアプリケーション側でのUNICODE→SJIS変換が不要になり
            // コード変換におけるオーバーヘッドがなくなりました」と明記されている通り、
            // バイト配列で直接受け取ることで文字コード変換の問題自体を回避する。
            // buff には「空の配列」を渡すこと。ここを事前確保した大きな配列にしてはいけない。
            //
            // 仕様書のJVGetsの項より:
            //   「データが格納されたBYTE型配列がセットされるポインタを指定します」
            //   「メモリ受け渡しをバイト配列型のポインタで行い、そのポインタに対して
            //     メモリエリアを確保して渡す方法になります」
            //   「JVGetsではメモリの解放を行わないので、アプリケーション側で読み出しの度に
            //     解放する必要があります」
            // つまり領域はJV-Link側が確保し、こちらが渡した配列は解放されない（出力専用引数）。
            // 付属のVB6サンプルでも未確保の動的配列（Dim bytData() As Byte）を渡している。
            //
            // 実機で発生した障害: ここで毎回 new byte[110000] を渡していたため、
            // 1レコード読むたびに110KBが解放されずに積み上がり、監視を数時間続けたところで
            // ヒープ破損（終了コード -1073740940 = 0xC0000374）でプロセスが異常終了した。
            // UmaConn終了時に出ていた「Unexpected Memory Leak」ダイアログも同じ原因。
            // 戻ってきた配列は.NETのマーシャラがbyte[]へ変換した後に解放するため、
            // 仕様書が求める「読み出しの度の解放」も満たされる。
            var args = new object[] { new byte[0], ReadBufferSize, string.Empty };
            int rc = (int)InvokeByRef("Gets", args, isByRef: new[] { true, false, true });

            // ここでは Shift_JIS ではなく Latin-1 でデコードする。
            // JVData_Struct.cs の SetDataB は、渡された文字列を Str2Byte で再びバイト列へ戻し、
            // 仕様書どおりのバイト位置で各フィールドを切り出す作りになっている。
            // Shift_JIS でデコードすると バイト→文字列→バイト の往復が可逆にならず
            // （未定義バイト列が '?' に潰れて1バイト減る）、以降のフィールドが全てズレる。
            // Latin-1 は 0x00〜0xFF を U+0000〜U+00FF に一対一対応させるため往復が完全に可逆で、
            // SetDataB 側が元のバイト列をそのまま復元できる。
            // 日本語への変換は SetDataB 内の MidB2S が切り出し後に Shift_JIS で行う。
            // 配列はJV-Link側が確保したものなので、戻り値のバイト数と実際の長さが
            // 食い違う可能性を考慮する。そのままGetStringに渡すと範囲外で例外になり、
            // 監視ループごと落ちてしまうため、短い方に合わせる。
            var rawBytes = args[0] as byte[];
            var length = (rawBytes == null) ? 0 : Math.Min(rc, rawBytes.Length);
            buffer = length > 0
                ? Encoding.GetEncoding(28591).GetString(rawBytes, 0, length)
                : string.Empty;
            fileName = args[2] as string ?? string.Empty;
            return rc;
        }

        public int OpenRealtime(string dataSpec, string key)
        {
            // JVRTOpen/NVRTOpen(string dataspec, string key) -- リアルタイム系データ種別コードは仕様書で要確認
            // （速報オッズ/中間成績/確定成績/払戻 等でコードが分かれている）。
            return (int)Invoke("RTOpen", dataSpec, key);
        }

        public void Close()
        {
            if (_com == null) return;
            try
            {
                Invoke("Close");
            }
            catch (Exception ex)
            {
                // Dispose経路から呼ばれるため、後片付けの失敗でアプリ全体を落とさない。
                Console.WriteLine($"[{SourceName}] Close失敗（無視して続行）: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Close();
            if (_com != null && Marshal.IsComObject(_com))
                Marshal.FinalReleaseComObject(_com);
            _com = null;
        }

        private object Invoke(string methodSuffix, params object[] args)
        {
            var method = _methodPrefix + methodSuffix;
            return _type.InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                null,
                _com,
                args);
        }

        /// <summary>
        /// out引数を含むCOMメソッド用。Type.InvokeMemberは既定ではByRef引数の書き戻しを保証しないため
        /// （実機で確認: JVOpen/JVReadのout引数が常に空/初期値のまま返ってきた）、
        /// ParameterModifierで明示的にByRefを指定して呼び出す。呼び出し後、argsの該当要素が更新される。
        /// </summary>
        private object InvokeByRef(string methodSuffix, object[] args, bool[] isByRef)
        {
            var method = _methodPrefix + methodSuffix;
            var modifier = new ParameterModifier(args.Length);
            for (int i = 0; i < args.Length; i++)
                modifier[i] = isByRef[i];

            return _type.InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                Type.DefaultBinder,
                _com,
                args,
                new[] { modifier },
                null,
                null);
        }

        private static int SafeInt(object value) => value is int i ? i : 0;
    }
}
