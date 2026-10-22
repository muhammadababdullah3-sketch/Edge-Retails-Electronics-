using System.Diagnostics;
using System.Text;

namespace EdgeRetails.Central.IntegrationTests.Browser;

/// <summary>
/// Browser-level SFR-11 harness. It consumes an explicitly provisioned isolated
/// development fixture instead of fabricating an authenticated session in the browser.
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
        if (string.IsNullOrWhiteSpace(fixturePath) || !File.Exists(fixturePath))
        {
            throw new InvalidOperationException($"SFR-11 browser fixture disappeared after test discovery. Required shape:{Environment.NewLine}{RequiredFixture}");
        }
        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            throw new InvalidOperationException("SFR-11 browser evidence directory disappeared after test discovery.");
        }

        var nodeProbe = await RunNodeAsync("try { require.resolve('playwright'); process.exit(0); } catch (_) { process.exit(2); }");
        if (nodeProbe.ExitCode != 0)
        {
            throw new InvalidOperationException("SFR-11 browser fixture is present but the Playwright runtime is missing.");
        }

        Directory.CreateDirectory(evidencePath);
        var evidenceAbsolute = Path.GetFullPath(evidencePath);
        var scriptPath = Path.Combine(evidenceAbsolute, "portal-support-queue-browser.cjs");
        await File.WriteAllTextAsync(scriptPath, BrowserHarness, new UTF8Encoding(false));
        var run = await RunNodeAsync($"require({System.Text.Json.JsonSerializer.Serialize(scriptPath)});", fixturePath, evidenceAbsolute);
        await File.WriteAllTextAsync(Path.Combine(evidenceAbsolute, "browser-run.log"), run.Output, new UTF8Encoding(false));
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException($"SFR-11 browser harness failed (exit {run.ExitCode}). See {Path.Combine(evidenceAbsolute, "browser-run.log")}.");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunNodeAsync(string script, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdoutTask) + Environment.NewLine + (await stderrTask));
    }

    private const string BrowserHarness = """
    const fs = require('node:fs');
    const path = require('node:path');
    const { chromium } = require('playwright');
    const fixturePath = process.argv[1];
    const evidenceDir = process.argv[2];
    const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
    const events = { console: [], pageErrors: [], requests: [], responses: [] };
    const requireValue = (condition, message) => { if (!condition) throw new Error(message); };
    const escapeRegExp = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const run = async () => {
      requireValue(fixture.baseUrl && fixture.storageStatePath && fs.existsSync(fixture.storageStatePath), 'Fixture must provide baseUrl and an existing storageStatePath.');
      requireValue(fixture.shopId && fixture.queueKind && Array.isArray(fixture.queuePageOneEntityIds) && Array.isArray(fixture.queuePageTwoEntityIds), 'Fixture is missing shopId, queueKind, or expected page entity IDs.');
      requireValue(fixture.queuePageOneEntityIds.length > 0 && fixture.queuePageTwoEntityIds.length > 0, 'Queue fixture must contain at least one expected row on each page.');
      const browser = await chromium.launch({ headless: true });
      const context = await browser.newContext({ storageState: fixture.storageStatePath });
      await context.tracing.start({ screenshots: true, snapshots: true, sources: true });
      const page = await context.newPage();
      page.on('console', message => { if (message.type() === 'error') events.console.push(message.text()); });
      page.on('pageerror', error => events.pageErrors.push(String(error)));
      page.on('request', request => { if (request.url().includes('/api/admin/v1/')) events.requests.push({ method: request.method(), url: request.url() }); });
      page.on('response', response => { if (response.url().includes('/api/admin/v1/')) events.responses.push({ status: response.status(), url: response.url() }); });
      let ticketId = null;
      const uniqueSubject = `SFR11 browser ${new Date().toISOString()} ${Math.random().toString(16).slice(2)}`;
      const replyText = `Persisted browser reply ${uniqueSubject}`;
      try {
        await page.goto(fixture.baseUrl, { waitUntil: 'networkidle' });
        await page.locator('#app-shell').waitFor({ state: 'visible' });
        requireValue(await page.locator('#auth-wrapper').isHidden(), 'Saved development session was not authenticated by the server.');
        const capabilities = await page.locator('#session-capabilities').innerText();
        for (const capability of ['queues.view', 'dashboard.view', 'support.view', 'support.create', 'support.reply'])
          requireValue(capabilities.includes(capability), `Authenticated fixture lacks ${capability}.`);

        await page.locator(`[data-queue="${fixture.queueKind}"]`).click();
        const readQueueIds = async () => (await page.locator('#dashboard-queue-tbody tr').evaluateAll(rows => rows.map(row => row.cells[0]?.textContent?.trim()).filter(Boolean)));
        await page.waitForFunction(() => document.querySelector('#dashboard-queue-status')?.textContent?.includes('page 1.'));
        requireValue(JSON.stringify(await readQueueIds()) === JSON.stringify(fixture.queuePageOneEntityIds), 'Queue page 1 did not match the seeded snapshot.');
        await page.locator('#dashboard-queue-next').click();
        await page.waitForFunction(() => document.querySelector('#dashboard-queue-status')?.textContent?.includes('page 2.'));
        requireValue(JSON.stringify(await readQueueIds()) === JSON.stringify(fixture.queuePageTwoEntityIds), 'Queue page 2 did not match the seeded snapshot.');

        await page.locator('[data-view="customer360"]').click();
        await page.locator('#customer-search-input').fill(fixture.shopId);
        await page.locator('#btn-customer-search').click();
        await page.locator('#customer-360-details').getByText(new RegExp(escapeRegExp(fixture.customer360ExpectedText || fixture.shopId))).waitFor();

        await page.locator('[data-view="support"]').click();
        await page.locator('#support-shop-id').fill(fixture.shopId);
        await page.locator('#support-subject').fill(uniqueSubject);
        await page.locator('#support-summary').fill(`Synthetic browser workflow ${uniqueSubject}`);
        await page.locator('#support-create-submit').click();
        await page.getByText(/Ticket [0-9a-f-]{36} was saved by the server\./i).waitFor();
        const createText = await page.locator('#support-create-status').innerText();
        ticketId = createText.match(/[0-9a-f]{8}-[0-9a-f-]{27}/i)?.[0];
        requireValue(ticketId, 'Portal did not return a persisted support ticket ID.');
        const ticketRow = page.locator('#support-tbody tr').filter({ hasText: uniqueSubject });
        await ticketRow.getByRole('button', { name: 'Open Ticket' }).click();
        await page.locator('#support-reply-form').locator('textarea').fill(replyText);
        await page.locator('#support-reply-form').getByRole('button', { name: 'Save Reply' }).click();
        await page.getByText('Reply saved in the portal. No external message was sent.').waitFor();
        await page.reload({ waitUntil: 'networkidle' });
        await page.locator('[data-view="support"]').click();
        await page.locator('#support-tbody').getByText(uniqueSubject).waitFor();
        await page.locator('#support-tbody tr').filter({ hasText: uniqueSubject }).getByRole('button', { name: 'Open Ticket' }).click();
        await page.locator('#support-history').getByText(replyText).waitFor();
        requireValue(events.console.length === 0 && events.pageErrors.length === 0, `Browser errors detected: ${JSON.stringify({ console: events.console, pageErrors: events.pageErrors })}`);
        requireValue(events.responses.some(response => response.status >= 200 && response.status < 300 && response.url.includes('/api/admin/v1/queues/')), 'No successful authenticated queue request was observed.');
        requireValue(events.responses.some(response => response.status >= 200 && response.status < 300 && response.url.includes('/api/admin/v1/dashboard/customer360/')), 'No successful Customer 360 request was observed.');
        requireValue(events.responses.some(response => response.status >= 200 && response.status < 300 && response.url.includes('/api/admin/v1/support/tickets')), 'No successful support persistence read/write was observed.');
        fs.writeFileSync(path.join(evidenceDir, 'browser-events.json'), JSON.stringify({ fixture: path.basename(fixturePath), ticketId, uniqueSubject, replyText, events }, null, 2));
        console.log(JSON.stringify({ result: 'PASS', ticketId, checks: ['authenticated session', 'queue page 1', 'queue page 2', 'Customer 360', 'support create', 'support reply', 'reload persistence', 'console/network'] }));
      } finally {
        await context.tracing.stop({ path: path.join(evidenceDir, 'portal-support-queue-trace.zip') });
        await browser.close();
      }
    };
    run().catch(error => {
      fs.writeFileSync(path.join(evidenceDir, 'browser-events.json'), JSON.stringify({ result: 'FAIL', message: String(error), events }, null, 2));
      console.error(error && error.stack || error);
      process.exitCode = 1;
    });
    """;
}

/// <summary>
/// Reports the browser journey as skipped at discovery when its explicitly owned
/// fixture is absent. This avoids treating a missing fixture as a passing test or
/// as a runtime failure in the xUnit v2 runner.
/// </summary>
public sealed class PortalBrowserFixtureFactAttribute : FactAttribute
{
    public PortalBrowserFixtureFactAttribute()
    {
        var fixturePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_FIXTURE");
        var evidencePath = Environment.GetEnvironmentVariable("CENTRAL_PORTAL_BROWSER_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(fixturePath) || !File.Exists(fixturePath) || string.IsNullOrWhiteSpace(evidencePath))
        {
            Skip = "SFR-11 browser journey requires an owned authenticated development fixture and evidence directory; no browser acceptance is claimed.";
        }
    }
}
