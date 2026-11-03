---
name: valutabot-audit
description: >-
  Performs complete forensic audit, numerical verification, and stress-testing of the ValutaBot
  high-frequency trading platform across 6 subsystems (Math, WebSockets, PostgreSQL, ML, SRE, Frontend).
  Use whenever asked to audit ValutaBot, verify code integrity, check for bugs, run test suites, or execute multi-agent audits.
---

# ValutaBot Forensic Audit & Quality Governance Skill

This skill governs the complete audit, verification, and automated testing runbook for the ValutaBot high-frequency binary options trading platform.

## Zero-Hallucination & Zero-Hack Protocol

When executing an audit round:
1. **Mandatory File Inspection**: ALWAYS read target files using `read_file` before analyzing or proposing changes. Never write diffs based on assumptions or memory.
2. **Fact Confirmation**: If a feature or fix is already implemented in the file, confirm the line range and declare: `✅ [File:lines]: Already implemented and verified.`
3. **No Workarounds / Zero-Hack Standard**:
   - ❌ Strictly prohibited: empty `try-catch`, masking symptoms, global locking, `Thread.Sleep`, blocking async calls.
   - ✅ Required: SOTA .NET 10, Zero-Allocation hot paths, atomic PostgreSQL operations, mathematical proofs.
4. **Compile & Test Verification**: Any proposed patch must be validated using `verify_all.py`.

## Subsystem Audit Runbook

Reference prompts and target files:
- [Audit 1: Mathematical Core & Signal Consensus](./references/prompts.md#audit-1-mathematical-core-numerics-indicators--signal-consensus)
- [Audit 2: Broker WebSockets & Buffer Pipeline](./references/prompts.md#audit-2-high-throughput-broker-websockets--ringbuffer-pipeline)
- [Audit 3: PostgreSQL Storage & Integrity](./references/prompts.md#audit-3-postgresql-high-throughput-persistence--data-governance)
- [Audit 4: ML DualStream & Drift Detection](./references/prompts.md#audit-4-ml-service-dualstream-cross-attention--drift-detection)
- [Audit 5: Worker Resilience & Infrastructure](./references/prompts.md#audit-5-worker-resilience-distributed-lock--infrastructure-observability)
- [Audit 6: MiniApp Frontend UX & Touch Latency](./references/prompts.md#audit-6-telegram-miniapp-frontend-ux-touch-latency--canvas)

## Verification Script

Run the unified test suite:
```powershell
py .agents/skills/valutabot-audit/scripts/verify_all.py
```
Checks:
1. .NET 10 Release Build (0 errors, 0 warnings)
2. .NET 10 Test Suite (359 tests)
3. Python ML Test Suite (23 tests)
4. Frontend JS Test Suite (10 tests)
