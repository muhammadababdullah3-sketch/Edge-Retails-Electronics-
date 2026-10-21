import json
from pathlib import Path

for fn in ["ad3be42f1b11fe9b6.output", "a161bec6340303786.output"]:
    task_file = Path(r"C:\Users\muham\AppData\Local\Temp\claude\c--Users-muham-OneDrive-Desktop-Point-of-Sale\e143b7ec-1d5c-4ad8-ad06-4bbe1dd20736\tasks") / fn
    if task_file.exists():
        with open(task_file, "r", encoding="utf-8") as f:
            lines = [l for l in f if l.strip()]
        print(f"File {fn}: {len(lines)} lines")
        for idx in range(len(lines) - 1, -1, -1):
            obj = json.loads(lines[idx])
            msg = obj.get("message", {})
            if msg.get("role") == "assistant":
                has_text = False
                for item in msg.get("content", []):
                    if item.get("type") == "text":
                        has_text = True
                        print(f"  Assistant at line {idx} text:\n{item.get('text')[:400]}")
                if has_text:
                    break
