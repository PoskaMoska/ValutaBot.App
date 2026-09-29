import re

with open('MiniApp/wwwroot/index.html', 'r', encoding='utf-8') as f:
    content = f.read()

# We want to remove the block that has radarOf
pattern = r"<div style='display:flex; justify-content:space-between; align-items:center; background:rgba\(255,255,255,0\.03\); padding:6px 8px; border-radius:6px;'>\s*<div style='display:flex; align-items:center; gap:6px;'>\s*<span style='font-size:12px;'>.*?</span>\s*<span style='font-size:10px; color:var\(--subtext\); font-weight:600;'>.*?\(\s*OFlow\s*\)</span>\s*</div>\s*<div id='radarOf'.*?</div>\s*</div>"

new_content = re.sub(pattern, "", content, flags=re.DOTALL)

with open('MiniApp/wwwroot/index.html', 'w', encoding='utf-8') as f:
    f.write(new_content)
