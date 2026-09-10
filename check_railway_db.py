import os
import psycopg2
from collections import defaultdict

def main():
    db_url = os.environ.get("DATABASE_URL")
    if not db_url:
        print("❌ ОШИБКА: Переменная DATABASE_URL не найдена.")
        print("Скопируйте Postgres Connection URL из Railway и запустите скрипт так:")
        print("  set DATABASE_URL=postgres://... && python check_railway_db.py (Windows)")
        print("  DATABASE_URL=postgres://... python check_railway_db.py (Linux/Mac)")
        return

    try:
        conn = psycopg2.connect(db_url)
        cur = conn.cursor()
        print("✅ Подключение к Railway PostgreSQL успешно!\n")

        # Проверяем subminute_candles (субминутные тики)
        try:
            cur.execute("""
                SELECT asset, interval, COUNT(*) 
                FROM subminute_candles 
                GROUP BY asset, interval 
                ORDER BY interval, asset
            """)
            rows = cur.fetchall()
            print("📊 ТАБЛИЦА: subminute_candles (Секундные графики: s5, s15 и т.д.)")
            print("-" * 50)
            if not rows:
                print("  Пусто! В базе нет субминутных тиков.")
            for row in rows:
                asset, interval, count = row
                warning = " ⚠️ Мало данных! (<150) -> Включается подмена на 1m" if count < 150 else " ✅ Достаточно"
                print(f"  {asset} [{interval}]: {count} свечей {warning}")
        except Exception as e:
            print(f"  Ошибка чтения subminute_candles: {e}")
            conn.rollback()

        print("\n" + "="*50 + "\n")

        # Проверяем historical_candles (минутные и часовые)
        try:
            cur.execute("""
                SELECT asset, interval, COUNT(*) 
                FROM historical_candles 
                GROUP BY asset, interval 
                ORDER BY interval, asset
            """)
            rows = cur.fetchall()
            print("📊 ТАБЛИЦА: historical_candles (Минутные и выше: 1m, 5m, 15m)")
            print("-" * 50)
            if not rows:
                print("  Пусто!")
            for row in rows:
                asset, interval, count = row
                print(f"  {asset} [{interval}]: {count} свечей")
        except Exception as e:
            print(f"  Ошибка чтения historical_candles: {e}")

    except Exception as e:
        print(f"❌ Ошибка подключения: {e}")
    finally:
        if 'conn' in locals() and conn:
            conn.close()

if __name__ == "__main__":
    main()
