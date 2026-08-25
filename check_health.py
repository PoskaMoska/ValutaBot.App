import requests

ML_URL = "https://mlphythonservice-production.up.railway.app"

try:
    r = requests.get(f"{ML_URL}/models", timeout=10)
    models = r.json()
    print(f"Server is UP. Loaded models: {len(models)}")
except Exception as e:
    print(f"ERROR: {e}")
