/*
 * The access token stockapp's backend keeps in a private file: {"access_token", "expires_at"}.
 * The adapter never issues tokens in stockapp mode (one issuer per client id; stockapp plan
 * decision 6). It rereads the file when it changes, and after a 401 (Invalidate), so a token the
 * backend refreshed is picked up without a restart.
 */

using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MyTrading.Stockapp
{
    public class TokenFileProvider
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private string _token;
        private DateTime _stamp = DateTime.MinValue;

        public TokenFileProvider(string path) { _path = path; }

        public string Token()
        {
            lock (_lock)
            {
                var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
                if (_token == null || stamp != _stamp)
                {
                    if (stamp == DateTime.MinValue)
                        throw new StockappException("stockapp token file is missing: the backend isn't running");
                    var token = JObject.Parse(File.ReadAllText(_path)).Value<string>("access_token");
                    if (string.IsNullOrEmpty(token))
                        throw new StockappException("stockapp token file has no access_token");
                    _token = token;
                    _stamp = stamp;
                }
                return _token;
            }
        }

        /// <summary>After a 401: forget the token so the next call rereads the file.</summary>
        public void Invalidate() { lock (_lock) { _token = null; _stamp = DateTime.MinValue; } }
    }
}
