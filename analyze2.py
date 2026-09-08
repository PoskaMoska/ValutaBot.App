# -*- coding: utf-8 -*-
import pandas as pd
import os
import json

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
    df = pd.read_excel(f)
    print(f"\n=== {alias} ({f}) ===")
    print("COLUMNS:", list(df.columns))
    print("SAMPLE 3 rows:")
    print(df.head(3).to_string())
    print("DTYPES:", df.dtypes.to_string())
    print()