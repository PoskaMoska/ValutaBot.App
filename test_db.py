import sqlite3
conn = sqlite3.connect('ml_service/data/ValutaTicks.db')
print('Total feedbacks:', conn.execute('SELECT count(*) FROM OnlineFeedback').fetchone()[0])
