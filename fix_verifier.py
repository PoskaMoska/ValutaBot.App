import os
import re

file = 'MiniApp/Services/PendingTradeVerificationService.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

new_query = '''
            var candle = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<dynamic>(conn, @"
                SELECT close_price as ""Close""
                FROM subminute_candles
                WHERE asset = @Asset AND interval = @Interval
                  AND open_time <= @VerifyAt
                ORDER BY open_time DESC LIMIT 1
            ", new { 
'''

text = re.sub(r'SELECT close_price as Close.*?FROM subminute_candles', 'SELECT close_price as \"Close\"\n                FROM subminute_candles', text, flags=re.DOTALL)

# Let's also make it robust:
robust_cast = '''
            if (candle != null)
            {
                var val = candle.Close ?? candle.close ?? candle.close_price;
                if (val != null) {
                    exitPrice = Convert.ToDouble(val);
                }
            }
'''
text = re.sub(r'if \(candle != null\)\s*\{\s*exitPrice = \(double\)candle\.Close;\s*\}', robust_cast.strip(), text, flags=re.DOTALL)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

