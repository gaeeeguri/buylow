using System;

namespace MyTrading.Stockapp
{
    /// <summary>A stockapp-mode precondition failed: a missing token or secrets file, a bad setting.</summary>
    public class StockappException : Exception
    {
        public StockappException(string message) : base(message) { }
    }
}
