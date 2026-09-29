with open('MiniApp/wwwroot/index.html', 'r', encoding='utf-8') as f:
    lines = f.readlines()

new_lines = []
skip = False
for i, line in enumerate(lines):
    if "display:flex; justify-content:space-between; align-items:center; background:rgba(255,255,255,0.03); padding:6px 8px; border-radius:6px;" in line:
        # Check if the next line is empty or has another identical div (which means this one is orphaned)
        if i+2 < len(lines) and "display:flex; justify-content:space-between; align-items:center; background:rgba(255,255,255,0.03); padding:6px 8px; border-radius:6px;" in lines[i+2]:
            continue # Skip this line!
    
    new_lines.append(line)

with open('MiniApp/wwwroot/index.html', 'w', encoding='utf-8') as f:
    f.writelines(new_lines)
