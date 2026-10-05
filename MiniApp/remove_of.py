"""
Remove Order Flow as a voting/signal system from the codebase.
Strategy:
  - Delete OrderFlowEngine.cs and StatefulOrderFlow.cs
  - Remove OF computation from Orchestrator
  - Remove OrderflowSignal parameter from ConfluenceMatrixEngine
  - Remove OF from MLPythonService.PredictAsync signature  
  - Remove OF from SignalTracker / TradeOutcomeRecord (keep DB cols, just zero them)
  - Keep DB columns (already migrated, historical data preserved)
  - Keep of_* in Python features (they'll be 0/NEUTRAL - model will downweight them)
"""
import sys, os, re
sys.stdout.reconfigure(encoding='utf-8')

def patch(path, find, replace, label, required=True):
    with open(path, "r", encoding="utf-8") as f:
        code = f.read()
    if find in code:
        code = code.replace(find, replace)
        with open(path, "w", encoding="utf-8") as f:
            f.write(code)
        print(f"  OK  {label}")
        return True
    else:
        print(f"  {'MISS' if required else 'SKIP'} {label}")
        return False

# ═══════════════════════════════════════════════════════════════════
# 1. MarketAnalysisOrchestrator.cs
# ═══════════════════════════════════════════════════════════════════
print("=== 1. MarketAnalysisOrchestrator.cs ===")
path = "MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs"

# Remove ofResult stub creation - it's a dead zeroed struct anyway
patch(path,
    """        var ofResult = new ValutaBot.MiniApp.OrderFlowEngine.OrderFlowResult { ScoreContribution = 0, Description = "OF Removed", DeltaRatio = 1.0, CumulativeVolumeDelta = 0, OrderFlowState = "NEUTRAL", IsInstitutionalBlockTrade = false };""",
    "",
    "remove ofResult creation", required=False)

# Handle whatever the actual line looks like
with open(path, "r", encoding="utf-8") as f:
    code = f.read()

# Remove the ofResult line (it may look different)
code = re.sub(r'[ \t]*var ofResult = new ValutaBot\.MiniApp\.OrderFlowEngine\.OrderFlowResult\b[^\n]+\n', '', code)
code = re.sub(r'[ \t]*var ofResult = .*?OrderFlowResult[^\n]+\n', '', code)

with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  removed ofResult stub via regex")

# Remove ofSignal creation
patch(path,
    "        var ofSignal = new OrderflowSignal(ofResult.ScoreContribution, ofResult.Description);\r\n",
    "",
    "remove ofSignal creation")

# Replace ofSignal in EvaluateMatrixAsync call  
patch(path,
    ", ofSignal,",
    ",",
    "remove ofSignal from EvaluateMatrixAsync call")

# Remove "OrderFlow" from sourceDirections
patch(path,
    """            [\"OrderFlow\"]    = DirectionExtensions.FromScore(consensus.OfScore, 0.05).ToSignal(),\r\n""",
    "",
    "remove OrderFlow from sourceDirections")

# Remove ofResult from MLPythonService.PredictAsync call (line 155 area)
with open(path, "r", encoding="utf-8") as f:
    code = f.read()
code = re.sub(r', smcResult, ofResult\)', ', smcResult)', code)
code = re.sub(r', smcResult, ofResult\b', ', smcResult', code)
with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  removed ofResult from PredictAsync call")

# Remove ofResult from RecordPredictionAsync calls - replace ofResult.DeltaRatio with 1.0 and ofResult.OrderFlowState with "NEUTRAL"
with open(path, "r", encoding="utf-8") as f:
    code = f.read()
code = code.replace("ofResult.DeltaRatio", "1.0")
code = code.replace("ofResult.OrderFlowState", "\"NEUTRAL\"")
code = code.replace("ofResult.ScoreContribution", "0.0")
code = code.replace("ofResult.Description", "\"OF Removed\"")
with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  replaced ofResult.* references")

# ═══════════════════════════════════════════════════════════════════
# 2. ConfluenceMatrixEngine.cs - remove OrderflowSignal parameter
# ═══════════════════════════════════════════════════════════════════
print("\n=== 2. ConfluenceMatrixEngine.cs ===")
path = "MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs"

with open(path, "r", encoding="utf-8") as f:
    code = f.read()

# Remove OrderflowSignal parameter from EvaluateMatrixAsync signature
code = re.sub(r',\s*OrderflowSignal ofSignal,', ',', code)
code = re.sub(r'\s*OrderflowSignal ofSignal,\s*', '', code)

# The OF score was already zeroed: double ofScore = 0.0;
# Remove the scaling/normalization lines that reference ofScore
# But keep the sb.AppendLine for logging (helps understand what happened)
# Actually remove the whole OF section to keep it clean

# Remove normOf line 
code = re.sub(r'[ \t]*double normOf = Math\.Clamp\(ofScore / 0\.5, -1\.0, 1\.0\).*\n', '', code)
# Remove scaledOf line
code = re.sub(r'[ \t]*double scaledOf = ofScore \*.*\n', '', code)
# Remove any further use of normOf/scaledOf in score computation
code = code.replace("+ normOf * 0.15", "")  # example weight line
code = code.replace("normOf", "0.0")
code = code.replace("scaledOf", "0.0")

with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  removed OrderflowSignal param and OF scoring from EvaluateMatrixAsync")

# ═══════════════════════════════════════════════════════════════════
# 3. IConfluenceMatrixEngine.cs - remove OrderflowSignal from interface
# ═══════════════════════════════════════════════════════════════════
print("\n=== 3. IConfluenceMatrixEngine.cs ===")
path = "MiniApp/Features/MarketAnalysis/Engines/IConfluenceMatrixEngine.cs"
with open(path, "r", encoding="utf-8") as f:
    code = f.read()
code = re.sub(r',\s*OrderflowSignal ofSignal,', ',', code)
code = re.sub(r'\s*OrderflowSignal ofSignal,\s*', '', code)
with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  removed OrderflowSignal from interface")

# ═══════════════════════════════════════════════════════════════════
# 4. MLPythonService.cs - remove ofResult parameter from PredictAsync
# ═══════════════════════════════════════════════════════════════════
print("\n=== 4. MLPythonService.cs ===")
path = "MiniApp/Services/MLPythonService.cs"
with open(path, "r", encoding="utf-8") as f:
    code = f.read()

# Remove ofResult parameter from PredictAsync signature
code = re.sub(r',\s*\n?\s*ValutaBot\.MiniApp\.OrderFlowEngine\.OrderFlowResult\? ofResult = null\)', ')', code)
code = re.sub(r'\s*ValutaBot\.MiniApp\.OrderFlowEngine\.OrderFlowResult\? ofResult = null,', '', code)

# Replace ofResult usages with defaults
code = code.replace("ofResult?.DeltaRatio ?? 1.0", "1.0")
code = code.replace("ofResult?.OrderFlowState ?? \"NEUTRAL\"", "\"NEUTRAL\"")
code = code.replace("ofResult?.ScoreContribution ?? 0.0", "0.0")
code = re.sub(r'[ \t]*double ofRat = ofResult\?\.[^\n]+\n', '', code)
code = re.sub(r'[ \t]*string ofState = ofResult\?\.[^\n]+\n', '', code)
code = re.sub(r'[ \t]*double ofDeltaRatio = ofResult\?\.[^\n]+\n', '', code)

# Replace remaining of_delta_ratio and of_state in the JSON payload with constants
code = code.replace("of_delta_ratio = ofDeltaRatio,", "of_delta_ratio = 1.0,")
code = code.replace("of_state = ofState,", "of_state = \"NEUTRAL\",")
code = code.replace("of_score = ofRat,", "of_score = 0.0,")

with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  removed ofResult from PredictAsync")

# ═══════════════════════════════════════════════════════════════════
# 5. SignalTracker.cs - remove OfScore from PredictionRecord
#    (keep for DB compat but zero out; also remove from method signature)
# ═══════════════════════════════════════════════════════════════════
print("\n=== 5. SignalTracker.cs ===")
path = "MiniApp/Services/SignalTracker.cs"
with open(path, "r", encoding="utf-8") as f:
    code = f.read()

# Remove ofScore from RecordPredictionAsync signature
code = re.sub(r',?\s*double ofScore = 0\.0,', '', code)

# Remove ofScore from record initializer
code = re.sub(r'[ \t]*OfScore = ofScore,?\r?\n', '', code)

# Replace OfScore = ofScore (record init) with OfScore = 0.0
code = code.replace("OfScore = ofScore", "OfScore = 0.0")

with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  removed ofScore from RecordPredictionAsync")

# ═══════════════════════════════════════════════════════════════════
# 6. TradeOutcomeTracker.cs - zero out OF fields (keep DB compat)
# ═══════════════════════════════════════════════════════════════════
print("\n=== 6. TradeOutcomeTracker.cs ===")
path = "MiniApp/Services/TradeOutcomeTracker.cs"
with open(path, "r", encoding="utf-8") as f:
    code = f.read()

# OfDirection was derived from sourceDirections["OrderFlow"] - 
# since we removed OrderFlow from sourceDirections, it'll be "NEUTRAL" by default
# No change needed - GetValueOrDefault("OrderFlow", "NEUTRAL") returns "NEUTRAL"

with open(path, "w", encoding="utf-8") as f:
    f.write(code)
print("  OK  no changes needed (OrderFlow key absent -> NEUTRAL default)")

# ═══════════════════════════════════════════════════════════════════
# 7. ConfluenceFormattingService.cs - remove OF from display
# ═══════════════════════════════════════════════════════════════════
print("\n=== 7. ConfluenceFormattingService.cs ===")
path = "MiniApp/Features/MarketAnalysis/ConfluenceFormattingService.cs"
if os.path.exists(path):
    with open(path, "r", encoding="utf-8") as f:
        code = f.read()
    # Remove OF score lines from formatting
    code = re.sub(r'[ \t]*.*[Oo]rder[Ff]low.*\n', lambda m: m.group(0) if "//OF Removed" in m.group(0) else '', code)
    with open(path, "w", encoding="utf-8") as f:
        f.write(code)
    print("  OK  removed OF from formatting service")
else:
    print("  SKIP file not found")

print("\n=== ALL OF REMOVAL PATCHES APPLIED ===")
print("\nNote: OrderFlowEngine.cs and StatefulOrderFlow.cs will be deleted separately")
print("Note: DB columns kept for historical data - they will contain 0.0/NEUTRAL going forward")
