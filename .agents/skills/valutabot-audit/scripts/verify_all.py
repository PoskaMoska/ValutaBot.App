"""
ValutaBot Unified Verification Script
Runs complete test suite across .NET 10, Python 3.14 ML Service, and Node.js MiniApp Frontend.
"""
import subprocess
import sys
import time

def run_step(name, cmd):
    print(f"\n[RUNNING] {name}...")
    start = time.time()
    res = subprocess.run(cmd, shell=True, capture_output=True, text=True, encoding='utf-8', errors='replace')
    duration = time.time() - start
    if res.returncode == 0:
        print(f"[SUCCESS] {name} passed in {duration:.1f}s")
        return True, res.stdout
    else:
        print(f"[FAILED]  {name} failed in {duration:.1f}s (code {res.returncode})")
        print("--- Output ---")
        print(res.stdout[-1500:] if len(res.stdout) > 1500 else res.stdout)
        print(res.stderr[-1000:] if len(res.stderr) > 1000 else res.stderr)
        return False, res.stdout + "\n" + res.stderr

def main():
    print("=" * 60)
    print("VALUTABOT FULL AUDIT VERIFICATION SUITE")
    print("=" * 60)

    steps = [
        (".NET 10 Build (Release)", "dotnet build ValutaBot.App.csproj -c Release"),
        (".NET 10 Tests (359 tests)", "dotnet test Tests/ValutaBot.Tests/ValutaBot.Tests.csproj -c Release --no-build"),
        ("Python ML Tests (23 tests)", "py -m pytest ml_service/tests"),
        ("Frontend JS Tests (10 tests)", "node MiniApp/wwwroot/js/tests/test_audit_7_fixes.js")
    ]

    all_passed = True
    for name, cmd in steps:
        ok, _ = run_step(name, cmd)
        if not ok:
            all_passed = False
            break

    print("\n" + "=" * 60)
    if all_passed:
        print("ALL VERIFICATION SUITES PASSED! (392/392 TESTS GREEN)")
        print("=" * 60)
        sys.exit(0)
    else:
        print("VERIFICATION FAILED! SEE ERRORS ABOVE.")
        print("=" * 60)
        sys.exit(1)

if __name__ == '__main__':
    main()
