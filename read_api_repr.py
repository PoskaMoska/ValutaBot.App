with open('MiniApp/wwwroot/js/api.js', 'rb') as f:
    lines = f.readlines()
    
start = -1
for i, line in enumerate(lines):
    if b"weatherSession" in line:
        start = i
        break

if start != -1:
    for i in range(start, start+40):
        try:
            print(lines[i].decode('utf-8').strip())
        except UnicodeDecodeError:
            print(lines[i])
