import re

with open('MiniApp/Controllers/MiniAppController.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Add singleton
if 'builder.Services.AddSingleton<INewsCalendarService, NewsCalendarService>();' not in content:
    content = content.replace(
        'builder.Services.AddHostedService<ValutaBot.MiniApp.Services.AutoTradingScannerService>();',
        'builder.Services.AddSingleton<ValutaBot.MiniApp.Services.INewsCalendarService, ValutaBot.MiniApp.Services.NewsCalendarService>();\nbuilder.Services.AddHostedService<ValutaBot.MiniApp.Services.NewsCalendarService>(p => (ValutaBot.MiniApp.Services.NewsCalendarService)p.GetRequiredService<ValutaBot.MiniApp.Services.INewsCalendarService>());\nbuilder.Services.AddHostedService<ValutaBot.MiniApp.Services.AutoTradingScannerService>();'
    )
    with open('MiniApp/Controllers/MiniAppController.cs', 'w', encoding='utf-8') as f:
        f.write(content)
