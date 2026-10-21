import os
import sys
import shutil
import hashlib
import json
import re
from datetime import datetime, timezone

def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

print("SHA256 test:", sha256_file("scripts/test_write.py"))
