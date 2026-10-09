// Exact NR-generated -> RD-consumed Final Save; one desktop, no business execution.
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const { product, record, jsonRequest, login, selectChoice, seedCustomer, identifiers, state, root, fixture, evidence } = require('./browser_pair');
const { chromium, expect } = require(path.join(root, 'browser/node_modules/@playwright/test'));
const launcher = path.join(__dirname, 'final_save.py');
const native = path.join(__dirname, 'native_pair.py');
function invoke(script, ...args) { return execFileSync('python3', [script, ...args], { encoding: 'utf8', timeout: 120000, env: process.env }); }
function counts() { return JSON.parse(invoke(native, 'counts')); }
async function masked(page, name) {
  await page.screenshot({ path: path.join(evidence, name + '.png'), fullPage: true, animations: 'disabled',
    mask: [page.locator('input'), page.getByTestId('generated-pairing-code'), page.getByTestId('pairing-code')] });
}
async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const nr = product('netratel'), rd = product('rateldesk'), ids = identifiers.rateldesk;
    const nrPage = await context.newPage(), rdPage = await context.newPage();
    for (const page of [nrPage, rdPage]) page.setDefaultTimeout(25000);
    await login(nrPage, nr); await login(rdPage, rd);
    const baselineCounts = counts();
    expect(Object.values(baselineCounts).every(value => value === 0)).toBe(true);
    await seedCustomer(context, rd);
    await expect(nrPage.getByTestId('generate-pairing-code')).toBeEnabled();
    await nrPage.getByTestId('generate-pairing-code').click();
    const code = nrPage.getByTestId('generated-pairing-code');
    await expect(code).toBeVisible();
    const input = await code.evaluate(element => element.tagName === 'INPUT') ? code : code.locator('input');
    const pairingCode = (await input.inputValue()).match(/\b[A-Z0-9]{4}-[A-Z0-9]{4}\b/);
    expect(pairingCode).not.toBeNull();
    await expect(rdPage.getByTestId(ids.create)).toBeEnabled();
    await rdPage.getByTestId(ids.create).click();
    const pairForm = rdPage.getByTestId(ids.pairForm);
    await expect(pairForm.locator('input:not([type="hidden"])')).toHaveCount(2);
    await pairForm.getByLabel('Address', { exact: true }).fill(nr.web);
    await pairForm.getByLabel('Pairing code', { exact: true }).fill(pairingCode[0]);
    await rdPage.getByTestId('pair-and-connect').click();
    const final = rdPage.getByTestId(ids.final);
    await expect(final).toBeVisible();
    const paired = await jsonRequest(context, rd, '/api/v1/admin/system-connections');
    expect(paired).toHaveLength(1);
    const pairId = paired[0].pairId;
    const directory = await jsonRequest(context, rd, `/api/v1/admin/system-connections/${pairId}/directory`);
    const tenant = directory.netRatelTenants[0], organization = directory.ratelDeskOrganizations[0];
    const customer = (directory.ratelDeskCustomers || directory.customers).find(x => x.parentId === organization.id);
    expect(tenant?.id).toBeTruthy(); expect(organization?.id).toBeTruthy(); expect(customer?.id).toBeTruthy();
    await selectChoice(rdPage, ids.nrTenant, tenant.name);
    await selectChoice(rdPage, ids.rdOrg, organization.name);
    const incidents = final.getByRole('checkbox', { name: /^Create incidents/ });
    const automation = final.getByRole('checkbox', { name: /^Run automation/ });
    await incidents.setChecked(true);
    await selectChoice(rdPage, ids.rdCustomer, customer.name);
    await automation.setChecked(true);
    const save = final.getByRole('button', { name: 'Save', exact: true });
    await expect(save).toBeEnabled();
    const name = 'Focused HTTPS incident and automation';
    const nameInput = final.getByLabel('Connection name', { exact: true });
    await nameInput.fill(name); await nameInput.press('Tab');
    function assertMapping(mapping) {
      expect(mapping.name).toBe(name);
      expect(String(mapping.netRatelTenantId)).toBe(String(tenant.id));
      expect(mapping.ratelDeskOrganizationId).toBe(organization.id);
      expect(mapping.ratelDeskCustomerId).toBe(customer.id);
      expect(mapping.createIncidents).toBe(true); expect(mapping.runAutomation).toBe(true);
    }
    async function assertDraft() {
      await expect(rdPage.getByTestId(ids.nrTenant).and(rdPage.getByRole('combobox'))).toHaveText(tenant.name);
      await expect(rdPage.getByTestId(ids.rdOrg).and(rdPage.getByRole('combobox'))).toHaveText(organization.name);
      await expect(rdPage.getByTestId(ids.rdCustomer).and(rdPage.getByRole('combobox'))).toHaveText(customer.name);
      await expect(nameInput).toHaveValue(name);
      await expect(incidents).toBeChecked(); await expect(automation).toBeChecked();
    }
    await assertDraft();
    let saveAttempts = 0, baselineApiHash = null;
    const connected = rdPage.getByTestId(ids.success).filter({ hasText: 'Connected' });
    const error = rdPage.getByTestId(ids.error);
    async function saveOnce() {
      await expect(save).toBeEnabled();
      ++saveAttempts;
      await save.click();
      await connected.or(error).first().waitFor({ state: 'visible' });
    }
    if (state.saveJourney === 'retained') {
      baselineApiHash = state.products.netratel.runtimeAssemblySha256.api;
      await saveOnce();
      const observation = JSON.parse(invoke(launcher, 'baseline-observation'));
      if (await connected.isVisible()) {
        record('https-baseline-guard-reproduction', 'failed', { reproduced: false, terminalKind: 'connected', ...observation });
        throw new Error('baseline-default-port-guard-not-reproduced');
      }
      record('https-baseline-guard-reproduction', observation.guardCodeObserved && observation.token200Observed && !observation.capabilitiesReached ? 'passed' : 'failed', observation);
      await expect(error).toContainText('receiver-readiness-unverified');
      await assertDraft();
      const failed = await jsonRequest(context, rd, '/api/v1/admin/system-connections');
      expect(failed).toHaveLength(1);
      expect(failed[0].pairId).toBe(pairId);
      expect(failed[0].status.toLowerCase()).not.toBe('connected');
      assertMapping(failed[0].mapping);
      await masked(rdPage, 'https-baseline-failed-draft');
      expect(observation.guardCodeObserved, 'Baseline must reproduce the exact real connection guard failure, not an injected error').toBe(true);
      expect(observation.token200Observed).toBe(true); expect(observation.capabilitiesReached).toBe(false);
      const mappingId = failed[0].mapping.id;
      invoke(launcher, 'switch-api');
      await assertDraft();
      await saveOnce();
      await expect(connected).toBeVisible();
      const retried = await jsonRequest(context, rd, '/api/v1/admin/system-connections');
      expect(retried).toHaveLength(1);
      expect(retried[0].pairId).toBe(pairId); expect(retried[0].mapping.id).toBe(mappingId);
      record('same-failed-draft-corrected-api-retry', 'passed', { pairId, mappingId, saveAttempts });
    } else {
      await saveOnce();
      await expect(connected).toBeVisible();
      record('fresh-corrected-first-save', 'passed', { saveAttempts });
    }
    await expect(final).toHaveCount(0);
    const rows = await jsonRequest(context, rd, '/api/v1/admin/system-connections');
    expect(rows).toHaveLength(1);
    const saved = rows[0];
    expect(saved.pairId).toBe(pairId); expect(saved.status.toLowerCase()).toBe('connected');
    assertMapping(saved.mapping);
    const connectors = await jsonRequest(context, nr, `/api/v2/tenants/${tenant.id}/connectors/rateldesk`);
    const connector = connectors.find(x => x.id === saved.mapping.id);
    expect(connector?.automaticDeliveryAvailable).toBe(true);
    expect(counts()).toEqual(baselineCounts);
    const observations = JSON.parse(fs.readFileSync(path.join(fixture, 'save-frontdoor-observations.json'), 'utf8'));
    expect(observations.some(x => x.stage === 'capabilities' && x.status === 200)).toBe(true);
    expect(observations.some(x => x.stage === 'targetValidation' && x.status === 200)).toBe(true);
    await masked(rdPage, 'https-connected-both-capabilities');
    const current = JSON.parse(fs.readFileSync(path.join(fixture, 'private-state.json'), 'utf8'));
    if (baselineApiHash) expect(current.products.netratel.runtimeAssemblySha256.api).not.toBe(baselineApiHash);
    fs.writeFileSync(path.join(root, 'pending-save-receipt.json'), JSON.stringify({
      fixtureId: state.fixtureId, journey: state.saveJourney, saveAttempts, pairId, mappingId: saved.mapping.id,
      sources: current.sources, baselineSource: current.baselineSource,
      baselineApiHash, runtimeAssemblySha256: Object.fromEntries(Object.entries(current.products).map(([key, value]) => [key, value.runtimeAssemblySha256])),
      canonicalApiScheme: 'https', canonicalApiPort: 443, allApiBasesAgree: true,
      connected: true, exactSubmittedValues: true, receiverValidationPassed: true, businessCounts: baselineCounts, cleanupComplete: false,
    }, null, 2), { mode: 0o600 });
    await context.close();
  } finally { await browser.close(); }
}
main().catch(error => {
  record('focused-https-final-save', 'failed', {
    exceptionType: ['Error', 'ExpectError', 'AssertionError', 'TimeoutError'].includes(error.constructor.name) ? error.constructor.name : 'Error',
    reason: 'focused-save-assertion-failed',
  });
  process.exitCode = 1;
});
