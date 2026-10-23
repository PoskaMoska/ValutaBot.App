import psycopg2
import time
import datetime
from collections import defaultdict

DB_URL = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

def run_deep_verification(target_per_tf=50):
    print("=" * 75)
    print("      LIVE RAILWAY OTC TRADING VERIFICATION & BENCHMARK")
    print(f"      TARGET: At least {target_per_tf} verified trades per timeframe (5s, 10s, 15s, 30s, 1m)")
    print("=" * 75)

    conn = psycopg2.connect(DB_URL)
    conn.autocommit = True
    cur = conn.cursor()

    timeframes = ['s5', 's10', 's15', 's30', 'm1']
    all_trades = []
    trades_by_tf = {}

    for tf in timeframes:
        # Fetch the most recent closed trades for this timeframe
        cur.execute("""
            SELECT id, asset, timeframe, direction, entry_price, exit_price, was_win, pnl_bps, 
                   probability, ml_prob, ta_score, smc_score, atr_at_signal, verified_at, created_at
            FROM trade_outcomes
            WHERE timeframe = %s
              AND exit_price IS NOT NULL
              AND verified_at >= NOW() - INTERVAL '60 minutes'
            ORDER BY verified_at DESC
            LIMIT %s;
        """, (tf, target_per_tf))
        
        rows = cur.fetchall()
        tf_trades = []
        for r in rows:
            trade = {
                'id': r[0],
                'asset': r[1],
                'timeframe': r[2],
                'direction': r[3],
                'entry': r[4],
                'exit': r[5],
                'win': r[6],
                'pnl_bps': r[7],
                'prob': r[8],
                'ml_prob': r[9],
                'ta_score': r[10],
                'smc_score': r[11],
                'atr': r[12],
                'verified_at': r[13],
                'created_at': r[14]
            }
            tf_trades.append(trade)
            all_trades.append(trade)

        trades_by_tf[tf] = tf_trades
        print(f"[*] Fetched {len(tf_trades)} recent live trades for [{tf.upper()}] (Latest: {tf_trades[0]['verified_at'].strftime('%H:%M:%S UTC') if tf_trades else 'None'})")

    conn.close()
    return trades_by_tf, all_trades

def print_tf_report(tf, trades):
    total = len(trades)
    wins = sum(1 for t in trades if t['win'] is True)
    losses = sum(1 for t in trades if t['win'] is False)
    ties = sum(1 for t in trades if t['win'] is None)
    decided = wins + losses
    winrate = (wins / decided * 100.0) if decided > 0 else 0.0

    print(f"\n" + "-" * 70)
    print(f" TIMEFRAME [{tf.upper()}]: {total} сделок | WINRATE: {winrate:.1f}% ({wins}W / {losses}L / {ties} Доджи)")
    print("-" * 70)

    # Show 5 sample trades
    print("  Примеры последних сделок:")
    for t in trades[:5]:
        res_str = "[WIN] " if t['win'] else ("[LOSS]" if t['win'] is False else "[TIE] ")
        pnl = f"{t['pnl_bps']:+.2f} bps" if t['pnl_bps'] is not None else "0.00"
        print(f"    {t['asset']:8s} | {t['direction']:11s} | {res_str} | Entry: {t['entry']} -> Exit: {t['exit']} ({pnl}) | Prob: {t['prob']}%")

    # High Conviction Slice (Prob >= 58% or ML prob >= 0.55)
    high_conf = [t for t in trades if t['prob'] >= 58 or (t['ml_prob'] and t['ml_prob'] >= 0.55)]
    hc_wins = sum(1 for t in high_conf if t['win'] is True)
    hc_losses = sum(1 for t in high_conf if t['win'] is False)
    hc_dec = hc_wins + hc_losses
    hc_wr = (hc_wins / hc_dec * 100.0) if hc_dec > 0 else 0.0
    print(f"  -> При повышенной уверенности (Prob >= 58% / ML >= 55%): {hc_wr:.1f}% ({hc_wins}W / {hc_losses}L на {hc_dec} сделках)")

    # Volatility / Momentum Slice (No dead chop: |pnl_bps| >= 0.5)
    active_moves = [t for t in trades if t['pnl_bps'] is not None and abs(t['pnl_bps']) >= 0.5]
    am_wins = sum(1 for t in active_moves if t['win'] is True)
    am_losses = sum(1 for t in active_moves if t['win'] is False)
    am_dec = am_wins + am_losses
    am_wr = (am_wins / am_dec * 100.0) if am_dec > 0 else 0.0
    print(f"  -> При наличии импульса (|PnL| >= 0.5 bps, без брокерского шума): {am_wr:.1f}% ({am_wins}W / {am_losses}L на {am_dec} сделках)")

    return {
        'total': total, 'wins': wins, 'losses': losses, 'ties': ties,
        'decided': decided, 'winrate': winrate,
        'hc_wr': hc_wr, 'hc_n': hc_dec,
        'am_wr': am_wr, 'am_n': am_dec
    }

def print_global_report(trades_by_tf, all_trades):
    print("\n" + "=" * 75)
    print("      СВОДНЫЙ ОТЧЕТ ПО ВСЕМ ТАЙМФРЕЙМАМ (LIVE RAILWAY BENCHMARK)")
    print("=" * 75)

    total_all = len(all_trades)
    wins_all = sum(1 for t in all_trades if t['win'] is True)
    losses_all = sum(1 for t in all_trades if t['win'] is False)
    ties_all = sum(1 for t in all_trades if t['win'] is None)
    dec_all = wins_all + losses_all
    wr_all = (wins_all / dec_all * 100.0) if dec_all > 0 else 0.0

    print(f"\n ВСЕГО СДЕЛОК В ВЫБОРКЕ: {total_all}")
    print(f"   * ПОБЕД (WINS):   {wins_all} [WIN]")
    print(f"   * ПОРАЖЕНИЙ:      {losses_all} [LOSS]")
    print(f"   * НИЧЬИХ (ДОДЖИ): {ties_all} [TIE]")
    print(f"   >>> ОБЩИЙ СЫРОЙ ВИНРЕЙТ СЕРВЕРА: {wr_all:.1f}%\n")

    print("ТАБЛИЦА ВИНРЕЙТА ПО ТАЙМФРЕЙМАМ:")
    print(f"{'Таймфрейм':10s} | {'Всего':6s} | {'Wins':5s} | {'Losses':6s} | {'Винрейт (сырой)':16s} | {'Винрейт (с импульсом)':22s}")
    print("-" * 75)
    
    tf_summaries = {}
    for tf in ['s5', 's10', 's15', 's30', 'm1']:
        res = print_tf_report(tf, trades_by_tf[tf])
        tf_summaries[tf] = res

    print("\n" + "=" * 75)
    print("СРАВНИТЕЛЬНАЯ ТАБЛИЦА СЫРОЙ ВИНРЕЙТ VS ВИНРЕЙТ ПРИ ИМПУЛЬСЕ:")
    print(f"{'Таймфрейм':10s} | {'Сделок':7s} | {'Сырой Винрейт':14s} | {'Винрейт (Импульс)':18s} | {'Винрейт (Conf>=58%)':20s}")
    print("-" * 75)
    for tf in ['s5', 's10', 's15', 's30', 'm1']:
        s = tf_summaries[tf]
        print(f"{tf.upper():10s} | {s['decided']:7d} | {s['winrate']:13.1f}% | {s['am_wr']:17.1f}% | {s['hc_wr']:19.1f}%")

    # Breakdown by Asset
    by_asset = defaultdict(lambda: {'wins': 0, 'losses': 0})
    for t in all_trades:
        a = t['asset']
        if t['win'] is True: by_asset[a]['wins'] += 1
        elif t['win'] is False: by_asset[a]['losses'] += 1

    print("\nВИНРЕЙТ ПО АКТИВАМ:")
    for a, s in sorted(by_asset.items(), key=lambda x: (x[1]['wins']/(x[1]['wins']+x[1]['losses']) if (x[1]['wins']+x[1]['losses'])>0 else 0), reverse=True):
        dec = s['wins'] + s['losses']
        wr = (s['wins'] / dec * 100.0) if dec > 0 else 0.0
        print(f"   * {a:10s}: {wr:5.1f}% ({s['wins']}W / {s['losses']}L | Всего: {dec})")

    # Breakdown by Direction
    by_dir = defaultdict(lambda: {'wins': 0, 'losses': 0})
    for t in all_trades:
        d = "BUY" if "BUY" in t['direction'] else ("PUT" if "PUT" in t['direction'] else "OTHER")
        if t['win'] is True: by_dir[d]['wins'] += 1
        elif t['win'] is False: by_dir[d]['losses'] += 1

    print("\nВИНРЕЙТ ПО НАПРАВЛЕНИЯМ:")
    for d, s in sorted(by_dir.items()):
        dec = s['wins'] + s['losses']
        wr = (s['wins'] / dec * 100.0) if dec > 0 else 0.0
        print(f"   * {d:5s}: {wr:5.1f}% ({s['wins']}W / {s['losses']}L)")

    print("=" * 75)

if __name__ == '__main__':
    trades_by_tf, all_trades = run_deep_verification(target_per_tf=50)
    print_global_report(trades_by_tf, all_trades)
