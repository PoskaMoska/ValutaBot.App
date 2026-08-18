import os
for root, dirs, files in os.walk('.'):
    for file in files:
        if file.endswith('.cs') or file.endswith('.json') or file.endswith('.py'):
            try:
                with open(os.path.join(root, file), 'rb') as f:
                    content = f.read()
                # Check utf-8 encoding of 'Куки'
                if 'Куки'.encode('utf-8') in content:
                    print(f'UTF8: {os.path.join(root, file)}')
                # Check windows-1251 encoding of 'Куки'
                if 'Куки'.encode('cp1251') in content:
                    print(f'CP1251: {os.path.join(root, file)}')
            except:
                pass
