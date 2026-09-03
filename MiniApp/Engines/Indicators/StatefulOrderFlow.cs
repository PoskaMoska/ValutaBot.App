using System;
using System.Collections.Generic;

namespace ValutaBot.MiniApp.Indicators;

public class StatefulOrderFlow
{
    private double _cumulativeVolumeDelta = 0;
    
    // FIX W-06: replaced fake rolling avg (_volSum/_volCount) with proper sliding window Queue
    private readonly Queue<double> _volWindow = new(20);
    private double _avgVolume = 1.0;

    private DateTime _lastProcessedTime;

    private readonly Queue<(double buy, double sell)> _shortTermWindow = new();
    private double _shortTermBuyVolume = 0;
    private double _shortTermSellVolume = 0;
    
    // B10-FIX: Track open candle volume separately so getters can include it without permanent accumulation
    private double _openBuyVolume = 0;
    private double _openSellVolume = 0;
    private bool _openHasInstitutionalBlockTrade = false;

    public double CumulativeVolumeDelta
    {
        get { lock (_lockObj) { return _cumulativeVolumeDelta; } }
    }

    // The DeltaRatio is calculated over the short-term window (12 candles), as it reflects immediate momentum
    public double DeltaRatio
    {
        get
        {
            lock (_lockObj)
            {
                double buy = _shortTermBuyVolume + _openBuyVolume;
                double sell = (_shortTermSellVolume + _openSellVolume) > 1e-8 ? (_shortTermSellVolume + _openSellVolume) : 1.0;
                return buy / sell;
            }
        }
    }

    public double BuyVolume  { get { lock (_lockObj) { return _shortTermBuyVolume + _openBuyVolume;  } } }
    public double SellVolume { get { lock (_lockObj) { return _shortTermSellVolume + _openSellVolume; } } }

    public bool HasInstitutionalBlockTrade
    {
        get { lock (_lockObj) { return _hasInstitutionalBlockTrade || _openHasInstitutionalBlockTrade; } }
        private set { _hasInstitutionalBlockTrade = value; }
    }
    private bool _hasInstitutionalBlockTrade;

    public double PriceDelta  { get { lock (_lockObj) { return _priceDelta;  } } }
    public double CurrentPrice { get { lock (_lockObj) { return _currentPrice; } } }
    private double _priceDelta;
    private double _currentPrice;

    private readonly object _lockObj = new();

        public void Update(ReadOnlySpan<MiniAppController.OhlcCandle> candles)
    {
        if (candles.Length == 0) return;

        lock (_lockObj)
        {
            // Permanent processing of closed candles
            for (int i = 0; i < candles.Length - 1; i++)
            {
                var c = candles[i];
                if (c.Timestamp <= _lastProcessedTime && _lastProcessedTime != default)
                    continue;

                // Session reset logic (e.g. gap > 4 hours)
                if (_lastProcessedTime != default && (c.Timestamp - _lastProcessedTime).TotalHours > 4)
                {
                    _cumulativeVolumeDelta = 0;
                    _shortTermWindow.Clear();
                    _shortTermBuyVolume = 0;
                    _shortTermSellVolume = 0;
                    // FIX C-3: Clear volume window so stale thresholds from previous session
                    // don't cause morning blindness to block trades.
                    _volWindow.Clear();
                }

                // FIX: Reset permanent block trade flag before processing the new closed candle
                _hasInstitutionalBlockTrade = false;

                ProcessCandle(c, isPermanent: true);
                _lastProcessedTime = c.Timestamp;
            }

            // For the current open candle, we calculate the state without permanently committing
            if (candles.Length > 0)
            {
                // B11-FIX: Reset open block trade flag, preserve historical block trade flag
                _openHasInstitutionalBlockTrade = false;
                _openBuyVolume = 0;
                _openSellVolume = 0;

                ProcessCandle(candles[^1], isPermanent: false);

                if (candles.Length >= 5)
                {
                    _priceDelta = candles[^1].Close - candles[^5].Close;
                }
                _currentPrice = candles[^1].Close;
            }
        }
    }

    private void ProcessCandle(MiniAppController.OhlcCandle c, bool isPermanent)
    {
        double totalVol = c.Volume > 0 ? c.Volume : 1.0;
        
        if (isPermanent)
        {
            // FIX W-06: old code subtracted the current mean instead of the oldest value,
            // giving ~5-10% systematic error in avgVolume → wrong blockTradeThreshold.
            // Proper fix: use a Queue as a real sliding window of last 20 candles.
            _volWindow.Enqueue(totalVol);
            if (_volWindow.Count > 20) _volWindow.Dequeue();
            _avgVolume = _volWindow.Average();
        }

        double noiseThreshold = _avgVolume * 0.60;
        double blockTradeThreshold = _avgVolume * 1.70;

        if (totalVol < noiseThreshold)
            return;

        if (totalVol >= blockTradeThreshold)
        {
            if (isPermanent) _hasInstitutionalBlockTrade = true;
            else _openHasInstitutionalBlockTrade = true;
        }

        double range = c.High - c.Low;
        double buyV, sellV;

        if (range > 1e-9)
        {
            double buyRatio = (c.Close - c.Low) / range;
            double sellRatio = (c.High - c.Close) / range;
            buyV = totalVol * buyRatio;
            sellV = totalVol * sellRatio;
        }
        else
        {
            buyV = totalVol * 0.5;
            sellV = totalVol * 0.5;
        }

        if (isPermanent)
        {
            _cumulativeVolumeDelta += (buyV - sellV);

            _shortTermWindow.Enqueue((buyV, sellV));
            _shortTermBuyVolume += buyV;
            _shortTermSellVolume += sellV;

            if (_shortTermWindow.Count > 12)
            {
                var oldest = _shortTermWindow.Dequeue();
                _shortTermBuyVolume -= oldest.buy;
                _shortTermSellVolume -= oldest.sell;
            }
        }
        else
        {
            // For the uncommitted open candle, we just temporarily save its volume
            _openBuyVolume = buyV;
            _openSellVolume = sellV;
        }
    }
}

