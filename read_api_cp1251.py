import codecs

try:
    with codecs.open('MiniApp/wwwroot/js/api.js', 'r', 'cp1251') as f:
        lines = f.readlines()
        
    start = -1
    for i, line in enumerate(lines):
        if "weatherSession" in line:
            start = i
            break

    if start != -1:
        for i in range(start, start+40):
            print(lines[i].strip())
except Exception as e:
    print(e)
