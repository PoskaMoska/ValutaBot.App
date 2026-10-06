import os
import re
import glob

test_dir = r"C:\Users\bural\source\repos\ValutaBot.App\Tests\ValutaBot.Tests"

def fix_evaluate_calls(content):
    # Match EvaluateMatrixAsync arguments
    # Look for: ta, smc, of, ml, st, mtf -> ta, smc, ml, st, mtf
    # We will just remove the specific orderflow arguments being passed in the tests.
    
    # Common test patterns:
    content = content.replace("ta, smc, of, ml, st, _neutralMtf", "ta, smc, ml, st, _neutralMtf")
    content = content.replace("ta, smc, of, ml, st, mtf", "ta, smc, ml, st, mtf")
    content = content.replace("taStrong, smc, of, mlBuy, st, _neutralMtf", "taStrong, smc, mlBuy, st, _neutralMtf")
    content = content.replace("taStrong, smc, of, mlPut, st, _neutralMtf", "taStrong, smc, mlPut, st, _neutralMtf")
    content = content.replace("ta, smc, of, ml, st, mtfGolden", "ta, smc, ml, st, mtfGolden")
    content = content.replace("taSignal, smcSignal, ofSignal, mlSignal,", "taSignal, smcSignal, mlSignal,")
    content = content.replace("ta, cms, ofs, mls, sts, mtf", "ta, cms, mls, sts, mtf")
    
    # Inline new OrderflowSignal removal
    content = re.sub(r'new OrderflowSignal\([^)]+\),\s*', '', content)
    
    return content

for root, dirs, files in os.walk(test_dir):
    for f in files:
        if f.endswith(".cs"):
            path = os.path.join(root, f)
            with open(path, 'r', encoding='utf-8') as file:
                content = file.read()
                
            orig = content
            content = fix_evaluate_calls(content)
            
            # Also remove OrderFlowEngine setups in tests
            content = re.sub(r'(?m)^.*OrderFlowEngine.*$', '', content)
            content = re.sub(r'(?m)^.*StatefulOrderFlow.*$', '', content)
            
            # Additional cleanup for missing comma or extra space if needed, though replace should be clean
            
            if content != orig:
                with open(path, 'w', encoding='utf-8') as file:
                    file.write(content)
                print(f"Fixed {path}")

