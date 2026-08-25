with open("MiniApp/Services/MLPythonService.cs", "r", encoding="utf-8") as f:
    text = f.read()
import sys
if "[MLPython] Online RL feedback registered" in text:
    print("FOUND!")
else:
    print("NOT FOUND!")
