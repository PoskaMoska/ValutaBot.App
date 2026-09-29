import re

with open('MiniApp/Services/PendingTradeVerificationService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Fix the dynamic Dapper cast
old_code = '''
            if (candle != null)
            {
                var val = candle.Close ?? candle.close ?? candle.close_price;
                if (val != null) {
                    exitPrice = Convert.ToDouble(val);
                }
            }
'''

new_code = '''
            if (candle != null)
            {
                var dict = (System.Collections.Generic.IDictionary<string, object>)candle;
                if (dict.TryGetValue("Close", out var val) || dict.TryGetValue("close", out val) || dict.TryGetValue("close_price", out val))
                {
                    if (val != null)
                    {
                        exitPrice = Convert.ToDouble(val);
                    }
                }
            }
'''

content = content.replace(old_code, new_code)

with open('MiniApp/Services/PendingTradeVerificationService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
