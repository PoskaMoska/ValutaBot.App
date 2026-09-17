import re

with open('MiniApp/Services/SignalTracker.cs', 'r', encoding='utf-8') as f:
    code = f.read()

# Replace class declaration
code = code.replace('public static class SignalTracker\n{', 'public class SignalTrackerService : ISignalTracker\n{\n    public static SignalTrackerService Instance { get; } = new();\n')

# Remove static from methods and properties
code = re.sub(r'public static (async Task|void|double|Task) ', r'public \1 ', code)
code = re.sub(r'private static (readonly )?ConcurrentDictionary', r'private \1ConcurrentDictionary', code)
code = re.sub(r'internal static (readonly )?ConcurrentDictionary', r'public \1ConcurrentDictionary', code)
code = code.replace('private static List<', 'private List<')
code = code.replace('private static DateTime', 'private DateTime')
code = code.replace('private static readonly SemaphoreSlim', 'private readonly SemaphoreSlim')

# Add the static wrapper at the end
wrapper = '''
public static class SignalTracker
{
    public static ConcurrentDictionary<string, double> _livePrices => SignalTrackerService.Instance.LivePrices;
    public static void UpdateLivePrice(string asset, double price) => SignalTrackerService.Instance.UpdateLivePrice(asset, price);
    public static Task RecordPredictionAsync(string direction, string asset, string timeframe, double price, int expiryCandles = 3, int timeframeSecs = 60, bool isForex = false, Dictionary<string, string>? sourceDirections = null, double taScore = 0.0, double ofScore = 0.0, double smcScore = 0.0, double mlProb = 0.0) => SignalTrackerService.Instance.RecordPredictionAsync(direction, asset, timeframe, price, expiryCandles, timeframeSecs, isForex, sourceDirections, taScore, ofScore, smcScore, mlProb);
    public static Task<SignalTrackerService.AccuracyStats> GetOverallStatsAsync() => SignalTrackerService.Instance.GetOverallStatsAsync();
    public static Task<SignalTrackerService.AccuracyStats> GetStatsAsync(string asset, string timeframe) => SignalTrackerService.Instance.GetStatsAsync(asset, timeframe);
    public static Task<SignalTrackerService.AccuracyStats[]> GetAllStatsAsync() => SignalTrackerService.Instance.GetAllStatsAsync();
    public static Task<int> GetPendingCountAsync() => SignalTrackerService.Instance.GetPendingCountAsync();
    public static Task<(string name, double agreeRatePct, double weight, int count)[]> GetSignalStatsAsync() => SignalTrackerService.Instance.GetSignalStatsAsync();
    public static double CalculateSignalWeight(IEnumerable<(string signalName, int verified, int correct)> votes, string signalName, double baseWeight = 1.0) => SignalTrackerService.Instance.CalculateSignalWeight(votes, signalName, baseWeight);
    public static Task<double> GetSignalWeightAsync(string signalName, double baseWeight = 1.0) => SignalTrackerService.Instance.GetSignalWeightAsync(signalName, baseWeight);
}
'''
code = code.replace('}\n', '}\n' + wrapper)

with open('MiniApp/Services/SignalTracker.cs', 'w', encoding='utf-8') as f:
    f.write(code)
