import os
import glob
import pandas as pd
import warnings
warnings.filterwarnings('ignore')

xlsx_files = glob.glob('*.xlsx')
for f in xlsx_files:
    print(f"Converting {f} to CSV...")
    try:
        df = pd.read_excel(f)
        csv_name = f.replace('.xlsx', '.csv')
        df.to_csv(csv_name, index=False)
        print(f"Saved {csv_name}")
    except Exception as e:
        print(f"Error converting {f}: {e}")
