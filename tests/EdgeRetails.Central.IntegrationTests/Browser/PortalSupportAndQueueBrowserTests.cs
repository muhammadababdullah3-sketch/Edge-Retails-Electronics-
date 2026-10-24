using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EdgeRetails.Central.IntegrationTests.Browser;

/// <summary>
/// Browser-level SFR-11 harness. It consumes an explicitly provisioned isolated
/// development fixture and uses the already provisioned Python Playwright runtime.
/// See RequiredFixture below for the manifest contract.
/// </summary>
public sealed class PortalSupportAndQueueBrowserTests
{
    private const string RequiredFixture = """
    {
      "baseUrl": "https://127.0.0.1:PORT/",
      "storageStatePath": "absolute path to Playwright storage state containing an isolated dev admin session",
      "shopId": "existing synthetic shop GUID for Customer 360 and support",
      "queueKind": "PAYMENT_REVIEW | HARDWARE_REVIEW | DUAL_APPROVAL | SUPPORT_INBOX",
      "queuePageOneEntityIds": ["GUID", "GUID"],
      "queuePageTwoEntityIds": ["GUID"],
      "customer360ExpectedText": "stable synthetic shop identifier or code shown by Customer 360"
    }
    """;

    [PortalBrowserFixtureFact]
    public async Task AuthenticatedPortal_PagesQueues_ReadsCustomer360_CreatesRepliesAndPersistsAfterReload()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE");
        var evidencePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_EVIDENCE_DIR");
        var pythonPath = Environment.GetEnvironmentVariable("CENTRAL_PLAYWRIGHT_PYTHON");
        if (string.IsNullOrWhiteSpace(fixturePath) || !File.Exists(fixturePath))
        {
            throw new InvalidOperationException($"SFR-11 browser fixture disappeared after test discovery. Required shape:{Environment.NewLine}{RequiredFixture}");
        }
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            throw new InvalidOperationException("SFR-11 browser evidence directory disappeared after test discovery.");
        }
        if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
        {
            throw new InvalidOperationException("SFR-11 requires the explicitly configured Python Playwright runtime.");
        }

        Directory.CreateDirectory(evidencePath);
        var evidenceAbsolute = Path.GetFullPath(evidencePath);
        var scriptPath = Path.Combine(evidenceAbsolute, "portal-support-queue-browser.py");
        await File.WriteAllTextAsync(scriptPath, PythonBrowserHarness, new UTF8Encoding(false));
        var run = await RunPythonAsync(pythonPath, scriptPath, fixturePath, evidenceAbsolute);
        await File.WriteAllTextAsync(Path.Combine(evidenceAbsolute, "browser-run.log"), run.Output, new UTF8Encoding(false));
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException($"SFR-11 browser harness failed (exit {run.ExitCode}). See {Path.Combine(evidenceAbsolute, "browser-run.log")}.");
        }

        var resultPath = Path.Combine(evidenceAbsolute, "browser-result.json");
        Assert.True(File.Exists(resultPath), "SFR-11 browser harness did not write browser-result.json.");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
        Assert.Equal("PASS", result.RootElement.GetProperty("result").GetString());
        Assert.True(result.RootElement.GetProperty("sessionCookieRedactedFromTrace").GetBoolean());
    }

    private static async Task<(int ExitCode, string Output)> RunPythonAsync(string pythonPath, string scriptPath,
        string fixturePath, string evidencePath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.GetFullPath(pythonPath))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(scriptPath));
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(fixturePath));
        process.StartInfo.ArgumentList.Add(Path.GetFullPath(evidencePath));
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdoutTask) + Environment.NewLine + (await stderrTask));
    }

    private const string PythonBrowserHarness = """
import asyncio, json, os, pathlib, sys, traceback, zipfile
from playwright.async_api import async_playwright

fixture_path = pathlib.Path(sys.argv[1])
evidence_dir = pathlib.Path(sys.argv[2])
fixture = json.loads(fixture_path.read_text(encoding='utf-8'))
events = {'console': [], 'pageErrors': [], 'requests': [], 'responses': []}
result_path = evidence_dir / 'browser-result.json'
events_path = evidence_dir / 'browser-events.json'
trace_path = evidence_dir / 'portal-support-queue-trace.zip'
screenshots = evidence_dir / 'screenshots'
screenshots.mkdir(parents=True, exist_ok=True)

def require(condition, message):
    if not condition:
        raise AssertionError(message)

def redact_trace(secrets):
    if not trace_path.exists():
        return not secrets
    temporary = trace_path.with_suffix('.redacted.zip')
    with zipfile.ZipFile(trace_path, 'r') as source, zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as target:
        for item in source.infolist():
            data = source.read(item.filename)
            for secret in secrets:
                if secret:
                    data = data.replace(secret.encode('utf-8'), b'[REDACTED]')
            target.writestr(item, data)
    temporary.replace(trace_path)
    with zipfile.ZipFile(trace_path, 'r') as sanitized:
        return all(secret.encode('utf-8') not in sanitized.read(item.filename)
                   for item in sanitized.infolist() for secret in secrets if secret)

async def main():
    require(fixture.get('baseUrl') and fixture.get('storageStatePath') and pathlib.Path(fixture['storageStatePath']).is_file(),
            'Fixture must provide baseUrl and an existing storageStatePath.')
    require(fixture.get('shopId') and fixture.get('queueKind') and isinstance(fixture.get('queuePageOneEntityIds'), list)
            and isinstance(fixture.get('queuePageTwoEntityIds'), list), 'Fixture is missing shopId, queueKind, or expected page entity IDs.')
    require(fixture['queuePageOneEntityIds'] and fixture['queuePageTwoEntityIds'], 'Queue fixture must contain rows on both pages.')
    state = json.loads(pathlib.Path(fixture['storageStatePath']).read_text(encoding='utf-8'))
    secrets = [cookie.get('value', '') for cookie in state.get('cookies', [])
               if cookie.get('name', '').lower() in ('central_session', 'authorization', 'access_token')]
    browser = None
    context = None
    ticket_id = None
    unique_subject = f"SFR11 browser {__import__('datetime').datetime.now(__import__('datetime').timezone.utc).isoformat()}"
    reply_text = f'Persisted browser reply {unique_subject}'
    try:
        async with async_playwright() as playwright:
            launch_options = {'headless': True}
            executable = os.environ.get('CENTRAL_PLAYWRIGHT_CHROMIUM_EXECUTABLE')
            if executable:
                require(pathlib.Path(executable).is_file(), f'Configured Chromium executable not found: {executable}')
                launch_options['executable_path'] = executable
            browser = await playwright.chromium.launch(**launch_options)
            context = await browser.new_context(storage_state=fixture['storageStatePath'], ignore_https_errors=True)
            await context.tracing.start(screenshots=True, snapshots=True, sources=True)
            page = await context.new_page()
            page.on('console', lambda message: events['console'].append(message.text) if message.type == 'error' else None)
            page.on('pageerror', lambda error: events['pageErrors'].append(str(error)))
            page.on('request', lambda request: events['requests'].append({'method': request.method, 'url': request.url})
                    if '/api/admin/v1/' in request.url else None)
            page.on('response', lambda response: events['responses'].append({'status': response.status, 'url': response.url})
                    if '/api/admin/v1/' in response.url else None)

            await page.goto(fixture['baseUrl'], wait_until='networkidle')
            await page.locator('#app-shell').wait_for(state='visible')
            require(await page.locator('#auth-wrapper').is_hidden(), 'Saved development session was not authenticated by the server.')
            capabilities = await page.locator('#session-capabilities').inner_text()
            for capability in ['queues.view', 'dashboard.view', 'support.view', 'support.create', 'support.reply']:
                require(capability in capabilities, f'Authenticated fixture lacks {capability}.')

            queue_path = f"/api/admin/v1/queues/{fixture['queueKind']}"
            async with page.expect_response(lambda response: queue_path in response.url and response.status == 200) as queue_page_one_response:
                await page.locator(f'[data-queue="{fixture["queueKind"]}"]').click()
            await queue_page_one_response.value
            async def read_queue_ids():
                return await page.locator('#dashboard-queue-tbody tr').evaluate_all(
                    "rows => rows.map(row => row.cells[0]?.textContent?.trim()).filter(Boolean)")
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('page 1.')")
            await page.wait_for_function("document.querySelector('#dashboard-queue-tbody')?.textContent?.trim() && !document.querySelector('#dashboard-queue-tbody')?.textContent?.includes('Loading')")
            page_one = await read_queue_ids()
            require(page_one == fixture['queuePageOneEntityIds'], f'Queue page 1 mismatch: {page_one}')
            await page.screenshot(path=str(screenshots / 'queue-page-1.png'), full_page=True)
            async with page.expect_response(lambda response: queue_path in response.url and 'cursor=20' in response.url and response.status == 200) as queue_page_two_response:
                await page.locator('#dashboard-queue-next').click()
            await queue_page_two_response.value
            await page.wait_for_function("document.querySelector('#dashboard-queue-status')?.textContent?.includes('page 2.')")
            await page.wait_for_function("document.querySelector('#dashboard-queue-tbody')?.textContent?.trim() && !document.querySelector('#dashboard-queue-tbody')?.textContent?.includes('Loading')")
            page_two = await read_queue_ids()
            require(page_two == fixture['queuePageTwoEntityIds'], f'Queue page 2 mismatch: {page_two}')
            await page.screenshot(path=str(screenshots / 'queue-page-2.png'), full_page=True)

            await page.locator('[data-view="customer360"]').click()
            await page.locator('#customer-search-input').fill(fixture['shopId'])
            await page.locator('#btn-customer-search').click()
            expected_text = fixture.get('customer360ExpectedText') or fixture['shopId']
            await page.locator('#customer-360-details').get_by_text(__import__('re').compile(__import__('re').escape(expected_text))).wait_for()
            await page.screenshot(path=str(screenshots / 'customer-360.png'), full_page=True)

            await page.locator('[data-view="support"]').click()
            await page.locator('#support-shop-id').fill(fixture['shopId'])
            await page.locator('#support-subject').fill(unique_subject)
            await page.locator('#support-summary').fill(f'Synthetic browser workflow {unique_subject}')
            await page.locator('#support-create-submit').click()
            await page.get_by_text(__import__('re').compile(r'Ticket [0-9a-f-]{36} was saved by the server\.', __import__('re').I)).wait_for()
            create_text = await page.locator('#support-create-status').inner_text()
            match = __import__('re').search(r'[0-9a-f]{8}-[0-9a-f-]{27}', create_text, __import__('re').I)
            require(match is not None, 'Portal did not return a persisted support ticket ID.')
            ticket_id = match.group(0)
            await page.screenshot(path=str(screenshots / 'support-created.png'), full_page=True)
            ticket_row = page.locator('#support-tbody tr').filter(has_text=unique_subject)
            await ticket_row.get_by_role('button', name='Open Ticket').click()
            await page.locator('#support-reply-form').locator('textarea').fill(reply_text)
            await page.locator('#support-reply-form').get_by_role('button', name='Save Reply').click()
            await page.get_by_text('Reply saved in the portal. No external message was sent.').wait_for()
            await page.screenshot(path=str(screenshots / 'support-reply.png'), full_page=True)
            await page.reload(wait_until='networkidle')
            await page.locator('[data-view="support"]').click()
            await page.locator('#support-tbody').get_by_text(unique_subject).wait_for()
            await page.locator('#support-tbody tr').filter(has_text=unique_subject).get_by_role('button', name='Open Ticket').click()
            await page.locator('#support-history').get_by_text(reply_text).wait_for()
            await page.screenshot(path=str(screenshots / 'support-reload-persistence.png'), full_page=True)

            require(not events['console'] and not events['pageErrors'],
                    f'Browser errors detected: {json.dumps({"console": events["console"], "pageErrors": events["pageErrors"]})}')
            require(any(200 <= item['status'] < 300 and '/api/admin/v1/queues/' in item['url'] for item in events['responses']),
                    'No successful authenticated queue request was observed.')
            require(any(200 <= item['status'] < 300 and '/api/admin/v1/dashboard/customer360/' in item['url'] for item in events['responses']),
                    'No successful Customer 360 request was observed.')
            require(any(200 <= item['status'] < 300 and '/api/admin/v1/support/tickets' in item['url'] for item in events['responses']),
                    'No successful support persistence read/write was observed.')
            await context.tracing.stop(path=str(trace_path))
            trace_redacted = redact_trace(secrets)
            require(trace_redacted, 'A browser session credential remained in the trace after redaction.')
            payload = {'result': 'PASS', 'ticketId': ticket_id, 'uniqueSubject': unique_subject, 'replyText': reply_text,
                       'queuePageOne': page_one, 'queuePageTwo': page_two, 'sessionCookieRedactedFromTrace': trace_redacted,
                       'checks': ['authenticated session', 'queue page 1', 'queue page 2', 'Customer 360', 'support create',
                                  'support reply', 'reload persistence', 'session-secret redaction', 'console/network'],
                       'events': events}
            result_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
            events_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
            print(json.dumps({'result': 'PASS', 'ticketId': ticket_id, 'checks': payload['checks']}))
    except Exception as error:
        if context is not None:
            try:
                await context.tracing.stop(path=str(trace_path))
                redact_trace(secrets)
            except Exception:
                pass
        payload = {'result': 'FAIL', 'message': str(error), 'traceback': traceback.format_exc(), 'events': events}
        result_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
        events_path.write_text(json.dumps(payload, indent=2), encoding='utf-8')
        print(json.dumps(payload), file=sys.stderr)
        raise
    finally:
        if browser is not None:
            await browser.close()

asyncio.run(main())
""";
}

/// <summary>
/// Reports the browser journey as skipped at discovery when its explicitly owned
/// fixture or Playwright Python runtime is absent. No browser acceptance is claimed.
/// </summary>
public sealed class PortalBrowserFixtureFactAttribute : FactAttribute
{
    public PortalBrowserFixtureFactAttribute()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE");
        var evidencePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_EVIDENCE_DIR");
        var pythonPath = Environment.GetEnvironmentVariable("CENTRAL_PLAYWRIGHT_PYTHON");
        if (string.IsNullOrWhiteSpace(fixturePath) || !File.Exists(fixturePath) ||
            string.IsNullOrWhiteSpace(evidencePath) || string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
        {
            Skip = "SFR-11 browser journey requires an owned authenticated development fixture, evidence directory, and configured Python Playwright runtime; no browser acceptance is claimed.";
        }
    }
}
