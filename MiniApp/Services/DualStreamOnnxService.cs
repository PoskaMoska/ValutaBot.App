using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ValutaBot.MiniApp;

/// <summary>
/// High-performance in-process ONNX inference service for the Dual-Stream Multi-Timeframe Brain.
/// Eliminates Python HTTP network latency, executing inference in ~1.0-1.5ms directly in .NET 10.
/// </summary>
public sealed class DualStreamOnnxService : IDisposable
{
    private static readonly Lazy<DualStreamOnnxService> _lazyInstance = new(() => new DualStreamOnnxService());
    public static DualStreamOnnxService Instance => _lazyInstance.Value;

    private readonly InferenceSession? _session;
    private readonly bool _isLoaded;
    private readonly object _inferenceLock = new();

    public bool IsModelLoaded => _isLoaded;
    public string ModelPath { get; } = string.Empty;

    public record OnnxPrediction(
        string Direction,        // "BUY" | "PUT" | "NEUTRAL"
        double Probability,      // [0.50 .. 0.99]
        double RawProbability,   // [0.01 .. 0.99]
        long ElapsedMicroseconds
    );

    public DualStreamOnnxService()
    {
        try
        {
            var candidatePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ml_service", "models", "dual_stream_brain_v5.onnx"),
                Path.Combine(Directory.GetCurrentDirectory(), "ml_service", "models", "dual_stream_brain_v5.onnx"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "ml_service", "models", "dual_stream_brain_v5.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dual_stream_brain_v5.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ml_service", "models", "dual_stream_brain_v5.onnx")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    ModelPath = Path.GetFullPath(path);
                    break;
                }
            }

            if (!string.IsNullOrEmpty(ModelPath) && File.Exists(ModelPath))
            {
                var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
                {
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = 2
                };

                _session = new InferenceSession(ModelPath, sessionOptions);
                _isLoaded = true;

                // Pre-warm ONNX session execution provider and memory buffers so first live inference is ~1ms
                try
                {
                    var dummyMacro = new DenseTensor<float>(new float[150], new[] { 1, 30, 5 });
                    var dummyMicro = new DenseTensor<float>(new float[150], new[] { 1, 30, 5 });
                    var dummyBridge = new DenseTensor<float>(new float[4], new[] { 1, 4 });
                    using var warmup = _session.Run(new[]
                    {
                        NamedOnnxValue.CreateFromTensor("macro_x", dummyMacro),
                        NamedOnnxValue.CreateFromTensor("micro_x", dummyMicro),
                        NamedOnnxValue.CreateFromTensor("bridge_x", dummyBridge)
                    });
                }
                catch { }

                BotLogger.Info($"[DualStreamOnnx] Successfully loaded and pre-warmed in-process ONNX model from: {ModelPath}");
            }
            else
            {
                BotLogger.Info("[DualStreamOnnx] Model file 'dual_stream_brain_v5.onnx' not found — in-process ML disabled (using C# rule engine).");
                _isLoaded = false;
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[DualStreamOnnx] Failed to initialize in-process ONNX session: {ex.Message}");
            _isLoaded = false;
        }
    }

    /// <summary>
    /// Evaluates in-process inference for the DualStream model:
    /// macro: (1, 30, 5) float tensor
    /// micro: (1, 30, 5) float tensor
    /// bridge: (1, 4) float tensor
    /// </summary>
    private static bool HasNonFinite(float[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (!float.IsFinite(data[i])) return true;
        }
        return false;
    }

    public OnnxPrediction Predict(float[] macroData, float[] microData, float[] bridgeData)
    {
        if (!_isLoaded || _session == null || macroData == null || microData == null || bridgeData == null ||
            macroData.Length != 150 || microData.Length != 150 || bridgeData.Length != 4 ||
            HasNonFinite(macroData) || HasNonFinite(microData) || HasNonFinite(bridgeData))
        {
            return new OnnxPrediction("NEUTRAL", 0.50, 0.50, 0);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var macroTensor = new DenseTensor<float>(macroData, new[] { 1, 30, 5 });
            var microTensor = new DenseTensor<float>(microData, new[] { 1, 30, 5 });
            var bridgeTensor = new DenseTensor<float>(bridgeData, new[] { 1, 4 });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("macro_x", macroTensor),
                NamedOnnxValue.CreateFromTensor("micro_x", microTensor),
                NamedOnnxValue.CreateFromTensor("bridge_x", bridgeTensor)
            };

            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
            lock (_inferenceLock)
            {
                results = _session.Run(inputs);
            }

            using (results)
            {
                var outputTensor = results.First().AsTensor<float>();
                float logit = outputTensor.GetValue(0);
                if (!float.IsFinite(logit))
                {
                    BotLogger.Warn("[DualStreamOnnx] Model returned non-finite logit (NaN/Inf). Falling back to NEUTRAL.");
                    return new OnnxPrediction("NEUTRAL", 0.50, 0.50, 0);
                }

                // Numerically stable sigmoid:
                double rawProb = logit >= 0
                    ? 1.0 / (1.0 + Math.Exp(-(double)logit))
                    : Math.Exp((double)logit) / (1.0 + Math.Exp((double)logit));
                if (!double.IsFinite(rawProb))
                {
                    return new OnnxPrediction("NEUTRAL", 0.50, 0.50, 0);
                }
                rawProb = Math.Clamp(rawProb, 0.01, 0.99);

                string dir = rawProb >= 0.50 ? "BUY" : "PUT";
                double directionalProb = dir == "BUY" ? rawProb : (1.0 - rawProb);
                directionalProb = Math.Clamp(directionalProb, 0.50, 0.92);

                sw.Stop();
                long elapsedMicro = (long)(sw.Elapsed.TotalMilliseconds * 1000.0);

                return new OnnxPrediction(dir, directionalProb, rawProb, elapsedMicro);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[DualStreamOnnx] Prediction failed: {ex.Message}");
            return new OnnxPrediction("NEUTRAL", 0.50, 0.50, 0);
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
