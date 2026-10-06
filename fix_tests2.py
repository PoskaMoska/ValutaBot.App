import os
import re

test_dir = r"C:\Users\bural\source\repos\ValutaBot.App\Tests\ValutaBot.Tests"

for root, dirs, files in os.walk(test_dir):
    for f in files:
        if f.endswith(".cs"):
            path = os.path.join(root, f)
            with open(path, 'r', encoding='utf-8') as file:
                content = file.read()
                
            orig = content
            
            # Regex replacements
            content = re.sub(r',\s*of,\s*ml,\s*st,', ', ml, st,', content)
            content = re.sub(r',\s*ofSignal,\s*mlSignal,', ', mlSignal,', content)
            content = re.sub(r',\s*ofs,\s*mls,', ', mls,', content)
            content = re.sub(r',\s*new\s*OrderflowSignal\([^)]*\),\s*ml,\s*st,', ', ml, st,', content)
            
            # Clean up OrderFlowEngine instances
            content = re.sub(r'(?m)^.*OrderFlowEngine.*$', '', content)
            content = re.sub(r'(?m)^.*StatefulOrderFlow.*$', '', content)
            
            # Some manual leftovers
            content = content.replace("ta, smc, of, ml, st", "ta, smc, ml, st")
            content = content.replace("taStrong, smc, of, mlBuy, st", "taStrong, smc, mlBuy, st")
            content = content.replace("taStrong, smc, of, mlPut, st", "taStrong, smc, mlPut, st")
            
            if content != orig:
                with open(path, 'w', encoding='utf-8') as file:
                    file.write(content)
                print(f"Fixed {path}")

