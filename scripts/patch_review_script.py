with open('scripts/generate_review_lanes_and_report.py', 'r', encoding='utf-8') as f:
    text = f.read()

target = 'def write_dual(rel_path: str, content: str):'
replacement = '''def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with open(p, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest().lower()

def write_dual(rel_path: str, content: str):'''

if 'def sha256_file' not in text:
    text = text.replace(target, replacement)
    with open('scripts/generate_review_lanes_and_report.py', 'w', encoding='utf-8') as f:
        f.write(text)
    print('Successfully patched')
else:
    print('Already patched')
