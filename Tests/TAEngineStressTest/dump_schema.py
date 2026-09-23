import sqlite3
conn = sqlite3.connect(r'C:\Users\bural\source\repos\ValutaBot.App\ml_service\data\ValutaTicks.db')
for row in conn.execute("SELECT sql FROM sqlite_master WHERE type='table'").fetchall():
    print(row[0])
