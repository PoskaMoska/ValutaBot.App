import re

with open('MiniApp/Controllers/MiniAppController.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_code = "builder.Services.AddHostedService<ValutaBot.MiniApp.Services.AutoTradingScannerService>(); // Enabled for 50k dataset collection"
new_code = "// builder.Services.AddHostedService<ValutaBot.MiniApp.Services.AutoTradingScannerService>(); // Temporarily disabled by user request to save API limits"

content = content.replace(old_code, new_code)

with open('MiniApp/Controllers/MiniAppController.cs', 'w', encoding='utf-8') as f:
    f.write(content)
