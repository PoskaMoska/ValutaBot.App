import re

with open('MiniApp/Services/TwelveDataWebSocketStream.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Change SilenceThresholdSeconds from 30 to 300
content = content.replace('const int SilenceThresholdSeconds = 30;', 'const int SilenceThresholdSeconds = 300;')

# Change the delay to 30s instead of 10s so it doesn't spin too fast
content = content.replace('await Task.Delay(10_000, watchdogCts.Token);', 'await Task.Delay(30_000, watchdogCts.Token);')

with open('MiniApp/Services/TwelveDataWebSocketStream.cs', 'w', encoding='utf-8') as f:
    f.write(content)
