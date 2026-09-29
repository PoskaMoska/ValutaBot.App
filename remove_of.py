with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    lines = f.readlines()

new_lines = []
for line in lines:
    if "var ofTask = Task.Run" in line:
        new_lines.append('        var ofResult = new ValutaBot.MiniApp.OrderFlowEngine.OrderFlowResult { ScoreContribution = 0, Description = "REMOVED", DeltaRatio = 1.0, OrderFlowState = "NEUTRAL" };\n')
    elif "await Task.WhenAll(smcTask, ofTask);" in line:
        new_lines.append('        await smcTask;\n')
    elif "var ofResult = await ofTask;" in line:
        pass # delete
    elif "Of = ofResult," in line:
        pass # delete
    else:
        new_lines.append(line)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.writelines(new_lines)
