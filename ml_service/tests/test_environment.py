import pytest
import sys
import os

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from data.data_loader import TF_MAP

def test_critical_constants_exist():
    """
    Ensure the data loader mappings haven't been broken.
    """
    assert "1m" in TF_MAP.values(), "1m timeframe missing from TF_MAP"
    assert "s5" in TF_MAP.keys(), "s5 timeframe missing from TF_MAP"

def test_env_fallbacks_present():
    """
    Check if run_with_env.py still has the database URL injected.
    If someone deletes the DB URL, local testing will crash.
    """
    run_file = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "run_with_env.py"))
    if os.path.exists(run_file):
        with open(run_file, "r") as f:
            content = f.read()
            assert "DATABASE_URL" in content, "DATABASE_URL fallback missing in run_with_env.py"
