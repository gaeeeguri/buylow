/*
 * stockapp: the KIS app key, app secret and WebSocket approval key, from the backend's 0600 secrets file
 * (stockapp KIS spec §5.2). Reread when the file changes (the backend renews the approval key), and after a
 * refused approval key (Invalidate).
 */

using System;
using System.IO;
using MyTrading.Stockapp;
using Newtonsoft.Json.Linq;

namespace MyTrading.Kis
{
    public class KisSecrets
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private DateTime _stamp = DateTime.MinValue;
        private string _appKey, _appSecret, _approval;

        public KisSecrets(string path) { _path = path; }

        public string AppKey { get { Load(); return _appKey; } }
        public string AppSecret { get { Load(); return _appSecret; } }
        public string ApprovalKey { get { Load(); return _approval; } }

        public void Invalidate() { lock (_lock) { _stamp = DateTime.MinValue; } }

        private void Load()
        {
            lock (_lock)
            {
                var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
                if (stamp == DateTime.MinValue)
                    throw new StockappException("stockapp KIS secrets file is missing: the backend isn't running");
                if (stamp == _stamp && _appKey != null) return;
                var doc = JObject.Parse(File.ReadAllText(_path));
                _appKey = doc.Value<string>("app_key");
                _appSecret = doc.Value<string>("app_secret");
                _approval = doc.Value<string>("approval_key");
                if (string.IsNullOrEmpty(_appKey) || string.IsNullOrEmpty(_appSecret))
                    throw new StockappException("stockapp KIS secrets file has no app key or secret");
                _stamp = stamp;
            }
        }
    }
}
