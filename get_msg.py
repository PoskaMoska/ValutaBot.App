import json

with open(r'C:\Users\bural\.gemini\antigravity\brain\7cd1f9f2-49ac-4764-8070-cac280f5e32c\.system_generated\logs\transcript.jsonl', 'r', encoding='utf-8') as f, open('output_msg.txt', 'w', encoding='utf-8') as out:
    for line in f:
        data = json.loads(line)
        if data.get('step_index') in [302, 303, 304, 305]:
            if data.get('source') == 'MODEL' and data.get('type') == 'PLANNER_RESPONSE':
                out.write(f"--- STEP {data.get('step_index')} ---\n")
                if data.get('content'):
                    out.write(data.get('content') + '\n')
