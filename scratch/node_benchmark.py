"""
ValutaBot — ПОЛНЫЙ ИЗОЛИРОВАННЫЙ ТЕСТ КАЖДОГО УЗЛА (100k свечей)
Узлы: TA · SMC · OrderFlow · ContinuousState · ML
Плюс: Синергия всех 5 узлов вместе
"""
import sqlite3, sys, math, warnings, os
import numpy as np
import pandas as pd
warnings.filterwarnings("ignore")

sys.path.insert(0, "ml_service")
import features as feat_module

# ── ЗАГРУЗКА ДАННЫХ ──────────────────────────────────────────
print("Загрузка 100,000 свечей из SQLite...")
conn = sqlite3.connect("ml_service/data/ValutaTicks.db")
df = pd.read_sql_query("""
    SELECT OpenTime, Open, High, Low, Close, Volume
    FROM HistoricalCandles
    WHERE Interval='1m' AND Asset='EURUSD'
    ORDER BY OpenTime ASC LIMIT 100000
""", conn)
conn.close()
df.rename(columns={"OpenTime":"opentime","Open":"open","High":"high",
                   "Low":"low","Close":"close","Volume":"volume"}, inplace=True)
df.reset_index(drop=True, inplace=True)
print(f"Загружено: {len(df)} свечей\n")

HORIZON = 5; WARMUP = 200; N = len(df)
df["future_close"] = df["close"].shift(-HORIZON)
df["target"] = (df["future_close"] > df["close"]).astype(float)

def rsi(s, p=14):
    d=s.diff(); g=d.clip(lower=0).ewm(alpha=1/p,adjust=False).mean(); l=(-d.clip(upper=0)).ewm(alpha=1/p,adjust=False).mean()
    return 100-100/(1+g/(l+1e-10))

def hma(s, p=9):
    h=max(1,p//2); sq=max(1,int(math.sqrt(p))); raw=2*s.rolling(h).mean()-s.rolling(p).mean()
    return raw.rolling(sq).mean()

def adx_calc(df, p=14):
    h=df["high"]; l=df["low"]; c=df["close"]
    up=h.diff(); dn=-l.diff()
    pdm=np.where((up>dn)&(up>0),up,0.0); ndm=np.where((dn>up)&(dn>0),dn,0.0)
    tr=pd.concat([h-l,(h-c.shift()).abs(),(l-c.shift()).abs()],axis=1).max(axis=1)
    atr_=pd.Series(tr.values).ewm(alpha=1/p,adjust=False).mean()
    pdi=100*pd.Series(pdm).ewm(alpha=1/p,adjust=False).mean()/(atr_+1e-10)
    ndi=100*pd.Series(ndm).ewm(alpha=1/p,adjust=False).mean()/(atr_+1e-10)
    dx=100*(pdi-ndi).abs()/(pdi+ndi+1e-10)
    return dx.ewm(alpha=1/p,adjust=False).mean().values, pdi.values, ndi.values

# Прекомпилируем всё
df["rsi14"]  = rsi(df["close"],14)
df["hma9"]   = hma(df["close"],9)
df["hma_dir"]= (df["hma9"] > df["hma9"].shift(3)).astype(float)
adx_arr,pdi_arr,ndi_arr = adx_calc(df)
df["adx"]=adx_arr; df["pdi"]=pdi_arr; df["ndi"]=ndi_arr

highs_arr=df["high"].values; lows_arr=df["low"].values
close_arr=df["close"].values; open_arr=df["open"].values
vol_arr=df["volume"].values; target_arr=df["target"].values
FRACTAL_LB=5; OF_WINDOW=10

print("Предвычисление фракталов (SMC)...")
frac_high=[None]*N; frac_low=[None]*N
for i in range(FRACTAL_LB, N-FRACTAL_LB):
    lb=slice(i-FRACTAL_LB,i+FRACTAL_LB+1)
    if highs_arr[i]==highs_arr[lb].max(): frac_high[i]=highs_arr[i]
    if lows_arr[i]==lows_arr[lb].min():   frac_low[i]=lows_arr[i]

nearest_fh=[None]*N; nearest_fl=[None]*N
lh=ll=None
for i in range(N):
    if frac_high[i]: lh=frac_high[i]
    if frac_low[i]:  ll=frac_low[i]
    nearest_fh[i]=lh; nearest_fl[i]=ll

print("Предвычисление OrderFlow дельты...")
delta_ratio_arr=np.zeros(N)
for i in range(OF_WINDOW,N):
    s=max(0,i-OF_WINDOW+1)
    bv=sum(vol_arr[j] for j in range(s,i+1) if close_arr[j]>=open_arr[j])
    sv=sum(vol_arr[j] for j in range(s,i+1) if close_arr[j]<open_arr[j])
    tv=bv+sv; delta_ratio_arr[i]=(bv-sv)/tv if tv>1e-9 else 0.0

print("Готово. Запускаем тесты...\n")

# ── УЗЕЛ 1: МАТЕМАТИКА / TA ──────────────────────────────────
print("="*64); print("УЗЕЛ 1 — МАТЕМАТИКА / TA (RSI + HMA)"); print("="*64)
ta_w=ta_l=0
for i in range(WARMUP,N-HORIZON):
    if np.isnan(target_arr[i]): continue
    r=df["rsi14"].iloc[i]; h=df["hma_dir"].iloc[i]
    if pd.isna(r) or pd.isna(h): continue
    sig=None
    if r<35 and h==1:   sig=1
    elif r>65 and h==0: sig=0
    if sig is not None:
        if sig==int(target_arr[i]): ta_w+=1
        else: ta_l+=1
ta_tot=ta_w+ta_l; ta_wr=ta_w/max(1,ta_tot)*100
print(f"  RSI<35/RSI>65 + HMA подтверждение:  WR={ta_wr:5.2f}%  (сделок: {ta_tot:,d})\n")

# ── УЗЕЛ 2: SMC ──────────────────────────────────────────────
print("="*64); print("УЗЕЛ 2 — SMART MONEY CONCEPTS (SMC)"); print("="*64)
bos_w=bos_l=sw_w=sw_l=0
for i in range(WARMUP,N-HORIZON):
    if np.isnan(target_arr[i]): continue
    fh=nearest_fh[i]; fl=nearest_fl[i]
    if not fh or not fl: continue
    c=close_arr[i]; t=int(target_arr[i])
    if   c>fh*1.0001: (bos_w:=bos_w+1) if 1==t else (bos_l:=bos_l+1)
    elif c<fl*0.9999: (bos_w:=bos_w+1) if 0==t else (bos_l:=bos_l+1)
    ph=highs_arr[i]; pl=lows_arr[i]
    if   ph>fh and c<fh: (sw_w:=sw_w+1) if 0==t else (sw_l:=sw_l+1)
    elif pl<fl and c>fl: (sw_w:=sw_w+1) if 1==t else (sw_l:=sw_l+1)
bos_tot=bos_w+bos_l; sw_tot=sw_w+sw_l
print(f"  Break of Structure (BOS):     WR={bos_w/max(1,bos_tot)*100:5.2f}%  (сделок: {bos_tot:,d})")
print(f"  Liquidity Sweep (разворот):   WR={sw_w/max(1,sw_tot)*100:5.2f}%  (сделок: {sw_tot:,d})\n")

# ── УЗЕЛ 3: ORDER FLOW ───────────────────────────────────────
print("="*64); print("УЗЕЛ 3 — ORDER FLOW (Дельта Объёмов)"); print("="*64)
of_w=of_l=0
for i in range(WARMUP,N-HORIZON):
    if np.isnan(target_arr[i]): continue
    ratio=delta_ratio_arr[i]; t=int(target_arr[i])
    if   ratio>0.20: (of_w:=of_w+1) if 1==t else (of_l:=of_l+1)
    elif ratio<-0.20: (of_w:=of_w+1) if 0==t else (of_l:=of_l+1)
of_tot=of_w+of_l; of_wr=of_w/max(1,of_tot)*100
print(f"  Дельта объёмов (окно {OF_WINDOW} свечей):  WR={of_wr:5.2f}%  (сделок: {of_tot:,d})\n")

# ── УЗЕЛ 4: CONTINUOUS STATE (ADX) ───────────────────────────
print("="*64); print("УЗЕЛ 4 — CONTINUOUS STATE (ADX-фаза рынка)"); print("="*64)
cs_w=cs_l=cs_sk=0
for i in range(WARMUP,N-HORIZON):
    if np.isnan(target_arr[i]): continue
    a=df["adx"].iloc[i]; p=df["pdi"].iloc[i]; nn=df["ndi"].iloc[i]
    if pd.isna(a) or pd.isna(p) or pd.isna(nn): continue
    if a<20: cs_sk+=1; continue
    sig=1 if p>nn else 0
    if sig==int(target_arr[i]): cs_w+=1
    else: cs_l+=1
cs_tot=cs_w+cs_l; cs_wr=cs_w/max(1,cs_tot)*100
print(f"  Торговля в тренде (ADX>20): WR={cs_wr:5.2f}%  (сделок: {cs_tot:,d})")
print(f"  Пропущено боковиков:         {cs_sk:,d} свечей\n")

# ── УЗЕЛ 5: ML (LightGBM) ────────────────────────────────────
print("="*64); print("УЗЕЛ 5 — ML LightGBM (реальная модель)"); print("="*64)
ml_ok=False; ml_results={}; idx_to_prob={}; oos_start_idx=int(N*0.80)
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
    for thr in [0.50,0.55,0.60,0.65,0.70,0.75]:
        mask=(probs_oos>thr)|(probs_oos<(1-thr))
        preds=(probs_oos[mask]>0.5).astype(int); act=yte[mask].astype(int)
        ww=(preds==act).sum(); tot=len(preds)
        ml_results[thr]=(ww/max(1,tot)*100,tot)
        print(f"  Порог >{thr*100:.0f}%: WR={ww/max(1,tot)*100:5.2f}%  (сделок: {tot:,d})")
    ml_ok=True
except Exception as e:
    print(f"  ML недоступен: {e}")
print()

# ── СИНЕРГИЯ: ВСЕ 5 УЗЛОВ ────────────────────────────────────
print("="*64); print("СИНЕРГИЯ — ВСЕ 5 УЗЛОВ ВМЕСТЕ (Confluence)"); print("="*64)
syn_w=syn_l=syn_skip=cons_l=0; pause_until=-1; oos_cnt=0
for i in range(max(WARMUP,oos_start_idx), N-HORIZON):
    if np.isnan(target_arr[i]): continue
    oos_cnt+=1
    if i<pause_until: syn_skip+=1; continue
    t=int(target_arr[i]); vote=0.0
    # ТА
    r=df["rsi14"].iloc[i]; hd=df["hma_dir"].iloc[i]
    if not pd.isna(r) and not pd.isna(hd):
        if r<35 and hd==1: vote+=1
        elif r>65 and hd==0: vote-=1
    # SMC
    fh=nearest_fh[i]; fl=nearest_fl[i]
    if fh and fl:
        c=close_arr[i]
        if c>fh*1.0001: vote+=1
        elif c<fl*0.9999: vote-=1
    # OrderFlow
    ratio=delta_ratio_arr[i]
    if ratio>0.25: vote+=1
    elif ratio<-0.25: vote-=1
    # ADX
    a=df["adx"].iloc[i]; p=df["pdi"].iloc[i]; nn=df["ndi"].iloc[i]
    if not (pd.isna(a) or pd.isna(p) or pd.isna(nn)):
        if a>25:
            if p>nn: vote+=1
            else: vote-=1
    # ML (двойной вес)
    if ml_ok and i in idx_to_prob:
        pm=idx_to_prob[i]
        if pm>0.65: vote+=2
        elif pm<0.35: vote-=2
    # Консенсус ≥ 3 голосов
    if abs(vote)>=3:
        sig=1 if vote>0 else 0; win=(sig==t)
        if win: syn_w+=1; cons_l=0
        else:
            syn_l+=1; cons_l+=1
            if cons_l>=3: pause_until=i+15; cons_l=0

syn_tot=syn_w+syn_l; syn_wr=syn_w/max(1,syn_tot)*100
print(f"  OOS свечей проанализировано: {oos_cnt:,d}")
print(f"  Отфильтровано как шум:       {oos_cnt-syn_tot-syn_skip:,d}")
print(f"  Пропущено (Cooloff/риск):    {syn_skip}")
print(f"  Качественных сделок:         {syn_tot}")
print(f"  WIN RATE СИНЕРГИИ:           {syn_wr:.2f}%\n")

# ── ИТОГОВАЯ ТАБЛИЦА ─────────────────────────────────────────
print(); print("="*64); print("         ИТОГОВАЯ СРАВНИТЕЛЬНАЯ ТАБЛИЦА УЗЛОВ"); print("="*64)
print(f"  {'Узел':<39} {'Win Rate':>9}  {'Сделок':>7}")
print(f"  {'-'*58}")
print(f"  {'1.  Математика / TA (RSI+HMA)':<39} {ta_wr:>8.2f}%  {ta_tot:>7,d}")
print(f"  {'2a. Smart Money — Break of Structure':<39} {bos_w/max(1,bos_tot)*100:>8.2f}%  {bos_tot:>7,d}")
print(f"  {'2b. Smart Money — Liquidity Sweep':<39} {sw_w/max(1,sw_tot)*100:>8.2f}%  {sw_tot:>7,d}")
print(f"  {'3.  Order Flow (дельта объёмов)':<39} {of_wr:>8.2f}%  {of_tot:>7,d}")
print(f"  {'4.  ContinuousState (ADX-фаза)':<39} {cs_wr:>8.2f}%  {cs_tot:>7,d}")
if ml_ok:
    wr50,cnt50=ml_results[0.50]; wr65,cnt65=ml_results[0.65]
    print(f"  {'5.  ML LightGBM (порог >50%)':<39} {wr50:>8.2f}%  {cnt50:>7,d}")
    print(f"  {'5.  ML LightGBM (порог >65%)':<39} {wr65:>8.2f}%  {cnt65:>7,d}")
print(f"  {'-'*58}")
print(f"  {'СИНЕРГИЯ: ВСЕ 5 УЗЛОВ (Confluence)':<39} {syn_wr:>8.2f}%  {syn_tot:>7,d}")
print(f"  {'  Точка безубытка PocketOption: 55.56%'}")
print("="*64)
