import os
import re

TESTS_DIR = r"C:\Users\muham\OneDrive\Desktop\Point of Sale\tests"

projects = {
    "UnitTests": os.path.join(TESTS_DIR, "EdgeRetails.UnitTests"),
    "Desktop.PerformanceTests": os.path.join(TESTS_DIR, "EdgeRetails.Desktop.PerformanceTests"),
    "IntegrationTests": os.path.join(TESTS_DIR, "EdgeRetails.IntegrationTests"),
    "PerformanceTests": os.path.join(TESTS_DIR, "EdgeRetails.PerformanceTests"),
    "CrashTestHost": os.path.join(TESTS_DIR, "EdgeRetails.CrashTestHost")
}

def analyze_project(proj_path):
    fact_theory_pattern = re.compile(r'^\s*\[(Fact|Theory)(?:\(.*\))?\]')
    method_pattern = re.compile(r'public\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z0-9_]+)\s*\(')

    test_methods = []
    file_counts = {}

    for root, dirs, files in os.walk(proj_path):
        if "bin" in dirs: dirs.remove("bin")
        if "obj" in dirs: dirs.remove("obj")
        for f in files:
            if f.endswith(".cs"):
                fpath = os.path.join(root, f)
                rel_fpath = os.path.relpath(fpath, proj_path)
                with open(fpath, "r", encoding="utf-8", errors="ignore") as file:
                    lines = file.readlines()

                is_test = False
                file_test_count = 0
                for line in lines:
                    if fact_theory_pattern.match(line):
                        is_test = True
                    elif is_test:
                        m = method_pattern.search(line)
                        if m:
                            test_name = m.group(1)
                            test_methods.append((rel_fpath, test_name))
                            file_test_count += 1
                            is_test = False
                        elif line.strip().startswith("[") or line.strip().startswith("//") or not line.strip():
                            # Attribute or comment or blank line before method
                            continue
                        else:
                            is_test = False
                if file_test_count > 0:
                    file_counts[rel_fpath] = file_test_count

    return test_methods, file_counts

print("Analyzing distinct test definitions across solution...")
total_distinct = 0
for name, ppath in projects.items():
    if not os.path.exists(ppath): continue
    methods, fcounts = analyze_project(ppath)
    print(f"\nProject {name}: {len(methods)} distinct test methods across {len(fcounts)} files.")
    total_distinct += len(methods)

print(f"\nTOTAL DISTINCT TEST DEFINITIONS ACROSS REPOSITORY: {total_distinct}")
