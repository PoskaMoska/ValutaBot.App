import sqlite3
conn = sqlite3.connect('ml_service/data/ValutaTicks.db')
print([r for r in conn.execute("SELECT Asset, COUNT(*) FROM HistoricalCandles GROUP BY Asset")])
