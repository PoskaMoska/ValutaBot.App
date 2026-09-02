import re

with open('MiniApp/Services/MarketDataFetcher.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# We will replace the tdResult block
old_block = '''        var tdResult = await TwelveDataService.FetchCandlesAsync(cleanAsset, interval, limit);
        
        if (tdResult != null)
            return tdResult.Value.candles;'''

new_block = '''        var tdResult = await TwelveDataService.FetchCandlesAsync(cleanAsset, interval, limit);
        
        if (tdResult != null)
        {
            var res = tdResult.Value.candles;
            if (res.Length > 0)
            {
                var last = res[^1];
                TimeSpan span = TimeSpan.FromMinutes(1);
                if (rawInterval.EndsWith("m", StringComparison.OrdinalIgnoreCase) && int.TryParse(rawInterval.Replace("m", "").Replace("M", ""), out int m)) span = TimeSpan.FromMinutes(m);
                else if (rawInterval.EndsWith("h", StringComparison.OrdinalIgnoreCase) && int.TryParse(rawInterval.Replace("h", "").Replace("H", ""), out int h)) span = TimeSpan.FromHours(h);
                else if (rawInterval.EndsWith("d", StringComparison.OrdinalIgnoreCase) && int.TryParse(rawInterval.Replace("d", "").Replace("D", ""), out int d)) span = TimeSpan.FromDays(d);
                
                // If it's a live unclosed candle, drop it
                if (DateTime.UtcNow < last.Timestamp.Add(span))
                {
                    BotLogger.Info($"[Anti-Repaint] Dropped forming {rawInterval} candle (OpenTime: {last.Timestamp:HH:mm:ss}) to enforce closed-candle rule.");
                    return res.Take(res.Length - 1).ToArray();
                }
            }
            return res;
        }'''

content = content.replace(old_block, new_block)

with open('MiniApp/Services/MarketDataFetcher.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print('MarketDataFetcher patched successfully')
