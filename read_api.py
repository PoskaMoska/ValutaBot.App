import codecs

with codecs.open('MiniApp/wwwroot/js/api.js', 'r', 'utf-8') as f:
    lines = f.readlines()

start = -1
for i, line in enumerate(lines):
    if "weatherSession" in line:
        start = i
        break

if start != -1:
    for i in range(start-2, start+40):
        print(lines[i].strip())
