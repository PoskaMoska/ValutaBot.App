import re

with open('MiniApp/Services/MLPythonService.cs', 'r', encoding='utf-8') as f:
    code = f.read()

# First, extract MarketDataColumnar and MLPythonPrediction
columnar_pattern = r"public class MarketDataColumnar\s*\{.*?\}"
pred_pattern = r"public record MLPythonPrediction\([^)]+\);"

columnar_match = re.search(columnar_pattern, code, flags=re.DOTALL)
pred_match = re.search(pred_pattern, code, flags=re.DOTALL)

if columnar_match and pred_match:
    # Remove them from their current location
    code = code.replace(columnar_match.group(0), "")
    code = code.replace(pred_match.group(0), "")
    
    # Place them before IMLPythonService
    insert_str = f"{columnar_match.group(0)}\n\n{pred_match.group(0)}\n\n"
    code = code.replace("public interface IMLPythonService", insert_str + "public interface IMLPythonService")

# Fix all "DefaultMLPythonService.MarketDataColumnar" to "MarketDataColumnar"
code = code.replace("DefaultMLPythonService.MarketDataColumnar", "MarketDataColumnar")
# Fix all "DefaultMLPythonService.MLPythonPrediction" to "MLPythonPrediction"
code = code.replace("DefaultMLPythonService.MLPythonPrediction", "MLPythonPrediction")

# Write it back
with open('MiniApp/Services/MLPythonService.cs', 'w', encoding='utf-8') as f:
    f.write(code)

print("MLPythonService extracted models successfully.")
