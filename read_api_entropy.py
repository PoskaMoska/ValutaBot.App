with open('MiniApp/wwwroot/js/api.js', 'r', encoding='utf-8') as f:
    lines = f.readlines()
    
start = -1
for i, line in enumerate(lines):
    if "weatherEntropy" in line:
        start = i
        break

if start != -1:
    for i in range(start, start+40):
        print(lines[i].strip())
