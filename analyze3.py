# -*- coding: utf-8 -*-
import pandas as pd
import numpy as np
import os
import sys
sys.stdout.reconfigure(encoding='utf-8')

files = {
    'меньше минуты.xlsx': 'ММ_1',
    'меньше минуты, 2.xlsx': 'ММ_2',
    'меньше минуты 3.xlsx': 'ММ_3',
    'меньше минуты 4.xlsx': 'ММ_4',
    '1м 3.xlsx': '1м_3',
}

for f, alias in files.items():
    if not os.path.exists(f):
        continue
    df = pd.read_excel(f, header=0)
    df.columns = ['direction','id','timeframe','asset','open_time','close_time','entry','exit_price','stake','profit','currency']
    df['open_time'] = pd.to_datetime(df['open_time'])
    df = df.sort_values('open_time').reset_index(drop=True)
    df['win'] = df['profit'] > 0
    df['tie'] = df['profit'] == 0

    total = len(df)
    wins = df['win'].sum()
    ties = df['tie'].sum()
    losses = total - wins - ties
    wr = wins / total * 100

    t_start = df['open_time'].min()
    t_end = df['open_time'].max()
    duration_h = (t_end - t_start).total_seconds() / 3600

    print(f"\n{'='*65}")
    print(f"ФАЙЛ: {alias}")
    print(f"  Сделок: {total} | Побед: {wins} | Ничьих: {ties} | Убытков: {losses}")
    print(f"  Общий винрейт: {wr:.1f}%")
    print(f"  Время: {t_start.strftime('%H:%M')} -> {t_end.strftime('%H:%M')} ({duration_h:.1f}ч)")

    # Asset breakdown
    print(f"  Активы:")
    for asset, g in df.groupby('asset'):
        print(f"    [{asset}] {len(g)} сделок, WR={g['win'].mean()*100:.1f}%")

    # Timeframe breakdown
    print(f"  Таймфреймы:")
    for tf, g in df.groupby('timeframe'):
        print(f"    [{tf}] {len(g)} сделок, WR={g['win'].mean()*100:.1f}%")

    # Rolling winrate in windows of 20 trades
    window = 20
    print(f"  Rolling WinRate (окно {window}):")
    for i in range(0, total, window):
        chunk = df.iloc[i:i+window]
        cw = int(chunk['win'].sum())
        ct = len(chunk)
        cwr = cw / ct * 100
        pnl = chunk['profit'].sum()
        bars = int(cwr / 5)
        bar = '#' * bars + '.' * (20 - bars)
        print(f"    [{i+1:3d}-{i+ct:3d}] {bar} {cwr:5.1f}% ({cw}/{ct}) PnL:{pnl:+.0f}")

    # Streak analysis
    streaks_win = []
    streaks_loss = []
    cur_w = 0
    cur_l = 0
    for w in df['win']:
        if w:
            cur_w += 1
            if cur_l > 0:
                streaks_loss.append(cur_l)
                cur_l = 0
        else:
            cur_l += 1
            if cur_w > 0:
                streaks_win.append(cur_w)
                cur_w = 0
    if cur_w > 0: streaks_win.append(cur_w)
    if cur_l > 0: streaks_loss.append(cur_l)

    max_win_streak = max(streaks_win) if streaks_win else 0
    max_loss_streak = max(streaks_loss) if streaks_loss else 0
    avg_win = round(np.mean(streaks_win), 1) if streaks_win else 0
    avg_loss = round(np.mean(streaks_loss), 1) if streaks_loss else 0
    print(f"  Серии побед: макс={max_win_streak}, среднее={avg_win}")
    print(f"  Серии убытков: макс={max_loss_streak}, среднее={avg_loss}")

    # PnL по квартилям
    df['cumPnL'] = df['profit'].cumsum()
    q1 = df['cumPnL'].iloc[total//4 - 1]
    q2 = df['cumPnL'].iloc[total//2 - 1]
    q3 = df['cumPnL'].iloc[3*total//4 - 1]
    q4 = df['cumPnL'].iloc[-1]
    print(f"  Кумулятивный PnL Q1/Q2/Q3/Q4: {q1:+.0f} / {q2:+.0f} / {q3:+.0f} / {q4:+.0f}")