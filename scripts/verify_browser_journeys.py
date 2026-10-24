#!/usr/bin/env python3
"""Fail-closed local Central portal preflight.

This verifies TLS-validated static asset delivery, one anonymous authorization
boundary, and (when configured) headless DOM rendering. It does not authenticate
and never claims the authenticated browser journeys required for acceptance.
"""
from __future__ import annotations

import json
import os
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


def fetch(url: str, timeout: float = 10.0) -> tuple[int, dict[str, str], bytes]:
    request = urllib.request.Request(url, headers={"Cache-Control": "no-store"})
    with urllib.request.urlopen(request, timeout=timeout, context=ssl.create_default_context()) as response:
        return response.status, dict(response.headers.items()), response.read()


def main() -> int:
    base_url = os.environ.get("CENTRAL_BROWSER_BASE_URL", "").rstrip("/")
    evidence_value = os.environ.get("CENTRAL_BROWSER_EVIDENCE_ROOT", "")
    if not base_url or not evidence_value:
        print("BLOCKED: set CENTRAL_BROWSER_BASE_URL and CENTRAL_BROWSER_EVIDENCE_ROOT for an owned local run.", file=sys.stderr)
        return 2

    parsed = urllib.parse.urlparse(base_url)
    if parsed.scheme != "https" or parsed.hostname not in {"127.0.0.1", "localhost", "::1"} or parsed.username or parsed.password:
        print("BLOCKED: browser preflight accepts only credential-free HTTPS loopback URLs.", file=sys.stderr)
        return 2

    evidence_root = Path(evidence_value)
    if not evidence_root.is_absolute() or evidence_root.exists():
        print("BLOCKED: evidence root must be a new absolute path; prior evidence is never overwritten.", file=sys.stderr)
        return 2
    evidence_root.mkdir(parents=True)

    report: dict[str, object] = {
        "timestampUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "baseUrl": base_url,
        "tlsValidation": "system trust validation enabled; no certificate bypass",
        "staticAssets": {},
        "anonymousAuthorization": {},
        "headlessBrowser": {"status": "NOT_CONFIGURED"},
        "authenticatedJourneys": "NOT_RUN",
        "acceptance": "NOT_ESTABLISHED",
    }

    try:
        for path, expected_type in (
            ("/", "text/html"),
            ("/css/portal.css", "text/css"),
            ("/js/portal.js", "javascript"),
        ):
            status, headers, body = fetch(base_url + path)
            content_type = headers.get("Content-Type", "").lower()
            if status != 200 or expected_type not in content_type:
                raise RuntimeError(f"Unexpected asset response for {path}: status={status}, contentType={content_type}")
            (evidence_root / ("index.html" if path == "/" else path.rsplit("/", 1)[-1])).write_bytes(body)
            report["staticAssets"][path] = {"status": status, "contentType": content_type, "bytes": len(body)}

        try:
            status, headers, body = fetch(base_url + "/api/admin/v1/dashboard/metrics")
            report["anonymousAuthorization"] = {"status": status, "contentType": headers.get("Content-Type", ""), "body": body.decode("utf-8", errors="replace")[:1000]}
            if status == 200:
                raise RuntimeError("Anonymous dashboard request unexpectedly succeeded.")
        except urllib.error.HTTPError as error:
            body = error.read()
            report["anonymousAuthorization"] = {"status": error.code, "body": body.decode("utf-8", errors="replace")[:1000]}
            if error.code != 401:
                raise RuntimeError(f"Anonymous dashboard request returned unexpected HTTP {error.code}.") from error

        browser_value = os.environ.get("CENTRAL_BROWSER_EXECUTABLE", "")
        if browser_value:
            browser = Path(browser_value)
            if not browser.is_file():
                raise RuntimeError("Configured CENTRAL_BROWSER_EXECUTABLE does not exist.")
            result = subprocess.run(
                [str(browser), "--headless", "--disable-gpu", "--dump-dom", base_url + "/"],
                capture_output=True, text=True, timeout=20, check=False,
            )
            dom = result.stdout or ""
            (evidence_root / "rendered-dom.html").write_text(dom, encoding="utf-8")
            report["headlessBrowser"] = {
                "status": "PASS" if result.returncode == 0 and "EdgeRetails Central Admin Portal" in dom else "FAIL",
                "returnCode": result.returncode,
                "stderr": (result.stderr or "")[-2000:],
                "domBytes": len(dom.encode("utf-8")),
                "certificateBypass": False,
            }
            if report["headlessBrowser"]["status"] != "PASS":
                raise RuntimeError("Headless browser failed to render the portal with normal certificate validation.")

        report["acceptance"] = "STATIC_AND_ANONYMOUS_BOUNDARY_ONLY"
        (evidence_root / "preflight-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report, indent=2))
        return 0
    except Exception as error:  # preserve exact fail-closed diagnostic in the new evidence folder
        report["error"] = f"{type(error).__name__}: {error}"
        report["acceptance"] = "BLOCKED_OR_FAILED"
        (evidence_root / "preflight-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report, indent=2), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
