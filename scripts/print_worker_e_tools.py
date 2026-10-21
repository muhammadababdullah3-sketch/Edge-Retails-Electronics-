import json
from pathlib import Path

task_file = Path(r"C:\Users\muham\AppData\Local\Temp\claude\c--Users-muham-OneDrive-Desktop-Point-of-Sale\e143b7ec-1d5c-4ad8-ad06-4bbe1dd20736\tasks\ad3be42f1b11fe9b6.output")
with open(task_file, "r", encoding="utf-8") as f:
    lines = [l for l in f if l.strip()]

for idx, line in enumerate(lines):
    obj = json.loads(line)
    msg = obj.get("message", {})
    if msg.get("role") == "assistant":
        for item in msg.get("content", []):
            if item.get("type") == "tool_use":
                print(f"[{idx}] Tool: {item.get('name')} -> {item.get('input')}")
