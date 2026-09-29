# -*- coding: utf-8 -*-
with open('MiniApp/Services/NewsCalendarService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('\"\"', '\"')
content = content.replace('var estZone = TimeZoneInfo.FindSystemTimeZoneById(\"Eastern Standard Time\"); // Windows', '''TimeZoneInfo estZone;
                try { estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { estZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }''')

with open('MiniApp/Services/NewsCalendarService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
