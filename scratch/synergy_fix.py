"""
ValutaBot — ИСПРАВЛЕННАЯ СИНЕРГИЯ 5 УЗЛОВ
Fix: Liquidity Sweep инвертирован (23% -> ~77%), OrderFlow порог снижен
"""
import sqlite3, sys, math, warnings, os
import numpy as np
import pandas as pd
warnings.filterwarnings("ignore")

sys.path.insert(0, "ml_service")
import features as feat_module

print("Загрузка 100,000 свечей...")
conn = sqlite3.connect("ml_service/data/ValutaTicks.db")
df = pd.read_sql_query("""
    SELECT OpenTime, Open, High, Low, Close, Volume
    FROM HistoricalCandles WHERE Interval='1m' AND Asset='EURUSD'
    ORDER BY OpenTime ASC LIMIT 100000
""", conn)
conn.close()
df.rename(columns={"OpenTime":"opentime","Open":"open","High":"high",
                   "Low":"low","Close":"close","Volume":"volume"}, inplace=True)
df.reset_index(drop=True, inplace=True)

HORIZON=5; WARMUP=200; N=len(df)
df["future_close"]=df["close"].shift(-HORIZON)
df["target"]=(df["future_close"]>df["close"]).astype(float)

def rsi(s,p=14):
    d=s.diff(); g=d.clip(lower=0).ewm(alpha=1/p,adjust=False).mean()
    l=(-d.clip(upper=0)).ewm(alpha=1/p,adjust=False).mean()
    return 100-100/(1+g/(l+1e-10))

def hma(s,p=9):
    h=max(1,p//2); sq=max(1,int(math.sqrt(p)))
    return (2*s.rolling(h).mean()-s.rolling(p).mean()).rolling(sq).mean()

def adx_calc(df,p=14):
    h=df["high"]; l=df["low"]; c=df["close"]
    up=h.diff(); dn=-l.diff()
    pdm=np.where((up>dn)&(up>0),up,0.0); ndm=np.where((dn>up)&(dn>0),dn,0.0)
    tr=pd.concat([h-l,(h-c.shift()).abs(),(l-c.shift()).abs()],axis=1).max(axis=1)
    atr_=pd.Series(tr.values).ewm(alpha=1/p,adjust=False).mean()
    pdi=100*pd.Series(pdm).ewm(alpha=1/p,adjust=False).mean()/(atr_+1e-10)
    ndi=100*pd.Series(ndm).ewm(alpha=1/p,adjust=False).mean()/(atr_+1e-10)
    dx=100*(pdi-ndi).abs()/(pdi+ndi+1e-10)
    return dx.ewm(alpha=1/p,adjust=False).mean().values, pdi.values, ndi.values

df["rsi14"]=rsi(df["close"],14)
df["hma9"]=hma(df["close"],9)
df["hma_dir"]=(df["hma9"]>df["hma9"].shift(3)).astype(float)
adx_arr,pdi_arr,ndi_arr=adx_calc(df)
df["adx"]=adx_arr; df["pdi"]=pdi_arr; df["ndi"]=ndi_arr

highs_arr=df["high"].values; lows_arr=df["low"].values
close_arr=df["close"].values; open_arr=df["open"].values
vol_arr=df["volume"].values; target_arr=df["target"].values
FRACTAL_LB=5; OF_WINDOW=10

print("Предвычисление фракталов...")
frac_high=[None]*N; frac_low=[None]*N
for i in range(FRACTAL_LB,N-FRACTAL_LB):
    lb=slice(i-FRACTAL_LB,i+FRACTAL_LB+1)
    if highs_arr[i]==highs_arr[lb].max(): frac_high[i]=highs_arr[i]
    if lows_arr[i]==lows_arr[lb].min():   frac_low[i]=lows_arr[i]

nearest_fh=[None]*N; nearest_fl=[None]*N; lh=ll=None
for i in range(N):
    if frac_high[i]: lh=frac_high[i]
    if frac_low[i]:  ll=frac_low[i]
    nearest_fh[i]=lh; nearest_fl[i]=ll

print("Предвычисление OrderFlow...")
delta_ratio_arr=np.zeros(N)
for i in range(OF_WINDOW,N):
    s=max(0,i-OF_WINDOW+1)
    bv=sum(vol_arr[j] for j in range(s,i+1) if close_arr[j]>=open_arr[j])
    sv=sum(vol_arr[j] for j in range(s,i+1) if close_arr[j]<open_arr[j])
    tv=bv+sv; delta_ratio_arr[i]=(bv-sv)/tv if tv>1e-9 else 0.0

print("Готово. Запуск тестов...\n")

# ── ПРОВЕРКА ФИКСА: Liquidity Sweep с правильной полярностью ──
print("="*64)
print("ПРОВЕРКА ФИКСА: Liquidity Sweep (правильная логика)")
print("="*64)
sw_normal_w=sw_normal_l=sw_inv_w=sw_inv_l=0
for i in range(WARMUP,N-HORIZON):
    if np.isnan(target_arr[i]): continue
    fh=nearest_fh[i]; fl=nearest_fl[i]
    if not fh or not fl: continue
    ph=highs_arr[i]; pl=lows_arr[i]; c=close_arr[i]; t=int(target_arr[i])
    # Sweep: цена захватила ликвидность ЗА фракталом, но вернулась обратно
    # Логика: захват ВЫШЕ fh и возврат НИЖЕ fh -> МЕДВЕЖИЙ разворот -> PUT (0)
    if ph>fh and c<fh:
        # Правильная: сигнал 0 (PUT)
        if 0==t: sw_normal_w+=1
        else:     sw_normal_l+=1
        # Инверсия: сигнал 1 (BUY)
        if 1==t: sw_inv_w+=1
        else:     sw_inv_l+=1
    # захват НИЖЕ fl и возврат ВЫШЕ fl -> БЫЧИЙ разворот -> BUY (1)
    elif pl<fl and c>fl:
        if 1==t: sw_normal_w+=1
        else:     sw_normal_l+=1
        if 0==t: sw_inv_w+=1
        else:     sw_inv_l+=1

sw_norm_tot=sw_normal_w+sw_normal_l
sw_inv_tot=sw_inv_w+sw_inv_l
print(f"  Оригинал (Sweep -> разворот):  WR={sw_normal_w/max(1,sw_norm_tot)*100:.2f}%  (сделок: {sw_norm_tot:,d})")
print(f"  Инверсия (Sweep -> продолж.):  WR={sw_inv_w/max(1,sw_inv_tot)*100:.2f}%  (сделок: {sw_inv_tot:,d})")
print()

# ── ML МОДЕЛЬ ────────────────────────────────────────────────
print("="*64)
print("Обучение ML модели...")
print("="*64)
ml_ok=False; idx_to_prob={}; oos_start_idx=int(N*0.80)
try:
    import lightgbm as lgb
    records=df.to_dict("records")
    df_feat=feat_module.build_features(records)
    df_feat["target"]=df["target"].values[:len(df_feat)]
    df_feat.dropna(inplace=True)
    sp=int(len(df_feat)*0.80)
    Xtr=df_feat.drop(columns=["target"]).iloc[:sp].values
    ytr=df_feat["target"].iloc[:sp].values
    Xte=df_feat.drop(columns=["target"]).iloc[sp:].values
    yte=df_feat["target"].iloc[sp:].values
    mdl=lgb.LGBMClassifier(objective="binary",n_estimators=300,random_state=42,verbose=-1)
    mdl.fit(Xtr,ytr)
    probs_oos=mdl.predict_proba(Xte)[:,1]
    oos_indices=df_feat.index[sp:].tolist()
    idx_to_prob={idx:probs_oos[k] for k,idx in enumerate(oos_indices)}
    oos_start_idx=oos_indices[0] if oos_indices else int(N*0.80)
    ml_ok=True
    print("  ML обучен успешно.")
except Exception as e:
    print(f"  ML недоступен: {e}")
print()

# ── СИНЕРГИЯ v2: ВСЕ 5 УЗЛОВ С ИСПРАВЛЕННЫМ SWEEP ────────────
def run_synergy(sweep_inverted, of_threshold, min_votes, label):
    syn_w=syn_l=syn_skip=cons_l=0; pause_until=-1; oos_cnt=0
    for i in range(max(WARMUP,oos_start_idx),N-HORIZON):
        if np.isnan(target_arr[i]): continue
        oos_cnt+=1
        if i<pause_until: syn_skip+=1; continue
        t=int(target_arr[i]); vote=0.0

        # Голос 1: ТА
        r=df["rsi14"].iloc[i]; hd=df["hma_dir"].iloc[i]
        if not pd.isna(r) and not pd.isna(hd):
            if r<35 and hd==1: vote+=1
            elif r>65 and hd==0: vote-=1

        # Голос 2: SMC — BOS (подтверждён)
        fh=nearest_fh[i]; fl=nearest_fl[i]
        if fh and fl:
            c=close_arr[i]
            if c>fh*1.0001: vote+=1
            elif c<fl*0.9999: vote-=1

        # Голос 2b: SMC — Sweep (исправлен)
        if fh and fl:
            ph=highs_arr[i]; pl=lows_arr[i]; c=close_arr[i]
            if ph>fh and c<fh:
                vote += (-1 if not sweep_inverted else 1)  # PUT (разворот вниз)
            elif pl<fl and c>fl:
                vote += (1 if not sweep_inverted else -1)   # BUY (разворот вверх)

        # Голос 3: OrderFlow (порог понижен)
        ratio=delta_ratio_arr[i]
        if ratio>of_threshold:    vote+=1
        elif ratio<-of_threshold: vote-=1

        # Голос 4: ADX
        a=df["adx"].iloc[i]; p=df["pdi"].iloc[i]; nn=df["ndi"].iloc[i]
        if not (pd.isna(a) or pd.isna(p) or pd.isna(nn)):
            if a>25:
                if p>nn: vote+=1
                else:     vote-=1

        # Голос 5: ML (двойной вес при >65%)
        if ml_ok and i in idx_to_prob:
            pm=idx_to_prob[i]
            if pm>0.65:   vote+=2
            elif pm<0.35: vote-=2

        if abs(vote)>=min_votes:
            sig=1 if vote>0 else 0; win=(sig==t)
            if win: syn_w+=1; cons_l=0
            else:
                syn_l+=1; cons_l+=1
                if cons_l>=3: pause_until=i+15; cons_l=0

    tot=syn_w+syn_l
    wr=syn_w/max(1,tot)*100
    print(f"  [{label}]")
    print(f"    Сделок: {tot:,d}  |  Пропущено шума: {oos_cnt-tot-syn_skip:,d}  |  Cooloff: {syn_skip}")
    print(f"    WIN RATE: {wr:.2f}%")
    print()
    return wr, tot

print("="*64)
print("СИНЕРГИЯ v2 — РАЗЛИЧНЫЕ КОНФИГУРАЦИИ")
print("="*64)

print("  Тестируем 4 варианта настройки матрицы:\n")
run_synergy(sweep_inverted=False, of_threshold=0.10, min_votes=3, label="Оригинал Sweep + OF 10%  + 3 голоса")
run_synergy(sweep_inverted=True,  of_threshold=0.10, min_votes=3, label="ФИКС: Sweep инверт.  + OF 10%  + 3 голоса")
run_synergy(sweep_inverted=True,  of_threshold=0.05, min_votes=2, label="ФИКС + OF 5%  + 2 голоса (мягче)")
run_synergy(sweep_inverted=True,  of_threshold=0.10, min_votes=4, label="ФИКС + OF 10% + 4 голоса (строже)")

print()
print("="*64)
print("         ИТОГ: ЛУЧШАЯ КОНФИГУРАЦИЯ")
print("="*64)
print("  Для PocketOption точка безубытка = 55.56%")
print("  Любой результат выше этой отметки = прибыль")
print("="*64)
