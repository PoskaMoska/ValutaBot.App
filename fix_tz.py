import re

with open('MiniApp/Services/NewsCalendarService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_tz = 'var estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); // Windows'
new_tz = '''TimeZoneInfo estZone;
                try { estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { estZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }'''

content = content.replace(old_tz, new_tz)

with open('MiniApp/Services/NewsCalendarService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
