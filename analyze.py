# -*- coding: utf-8 -*-
import pandas as pd
import os

files = [
    'меньше минуты.xlsx',
    'меньше минуты, 2.xlsx',
    'меньше минуты 3.xlsx',
    'меньше минуты 4.xlsx',
    '1м 3.xlsx'
]

for f in files:
    if not os.path.exists(f):
        print(f'File {f} not found.')
        continue
    df = pd.read_excel(f)
    profits = None
    for c in df.columns:
        # Check if column has positive and negative numbers (typical for profit)
        if pd.api.types.is_numeric_dtype(df[c]):
            if (df[c] < 0).any():
                profits = df[c]
                break
    
    if profits is None:
        # Maybe it's stored as strings with $ or just take the last numeric
        numeric_cols = df.select_dtypes(include=['number']).columns
        if len(numeric_cols) > 0:
            profits = df[numeric_cols[-1]]

    if profits is not None:
        profits = profits.dropna()
        wins = (profits > 0).sum()
        losses = (profits <= 0).sum()
        total = wins + losses
        winrate = (wins / total) * 100 if total > 0 else 0
        
        # Split into two halves to check degradation
        half = total // 2
        first_half = profits.iloc[:half]
        second_half = profits.iloc[half:]
        
        w1 = (first_half > 0).sum()
        t1 = len(first_half)
        wr1 = (w1/t1)*100 if t1 > 0 else 0
        
        w2 = (second_half > 0).sum()
        t2 = len(second_half)
        wr2 = (w2/t2)*100 if t2 > 0 else 0
        
        print(f'{f}:')
        print(f'  Total Trades: {total} (Wins: {wins}, Losses: {losses}) -> WinRate: {winrate:.1f}%')
        print(f'  First Half WinRate: {wr1:.1f}% ({w1}/{t1})')
        print(f'  Second Half WinRate: {wr2:.1f}% ({w2}/{t2})')
        print('-'*30)