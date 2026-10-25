using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// 同じVPS上で動く複数のシステムが、JV-Link/UmaConnのデータ取得セッション
    /// （Init、およびOpen〜Close）を同時に張らないようにする、プロセス間の排他ロック。
    ///
    /// 背景（2026-10-25）: このVPSでは3つのシステム、およそ17本のタスクが同じJV-Link/UmaConnと
    /// 同じ本番サイトを使っており、毎時0分には最大7本が一斉に起動していた。そのため
    /// 地方側のNVInitが「シェル通知アイコンが削除できません」で失敗したり、取得が極端に遅くなったり
    /// して、scoreが1時間の上限で強制終了されていた。時刻をずらすだけでは、実行時間が日によって
    /// 大きく変わるため再び重なる。そこで、時刻に頼らず「順番待ち」にする。
    ///
    /// 仕組み: 共有のロックファイルを FileShare.Read で開く。開いている間は他のプロセスが
    /// 書き込み付きで開けないので排他になり、プロセスが落ちればOSが自動で解放する
    /// （ロックが残り続けて詰まることがない）。ファイルには保持者の情報を書いてあり、
    /// 保持中でも中身を読める（type コマンド等）。
    ///
    /// 重要な方針: ロックの待ちには必ず上限を付け、超えたらロックなしで続行する（fail-open）。
    /// 他のシステムが長時間ロックを持っていても、結果の反映や予想が止まりきることはない。
    /// その場合は「ロックなし」＝従来と同じ状態に戻るだけで、悪化はしない。
    ///
    /// ロックを協調させるには、同じロックファイルを他のシステムも使う必要がある。
    /// このクラスを入れていないシステムは、run-with-jvlock.ps1 でタスクごと包める。
    /// </summary>
    internal static class JvSessionLock
    {
        private const string LockPathEnvVar = "JV_LOCK_PATH";
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan NoticeAfter = TimeSpan.FromSeconds(10);

        private static string LockPath
        {
            get
            {
                var overridden = Environment.GetEnvironmentVariable(LockPathEnvVar);
                if (!string.IsNullOrEmpty(overridden)) return overridden;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "KeibaJvLock", "jvlink.lock");
            }
        }

        /// <summary>ロックを取る。取れるまで待ち、timeoutを超えたらロックなしで続行する。
        /// 戻り値はDisposeで解放する（複数回Disposeしても安全）。</summary>
        public static IDisposable Acquire(string purpose, TimeSpan timeout)
        {
            var path = LockPath;
            var waitStarted = DateTime.UtcNow;
            var noticed = false;

            while (true)
            {
                FileStream stream = null;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    // 他のプロセス（または同じプロセスの別のソース）がロック中。順番を待つ。
                }
                catch (Exception ex)
                {
                    // 権限不足・パス不正など。待っても直らないので、取得処理は止めずに続行する。
                    Console.WriteLine($"[jvlock] ロックファイルを開けないため、ロックなしで続行します（{purpose}）: {ex.Message}");
                    return NoLock.Instance;
                }

                if (stream != null)
                {
                    WriteOwner(stream, purpose);
                    if (noticed)
                    {
                        Console.WriteLine(
                            $"[jvlock] 取得しました（{purpose}、待ち {(DateTime.UtcNow - waitStarted).TotalSeconds:0}秒）。");
                    }
                    return new Held(stream);
                }

                var elapsed = DateTime.UtcNow - waitStarted;
                if (elapsed >= timeout)
                {
                    Console.WriteLine(
                        $"[jvlock] {timeout.TotalSeconds:0}秒待っても取れなかったため、ロックなしで続行します（{purpose}）。" +
                        $" 保持側: {ReadOwner(path)}");
                    return NoLock.Instance;
                }

                if (!noticed && elapsed >= NoticeAfter)
                {
                    noticed = true;
                    Console.WriteLine(
                        $"[jvlock] 他の処理がJV-Link/UmaConnを使用中のため待機します（{purpose}）。 保持側: {ReadOwner(path)}");
                }

                Thread.Sleep(PollInterval);
            }
        }

        /// <summary>ERROR_SHARING_VIOLATION(32) / ERROR_LOCK_VIOLATION(33)。
        /// DirectoryNotFoundなど、待っても直らないIOExceptionと区別するために見る。</summary>
        private static bool IsSharingViolation(IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33;
        }

        private static void WriteOwner(FileStream stream, string purpose)
        {
            try
            {
                var me = Process.GetCurrentProcess();
                var text = $"{me.ProcessName} pid={me.Id} {purpose} 取得 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.SetLength(0);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception)
            {
                // 保持者の記録は診断用。書けなくても排他自体は有効なので無視する。
            }
        }

        private static string ReadOwner(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    var text = reader.ReadToEnd().Trim();
                    return text.Length > 0 ? text : "(不明)";
                }
            }
            catch (Exception)
            {
                return "(不明)";
            }
        }

        private sealed class Held : IDisposable
        {
            private FileStream _stream;

            public Held(FileStream stream)
            {
                _stream = stream;
            }

            public void Dispose()
            {
                var stream = _stream;
                _stream = null;
                if (stream == null) return;

                try
                {
                    stream.SetLength(0); // 解放後に古い保持者の情報が残らないようにする
                }
                catch (Exception)
                {
                }
                stream.Dispose();
            }
        }

        private sealed class NoLock : IDisposable
        {
            public static readonly NoLock Instance = new NoLock();

            public void Dispose()
            {
            }
        }
    }
}
