import re

with open('MiniApp/Controllers/MiniAppController.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Find the msg string and replace it
new_msg = 'string msg = $"' + '?? <b>ValutaBot Успешно Запущен!</b>\\n\\n?? <b>Системная диагностика:</b>\\n- C# Core: ? ОК\\n- PostgreSQL: {dbStatus}\\n- Python ML: {mlStatus}\\n- WebSocket: {tiingoStatus}\\n\\n<i>Бот полностью в сети и мониторит рынок.</i>";'

content = re.sub(r'string msg =.*?;"', new_msg, content, flags=re.DOTALL)
content = re.sub(r'string msg = \$\"\"??.*?;', new_msg, content, flags=re.DOTALL)

with open('MiniApp/Controllers/MiniAppController.cs', 'w', encoding='utf-8') as f:
    f.write(content)
