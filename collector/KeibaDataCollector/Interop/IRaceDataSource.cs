using System;

namespace KeibaDataCollector.Interop
{
    /// <summary>
    /// JV-Link（中央競馬）とUmaConn（地方競馬DATA）は同一仕様
    /// （JV-Link Interface Specification）のCOMコンポーネントのため、
    /// 同じインターフェースで両方を扱う。
    /// </summary>
    public interface IRaceDataSource : IDisposable
    {
        string SourceName { get; }

        void Initialize(string softwareId);

        /// <summary>JVSetUIProperties相当。初回のみ手動実行し、利用キー等をGUIで設定・保存する。</summary>
        void RunInteractiveSetup();

        OpenResult Open(string dataSpec, string fromTime, DataOption option);

        /// <summary>
        /// 1レコード読み込む。戻り値: 0=全件読込完了 / 負値=ファイル切替や制御コード / 正値=読み込みバイト数。
        /// 正確な戻り値の意味はJV-Link仕様書のJVRead節を参照して確定させること。
        /// </summary>
        int Read(out string buffer, out string fileName);

        void Close();

        int OpenRealtime(string dataSpec, string key);
    }

    public class OpenResult
    {
        public int ReturnCode { get; set; }
        public int ReadCount { get; set; }
        public int DownloadCount { get; set; }
        public string LastFileTimestamp { get; set; }
    }

    public enum DataOption
    {
        Normal = 1,
        ThisWeekAndToday = 2,
        Setup = 3,
        SetupThisWeek = 4,
    }
}
