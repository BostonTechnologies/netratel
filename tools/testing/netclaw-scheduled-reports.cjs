// Opt-in browser acceptance against a disposable, paired NetRatel installation.
const assert = require('node:assert/strict');
const { randomBytes } = require('node:crypto');
const { mkdir, readFile, writeFile } = require('node:fs/promises');
const path = require('node:path');

function required(name) {
    const value = process.env[name];
    if (!value) throw new Error(`Set ${name} before running the native acceptance check.`);
    return value;
}

async function snapshot(context, base, tenant, conversation) {
    const response = await context.request.get(new URL(`/api/v1/tenants/${tenant}/ai/investigations/${conversation}`, base).href);
    assert.equal(response.status(), 200, 'Read the authenticated saved conversation');
    return response.json();
}

function evidence(saved) {
    return {
        conversation: saved.investigation.id,
        state: saved.investigation.state,
        message: saved.activeMessageId,
        lastSequence: saved.lastSequence,
        followUp: saved.followUp,
        events: saved.events.filter(event => ['operator', 'assistant', 'turn_completed', 'scheduled_report_registered',
            'scheduled_report_unconfirmed'].includes(event.type)).map(event => ({
            sequence: event.sequence,
            type: event.type,
            createdAtUtc: event.createdAtUtc,
            session: event.trace?.sessionId,
            message: event.messageId ?? event.trace?.turnMessageId,
            sourceTimestampUtc: event.trace?.sourceTimestampUtc,
            nativeTurn: event.trace?.scheduledReport?.turnNumber ?? JSON.parse(event.trace?.detailsJson ?? '{}').turnNumber,
            occurrence: event.trace?.scheduledReport?.occurrenceId,
            followUp: event.trace?.followUp,
            marker: event.type === 'assistant' ? /^NR-SCHEDULED-(?:REGISTERED|FINAL)-(?:BUFFERED|POSTPARENT)-[A-F0-9]{12}$/.exec(event.text)?.[0] : undefined
        }))
    };
}

async function checkScenario(context, base, tenant, directory, mode) {
    const page = await context.newPage();
    const id = randomBytes(6).toString('hex').toUpperCase();
    const reminder = `nr-${mode.toLowerCase()}-${id.toLowerCase()}`;
    const parent = `NR-SCHEDULED-REGISTERED-${mode}-${id}`;
    const final = `NR-SCHEDULED-FINAL-${mode}-${id}`;
    const surface = page.getByTestId('netclaw-conversation');
    const composer = page.getByTestId('chat-message');
    const replies = page.locator('.chat-message-assistant');
    const finalReply = replies.getByText(final, { exact: true });
    const receipt = { mode, reminder, parent, final };
    const file = path.join(directory, mode.toLowerCase());
    try {
        await page.goto(new URL(`/ai/chat?tenantId=${tenant}`, base).href);
        await composer.fill(`NRPROBE-${mode}-${id} Register exactly one current_session one-shot reminder ` +
            `${reminder} in 30s. The reminder's only action is to reply with ${final}. ` +
            `Do not run any endpoint action. ${mode === 'BUFFERED' ? 'Keep the parent busy until after the reminder dispatch.' : 'End the parent before the reminder dispatch.'} ` +
            `Finish the parent with ${parent}.`);
        receipt.sendAtUtc = new Date().toISOString();
        await page.getByTestId('chat-send').click();
        await replies.getByText(parent, { exact: true }).waitFor({ timeout: 65_000 });
        const conversation = new URL(page.url()).searchParams.get('chat');
        assert.match(conversation, /^[0-9a-f-]{36}$/i);
        await page.screenshot({ path: `${file}-parent.png` });

        // Capture the persisted state even when the delivery assertion times out.
        try { await finalReply.waitFor({ timeout: 45_000 }); }
        catch (error) { if (error.name !== 'TimeoutError') throw error; }
        const saved = await snapshot(context, base, tenant, conversation);
        receipt.beforeReload = evidence(saved);
        receipt.observedAtUtc = new Date().toISOString();
        await page.reload();
        await replies.getByText(parent, { exact: true }).waitFor();
        const reloaded = await snapshot(context, base, tenant, conversation);
        receipt.afterReload = evidence(reloaded);
        receipt.ui = {
            status: await surface.locator('.chat-assistant-identity p').textContent(),
            pending: await surface.getAttribute('data-pending-scheduled-reports'),
            finalCount: await finalReply.count(),
            composerEnabled: await composer.isVisible() && await composer.isEnabled(),
            waitingBanner: await surface.locator('.chat-processing').isVisible()
        };
        await page.screenshot({ path: `${file}-reload.png`, fullPage: true });

        assert.equal(saved.events.filter(event => event.type === 'operator').length, 1);
        assert.equal(reloaded.events.filter(event => event.type === 'operator').length, 1);
        const parentTerminal = saved.events.find(event => event.type === 'turn_completed');
        assert.ok(parentTerminal?.trace?.followUp?.reminders.some(item => item.id === reminder), 'Parent ends with its reminder pending');
        assert.equal(receipt.ui.finalCount, 1, 'The exact scheduled final must appear once in the original chat after reload');
        assert.equal(receipt.ui.pending, '0');
        assert.equal(receipt.ui.composerEnabled, true);
        assert.equal(receipt.ui.waitingBanner, false);
        for (const result of [saved, reloaded]) {
            assert.equal(result.investigation.state, 0, 'Idle');
            const answer = result.events.filter(event => event.type === 'assistant' && event.text === final);
            assert.equal(answer.length, 1);
            const origin = answer[0].trace;
            assert.equal(origin.turnMessageId, result.activeMessageId);
            assert.ok(origin.scheduledReport?.occurrenceId, 'The saved answer carries exact native occurrence identity');
            const terminals = result.events.filter(event => event.type === 'turn_completed' &&
                event.trace?.scheduledReport?.occurrenceId === origin.scheduledReport.occurrenceId);
            assert.equal(terminals.length, 1);
            assert.equal(terminals[0].trace.turnMessageId, result.activeMessageId);
            assert.equal(terminals[0].sequence, answer[0].sequence + 1);
        }
        return { mode, passed: true };
    } catch (error) {
        receipt.failure = error.message;
        return { mode, passed: false };
    } finally {
        await writeFile(`${file}.json`, JSON.stringify(receipt, null, 2));
        await page.close();
    }
}

async function main() {
    assert.equal(process.env.NETRATEL_RUN_NATIVE_SCHEDULED_REPORTS, '1', 'Enable this disposable native check explicitly');
    const base = new URL(required('NETRATEL_SCHEDULED_REPORT_WEB_URL'));
    const directory = path.resolve(required('NETRATEL_SCHEDULED_REPORT_EVIDENCE'));
    const email = required('NETRATEL_SCHEDULED_REPORT_EMAIL');
    const password = (await readFile(required('NETRATEL_SCHEDULED_REPORT_PASSWORD_FILE'), 'utf8')).trimEnd();
    await mkdir(directory, { recursive: true });
    const { chromium } = require('playwright');
    const browser = await chromium.launch({ executablePath: process.env.NETRATEL_SCHEDULED_REPORT_CHROMIUM });
    try {
        const context = await browser.newContext();
        context.setDefaultTimeout(15_000);
        const login = await context.newPage();
        await login.goto(new URL('/ai/chat', base).href);
        await login.getByTestId('local-login-email').fill(email);
        await login.getByTestId('local-login-password').fill(password);
        const loginResult = login.waitForURL(url => !url.pathname.includes('login'));
        await login.getByTestId('local-login-submit').click();
        await loginResult;
        await login.goto(new URL('/ai/chat', base).href);
        const tenant = await login.getByTestId('chat-tenant').inputValue();
        assert.match(tenant, /^[1-9][0-9]*$/);
        await login.close();
        const results = await Promise.all(['BUFFERED', 'POSTPARENT'].map(mode => checkScenario(context, base, tenant, directory, mode)));
        console.log(JSON.stringify(results));
        assert.ok(results.every(result => result.passed), 'Native scheduled-report delivery acceptance failed. Read both saved receipts.');
    } finally { await browser.close(); }
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
