import re

with open('MiniApp/Services/RealtimeTickCollector.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace(
    'public DateTime OpenTime { get; set; }',
    'public DateTime OpenTime { get; set; }\n            public MiniAppController.OhlcCandle? LastClosed { get; set; }'
)

content = content.replace(
    'public void Reset(DateTime openTime)\n            {\n                Open = null;',
    'public void Reset(DateTime openTime)\n            {\n                if (Open.HasValue) LastClosed = new MiniAppController.OhlcCandle(Open.Value, High, Low, Close, TickCount, OpenTime);\n                Open = null;'
)

old_live_acc = '''                if (liveAcc != null)
                {
                    // D1-4 FIX: Convert both sides to DateTime (UTC ticks) before comparing.
                    // Old code: Convert.ToString(r.OpenTime) == liveOpenTimeStr
                    //   - Culture-dependent: on Railway Linux (non en-US) the DB DateTime
                    //     formats differently than "o" (ISO-8601), so RemoveAll never matched
                    //     - liveAcc candle AND the same DB candle both appeared - duplicate last candle.
                    DateTime liveOpenTime = liveAcc.OpenTime.ToUniversalTime();
                    records.RemoveAll(r =>
                    {
                        DateTime dbTime = r.OpenTime is string s
                            ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                            : Convert.ToDateTime(r.OpenTime).ToUniversalTime();
                        return dbTime.Ticks == liveOpenTime.Ticks;
                    });
                }'''

new_live_acc = '''                MiniAppController.OhlcCandle? missedClosed = null;
                if (liveAcc != null)
                {
                    DateTime liveOpenTime = liveAcc.OpenTime.ToUniversalTime();
                    records.RemoveAll(r =>
                    {
                        DateTime dbTime = r.OpenTime is string s
                            ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                            : Convert.ToDateTime(r.OpenTime).ToUniversalTime();
                        return dbTime.Ticks == liveOpenTime.Ticks;
                    });

                    if (liveAcc.LastClosed != null)
                    {
                        long lastClosedTicks = liveAcc.LastClosed.Timestamp.ToUniversalTime().Ticks;
                        bool found = false;
                        foreach (var r in records)
                        {
                            DateTime dbTime = r.OpenTime is string s
                                ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                                : Convert.ToDateTime(r.OpenTime).ToUniversalTime();
                            if (dbTime.Ticks == lastClosedTicks) { found = true; break; }
                        }
                        if (!found) missedClosed = liveAcc.LastClosed;
                    }
                }'''
content = content.replace(old_live_acc, new_live_acc)

content = content.replace('int totalCount = records.Count + (liveAcc != null ? 1 : 0);',
                          'int totalCount = records.Count + (missedClosed != null ? 1 : 0) + (liveAcc != null ? 1 : 0);')

content = content.replace('int dbRecordsToTake = Math.Min(records.Count, resultSize - (liveAcc != null ? 1 : 0));',
                          'int dbRecordsToTake = Math.Min(records.Count, resultSize - (liveAcc != null ? 1 : 0) - (missedClosed != null ? 1 : 0));')

old_insert = '''                if (liveAcc != null && resultIdx < resultSize)
                {
                    lock (liveAcc)
                    {
                        if (liveAcc.Open.HasValue)
                        {
                            result[resultIdx++] = new MiniAppController.OhlcCandle(
                                liveAcc.Open.Value, liveAcc.High, liveAcc.Low, liveAcc.Close, liveAcc.TickCount, liveAcc.OpenTime);
                        }
                    }
                }'''

new_insert = '''                if (missedClosed != null && resultIdx < resultSize)
                {
                    result[resultIdx++] = missedClosed;
                }
                if (liveAcc != null && resultIdx < resultSize)
                {
                    lock (liveAcc)
                    {
                        if (liveAcc.Open.HasValue)
                        {
                            result[resultIdx++] = new MiniAppController.OhlcCandle(
                                liveAcc.Open.Value, liveAcc.High, liveAcc.Low, liveAcc.Close, liveAcc.TickCount, liveAcc.OpenTime);
                        }
                    }
                }'''
content = content.replace(old_insert, new_insert)

old_sig = '''public static Task OnPriceUpdateAsync(string asset, double price)
        {
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
            long nowTicks = DateTime.UtcNow.Ticks;'''
new_sig = '''public static Task OnPriceUpdateAsync(string asset, double price, long tickTimeUtc)
        {
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
            long nowTicks = tickTimeUtc;'''
content = content.replace(old_sig, new_sig)

content = content.replace('public static class RealtimeTickCollector\n    {',
                          'public static class RealtimeTickCollector\n    {\n        public static event Action<string, string>? OnCandleClosed;')

old_reset = '''                    acc.Reset(openTime);
                else if'''
new_reset = '''                    acc.Reset(openTime);
                    OnCandleClosed?.Invoke(asset, dict == _s5 ? "s5" : dict == _s10 ? "s10" : dict == _s15 ? "s15" : dict == _s30 ? "s30" : "m1");
                }
                else if'''
content = content.replace(old_reset, new_reset)

with open('MiniApp/Services/RealtimeTickCollector.cs', 'w', encoding='utf-8') as f:
    f.write(content)
