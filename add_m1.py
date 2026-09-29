import re

with open('MiniApp/Services/AutoTradingScannerService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Add m1 to _targetTimeframes
old_tfs = 'private static readonly string[] _targetTimeframes = { "s5", "s10", "s15", "s30" };'
new_tfs = 'private static readonly string[] _targetTimeframes = { "s5", "s10", "s15", "s30", "m1" };'

content = content.replace(old_tfs, new_tfs)

# Also update the log message
old_log = 'Will scan 6 pairs on s5/s10/s15/s30.'
new_log = 'Will scan 6 pairs on s5/s10/s15/s30/m1.'
content = content.replace(old_log, new_log)

with open('MiniApp/Services/AutoTradingScannerService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
