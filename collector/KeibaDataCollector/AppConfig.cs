using System;
using System.Configuration;

namespace KeibaDataCollector
{
    // 機密情報は環境変数を優先。App.configの値は非機密の既定値/開発時フォールバックとしてのみ使う。
    internal static class AppConfig
    {
        public static string JvLinkProgId => Get("JvLinkProgId");
        public static string UmaConnProgId => Get("UmaConnProgId");
        public static string JvLinkSoftwareId => Get("JvLinkSoftwareId");

        public static string WordPressBaseUrl => Get("WordPressBaseUrl");
        public static string WordPressUser => Get("WordPressUser");
        public static string WordPressAppPassword => Get("WordPressAppPassword");

        public static TimeSpan RealtimePollInterval =>
            TimeSpan.FromSeconds(int.Parse(Get("RealtimePollIntervalSeconds")));

        // 6ファクター用の履歴DB。秘匿情報ではないため未設定でも起動できるよう既定値を持つ。
        public static string HistoricalDbPath => GetOrDefault("HistoricalDbPath", "data\\historical.sqlite3");

        private static string Get(string key)
        {
            var value = GetOrDefault(key, null);
            if (value != null) return value;

            throw new InvalidOperationException(
                $"設定値 '{key}' が未設定です。環境変数か App.config に設定してください。");
        }

        private static string GetOrDefault(string key, string defaultValue)
        {
            var fromEnv = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

            var fromConfig = ConfigurationManager.AppSettings[key];
            if (!string.IsNullOrEmpty(fromConfig)) return fromConfig;

            return defaultValue;
        }
    }
}
