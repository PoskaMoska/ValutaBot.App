import sys

with open('MiniApp/Features/MarketAnalysis/Engines/TradeTimeoutEngine.cs', 'r', encoding='utf-8') as f:
    lines = f.readlines()

new_lines = []
for line in lines:
    if 'string timeoutText = $' in line:
        new_lines.append('''        int tfSeconds = 60;
        if (timeframe.Equals("s5", StringComparison.OrdinalIgnoreCase)) tfSeconds = 5;
        else if (timeframe.Equals("s10", StringComparison.OrdinalIgnoreCase)) tfSeconds = 10;
        else if (timeframe.Equals("s15", StringComparison.OrdinalIgnoreCase)) tfSeconds = 15;
        else if (timeframe.Equals("s30", StringComparison.OrdinalIgnoreCase)) tfSeconds = 30;
        else if (timeframe.Equals("m1", StringComparison.OrdinalIgnoreCase)) tfSeconds = 60;
        else if (timeframe.Equals("m5", StringComparison.OrdinalIgnoreCase)) tfSeconds = 300;
        else if (timeframe.Equals("m15", StringComparison.OrdinalIgnoreCase)) tfSeconds = 900;
        else if (timeframe.Equals("h1", StringComparison.OrdinalIgnoreCase)) tfSeconds = 3600;

        int totalSeconds = baseCandles * tfSeconds;
        string timeoutText = "";
        if (totalSeconds < 60) {
            timeoutText = $"{totalSeconds} \\u0441\\u0435\\u043a";
        } else {
            int m = totalSeconds / 60;
            int s = totalSeconds % 60;
            if (s == 0) timeoutText = $"{m} \\u043c\\u0438\\u043d";
            else timeoutText = $"{m} \\u043c\\u0438\\u043d {s} \\u0441\\u0435\\u043a";
        }
''')
    elif 'string reasoning = $' in line and 'Timeout: ' in line:
        new_lines.append('        string reasoning = $"Timeout: {baseCandles}c ({timeoutText}). {dynamicReason}";\n')
    else:
        new_lines.append(line)

with open('MiniApp/Features/MarketAnalysis/Engines/TradeTimeoutEngine.cs', 'w', encoding='utf-8') as f:
    f.writelines(new_lines)
print('Patched successfully')
