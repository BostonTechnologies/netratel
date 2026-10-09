// Actual local deletion from each rendered owner page while the peer is offline.
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { product, record, jsonRequest, login, account, visualMatrix, identifiers, state, root, fixture } = require('./browser_pair');
const { chromium, expect } = require(path.join(root, 'browser/node_modules/@playwright/test'));
const { control } = require('./business_pair');
const launcher = path.join(__dirname, 'native_pair.py');
function native(...args) { return execFileSync('python3', [launcher, ...args], { env: process.env, timeout: 120000, encoding: 'utf8' }); }
function counts() { return JSON.parse(native('counts')); }
async function deleteLocal(page, context, entry, expected) {
  await account(page, entry);
  const rows = await jsonRequest(context, entry, '/api/v1/admin/system-connections');
  expect(rows.length, 'Each owner must delete an actual persisted local connection from its rendered page').toBeGreaterThan(0);
  for (const row of rows) {
    const card = page.getByTestId(identifiers[entry.name].row).filter({ hasText: row.mapping?.name || row.peer.product });
    await card.getByTestId(identifiers[entry.name].delete).click();
    await expect(card).toHaveCount(0);
    const route = '/api/v1/admin/system-connections/' + encodeURIComponent(row.pairId)
      + (row.mapping ? '/mappings/' + row.mapping.id : '');
    // A repeated local DELETE uses the real owner cookie and is harmless.
    const repeated = await context.request.delete(entry.web + route, { headers: { Origin: entry.web, 'X-Requested-With': 'XMLHttpRequest', 'X-NetRatel-Account-Request': '1' } });
    expect([200, 204, 404]).toContain(repeated.status());
  }
  expect(await jsonRequest(context, entry, '/api/v1/admin/system-connections')).toEqual([]);
  expect(counts()).toEqual(expected);
  await visualMatrix(page, entry.name + '-deleted-peer-offline');
}
async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    const nr = product('netratel'), rd = product('rateldesk');
    const nrPage = await context.newPage(), rdPage = await context.newPage();
    nrPage.setDefaultTimeout(25000); rdPage.setDefaultTimeout(25000);
    await login(nrPage, nr); await login(rdPage, rd);
    const baseline = counts();
    expect(baseline.incidentCount).toBe(1); expect(baseline.receiptCount).toBe(1);
    expect(baseline.jobRunCount).toBe(1); expect(baseline.taskCount).toBe(1);
    const activeCredential = await control('cached-receiver-status');
    expect(activeCredential.status, 'The exact cached business credential must be accepted immediately before deletion').toBe(200);
    expect(Number.isSafeInteger(activeCredential.expiresAtUnixSeconds)).toBe(true);
    expect(activeCredential.expiresAtUnixSeconds).toBeGreaterThan(Math.floor(Date.now() / 1000));
    expect(activeCredential.credentialSha256).toMatch(/^[0-9a-f]{64}$/);
    native('stop', 'rateldesk');
    await deleteLocal(nrPage, context, nr, baseline);
    native('stop', 'netratel');
    native('restart', 'rateldesk');
    await login(rdPage, rd);
    await deleteLocal(rdPage, context, rd, baseline);
    native('restart', 'netratel');
    await login(nrPage, nr);
    expect(await jsonRequest(context, nr, '/api/v1/admin/system-connections')).toEqual([]);
    expect(await jsonRequest(context, rd, '/api/v1/admin/system-connections')).toEqual([]);
    const cached = await control('cached-receiver-status');
    expect(cached.credentialSha256).toBe(activeCredential.credentialSha256);
    expect(cached.expiresAtUnixSeconds).toBe(activeCredential.expiresAtUnixSeconds);
    expect(cached.expiresAtUnixSeconds, 'The original credential must still be unexpired when durable deletion denies it').toBeGreaterThan(Math.floor(Date.now() / 1000));
    expect([401, 403, 404, 410]).toContain(cached.status);
    expect(counts()).toEqual(baseline);
    record('deleteWhilePeerOffline', 'passed', { bothLocalPages: true, repeatedDeleteHarmless: true, historyPreserved: true, cachedBusinessCredentialDenied: true, unexpiredCredentialRevoked: true });
    await context.close();
  } finally { await browser.close(); }
}
main().catch(error => { record('deleteWhilePeerOffline', 'failed', { exceptionType: error.constructor.name, reason: String(error.message).split('\n')[0].slice(0, 240) }); process.exitCode = 1; });
