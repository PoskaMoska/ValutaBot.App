import psycopg2
import pandas as pd

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def get_summary(date_str):
    query = f"""
    SELECT asset, timeframe, direction, was_win, pnl_bps
    FROM trade_outcomes
    WHERE DATE(created_at) = '{date_str}'
    """
    with psycopg2.connect(DB_URL) as conn:
        df = pd.read_sql_query(query, conn)
    
    if df.empty:
        return "No trades for this date."

    total = len(df)
    wins = df['was_win'].sum()
    win_rate = (wins / total) * 100
    pnl = df['pnl_bps'].sum()

    output = f"Summary for {date_str}:\n"
    output += f"Total Trades: {total}\n"
    output += f"Wins: {wins} ({win_rate:.2f}%)\n"
    output += f"Total PnL: {pnl:.2f} BPS\n\n"

    output += "By Asset:\n"
    asset_group = df.groupby('asset').agg(
        trades=('was_win', 'count'),
        wins=('was_win', 'sum'),
        pnl=('pnl_bps', 'sum')
    )
    asset_group['win_rate'] = (asset_group['wins'] / asset_group['trades']) * 100
    output += asset_group.to_string() + "\n\n"

    output += "By Timeframe:\n"
    tf_group = df.groupby('timeframe').agg(
        trades=('was_win', 'count'),
        wins=('was_win', 'sum'),
        pnl=('pnl_bps', 'sum')
    )
    tf_group['win_rate'] = (tf_group['wins'] / tf_group['trades']) * 100
    output += tf_group.to_string() + "\n\n"

    output += "By Direction:\n"
    dir_group = df.groupby('direction').agg(
        trades=('was_win', 'count'),
        wins=('was_win', 'sum'),
        pnl=('pnl_bps', 'sum')
    )
    dir_group['win_rate'] = (dir_group['wins'] / dir_group['trades']) * 100
    output += dir_group.to_string() + "\n\n"

    return output

print(get_summary('2026-09-28'))
print(get_summary('2026-09-29'))
