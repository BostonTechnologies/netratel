// Real rendered parent pages and actual new endpoints; no routing or auth mocks.
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const root = process.env.NETRATEL_PAIRING_RUNTIME_ROOT;
if (!root) throw new Error('An explicit disposable NETRATEL_PAIRING_RUNTIME_ROOT is required');
const { chromium, expect } = require(path.join(root, 'browser/node_modules/@playwright/test'));
const fixture = path.join(root, 'actual-pair');
const state = JSON.parse(fs.readFileSync(path.join(fixture, 'private-state.json'), 'utf8'));
const results = [];
const evidence = path.join(fixture, 'browser-evidence');
fs.mkdirSync(evidence, { recursive: true, mode: 0o700 });
const previousReceipt = path.join(evidence, 'receipt.json');
if (fs.existsSync(previousReceipt)) {
  const previous = JSON.parse(fs.readFileSync(previousReceipt, 'utf8'));
  if (previous.fixtureId === state.fixtureId) results.push(...previous.results);
}

function product(name) {
  const entry = state.products[name];
  return { ...entry, name, web: `http://127.0.0.1:${entry.webPort}`, api: `http://127.0.0.1:${entry.apiPort}` };
}
const identifiers = {
  netratel: { panel: 'system-connections', create: 'create-connection', pairForm: 'pairing-create-form',
    final: 'pairing-final-form', nrTenant: 'pairing-netratel-tenant', rdOrg: 'pairing-rateldesk-organization',
    rdCustomer: 'pairing-rateldesk-customer', success: 'pairing-success', row: 'connection-card',
    view: 'connection-view', test: 'connection-test', delete: 'connection-delete', error: 'pairing-error' },
  rateldesk: { panel: 'system-connections-panel', create: 'create-system-connection', pairForm: 'system-pairing-form',
    final: 'system-connection-finalization', nrTenant: 'connection-netratel-tenant', rdOrg: 'connection-rateldesk-organization',
    rdCustomer: 'connection-rateldesk-customer', success: 'system-connection-success', row: 'system-connection-row',
    view: 'view-system-connection', test: 'test-system-connection', delete: 'delete-system-connection', error: 'system-connection-error' },
};
function record(stage, status, details = {}) {
  results.push({ stage, status, ...details });
  fs.writeFileSync(path.join(evidence, 'receipt.json'), JSON.stringify({ fixtureId: state.fixtureId, results }, null, 2), { mode: 0o600 });
  console.log(stage + ': ' + status);
}
async function jsonRequest(context, entry, route, data, method = 'GET') {
  const response = await context.request.fetch(entry.web + route, {
    method, data, headers: { Origin: entry.web }, timeout: 30000,
  });
  if (!response.ok()) {
    let code = 'unknown';
    try { const body = await response.json(); if (typeof body.code === 'string' && /^[a-z0-9-]{1,80}$/.test(body.code)) code = body.code; } catch {}
    throw new Error(`Actual ${entry.name} ${method} ${route} returned HTTP ${response.status()} (${String(code).slice(0, 80)})`);
  }
  return response.status() === 204 ? null : response.json();
}
async function login(page, entry) {
  await page.goto(entry.web + '/login');
  if (entry.name === 'netratel') {
    await page.getByTestId('local-login-email').fill(entry.email);
    await page.getByTestId('local-login-password').fill(state.adminPassword);
    await page.getByTestId('local-login-submit').click();
  } else {
    await page.locator('input[name="email"]').fill(entry.email);
    await page.locator('input[name="password"]').fill(state.adminPassword);
    await page.getByRole('button', { name: /^Sign in to / }).click();
  }
  await expect(page).not.toHaveURL(/\/login(?:[/?]|$)/);
  await account(page, entry);
}
async function account(page, entry) {
  await page.goto(entry.web + '/account/integration-credentials');
  const tab = page.getByRole('tab', { name: 'System connections', exact: true });
  if (await tab.count()) await tab.click();
  await expect(page.getByTestId(identifiers[entry.name].panel)).toBeVisible();
  await expect(page.getByTestId('generate-pairing-code')).toBeEnabled();
}
async function assertNoBrowserError(page) {
  const error = page.locator('#blazor-error-ui');
  if (await error.count()) await expect(error).not.toBeVisible();
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 2),
    { message: 'The actual account form must fit its viewport', timeout: 5000 }).toBe(false);
}
async function visualMatrix(page, stem) {
  for (const theme of ['light', 'dark']) {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.emulateMedia({ colorScheme: theme });
    await page.getByTestId('theme-preference-menu').click();
    await page.getByTestId('theme-option-' + theme).click();
    const attribute = page.url().startsWith(product('netratel').web) ? 'data-netratel-theme' : 'data-helpdesk-theme';
    await expect(page.locator('html')).toHaveAttribute(attribute, theme);
    for (const [viewport, size] of Object.entries({ desktop: { width: 1440, height: 900 }, narrow: { width: 390, height: 844 } })) {
      await page.setViewportSize(size);
      await assertNoBrowserError(page);
      // Generated codes and code fields never enter persistent screenshots.
      await page.screenshot({ path: path.join(evidence, stem + '-' + theme + '-' + viewport + '.png'),
        fullPage: true, mask: [page.getByTestId('generated-pairing-code'), page.getByTestId('pairing-code'), page.getByLabel('Pairing code', { exact: true }), page.locator('input[type="password"]')] });
    }
  }
  await page.setViewportSize({ width: 1440, height: 900 });
}
async function selectChoice(page, testId, name) {
  const field = page.getByTestId(testId);
  await field.click();
  await page.getByRole('option', { name, exact: true }).click();
}
async function visiblePair(page, consumer, generatorPage, generator, mappingName, capabilities) {
  const ids = identifiers[consumer.name];
  await account(generatorPage, generator);
  await generatorPage.getByTestId('generate-pairing-code').click();
  const codePanel = generatorPage.getByTestId('generated-pairing-code');
  await expect(codePanel).toBeVisible();
  const readCode = async () => (generator.name === 'netratel'
    ? await (await codePanel.evaluate(element => element.tagName === 'INPUT') ? codePanel : codePanel.locator('input')).inputValue() : await codePanel.innerText()).match(/\b[A-Z0-9]{4}-[A-Z0-9]{4}\b/);
  const replaced = await readCode();
  await generatorPage.getByTestId('generate-pairing-code').click();
  await expect.poll(async () => (await readCode())?.[0]).not.toBe(replaced?.[0]);
  const code = await readCode();
  expect(code, 'Generator must show a readable short code').not.toBeNull();
  await account(page, consumer);
  await page.getByTestId(ids.create).click();
  const form = page.getByTestId(ids.pairForm);
  await expect(form.locator('input:not([type="hidden"])')).toHaveCount(2);
  await expect(page.getByTestId('pair-and-connect')).toBeDisabled();
  await visualMatrix(page, consumer.name + '-pair-form');
  await form.getByLabel('Address', { exact: true }).fill(generator.web + '/');
  await form.getByLabel('Pairing code', { exact: true }).fill(replaced[0]);
  await expect(page.getByTestId('pair-and-connect')).toBeEnabled();
  await page.getByTestId('pair-and-connect').click();
  await expect(page.getByTestId(ids.error)).toBeVisible();
  await expect(page.getByTestId(ids.final)).toHaveCount(0);
  await visualMatrix(page, consumer.name + '-replaced-code-inline-failure');
  record(consumer.name + '-replaced-code-rejected-inline', 'passed');
  await form.getByLabel('Pairing code', { exact: true }).fill(code[0]);
  await page.getByTestId('pair-and-connect').click();
  await expect(page).toHaveURL(consumer.web + '/account/integration-credentials');
  const final = page.getByTestId(ids.final);
  await expect(final).toBeVisible();
  await expect(final.getByRole('button', { name: 'Save', exact: true })).toHaveCount(1);
  await expect(final.getByRole('checkbox')).toHaveCount(2);
  await expect(final.getByRole('checkbox', { name: /^Run automation/ })).not.toBeChecked();
  await visualMatrix(page, consumer.name + '-paired-finalization');
  const list = await jsonRequest(page.context(), consumer, '/api/v1/admin/system-connections');
  const connection = list.find(row => row.status === 'paired' || !row.mapping) || list[0];
  expect(connection, 'Pair must persist an incomplete system relationship').toBeTruthy();
  const directory = await jsonRequest(page.context(), consumer,
    `/api/v1/admin/system-connections/${connection.pairId}/directory`);
  const nr = directory.netRatelTenants[0];
  const rd = directory.ratelDeskOrganizations[0];
  expect(nr?.id).toBeTruthy(); expect(rd?.id).toBeTruthy();
  await selectChoice(page, ids.nrTenant, nr.name);
  await selectChoice(page, ids.rdOrg, rd.name);
  await final.getByLabel('Connection name', { exact: true }).fill(mappingName);
  await final.getByRole('checkbox', { name: /^Create incidents/ }).setChecked(capabilities.incidents);
  if (capabilities.incidents) {
    const choices = directory.ratelDeskCustomers || directory.customers;
    const customer = choices.find(choice => choice.parentId === rd.id);
    expect(customer, 'An authorized existing customer is required').toBeTruthy();
    await selectChoice(page, ids.rdCustomer, customer.name);
  }
  else await expect(page.getByTestId(ids.rdCustomer)).toHaveCount(0);
  await final.getByRole('checkbox', { name: /^Run automation/ }).setChecked(capabilities.automation);
  await visualMatrix(page, consumer.name + '-final-save-form');
  await final.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByTestId(ids.success)).toContainText('Connected');
  await expect(page.getByTestId(ids.final)).toHaveCount(0);
  const rows = await jsonRequest(page.context(), consumer, '/api/v1/admin/system-connections');
  const saved = rows.find(row => row.mapping?.name === mappingName);
  expect(saved?.status?.toLowerCase()).toBe('connected');
  expect(saved.mapping.createIncidents).toBe(capabilities.incidents);
  expect(saved.mapping.runAutomation).toBe(capabilities.automation);
  await visualMatrix(page, consumer.name + '-connected');
  record(generator.name + '-to-' + consumer.name + '-rendered-pair-and-single-save', 'passed', { pairId: saved.pairId, mappingId: saved.mapping.id });
  return saved;
}
async function seedCustomer(context, rd) {
  // Normal authorized CRUD creates only controlled fixture data.
  const organizations = await jsonRequest(context, rd, '/api/v1/organizations/');
  const organization = Array.isArray(organizations) ? organizations[0] : organizations.items?.[0];
  expect(organization?.id).toBeTruthy();
  return jsonRequest(context, rd, '/api/v1/customers/', {
    name: 'Acceptance customer', organizationId: organization.id, state: 0,
  }, 'POST');
}
async function main() {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const nr = product('netratel'), rd = product('rateldesk');
    const nrPage = await context.newPage(), rdPage = await context.newPage();
    for (const page of [nrPage, rdPage]) page.setDefaultTimeout(25000);
    await login(nrPage, nr); await login(rdPage, rd);
    record('actual-normal-local-administrator-login-both-products', 'passed');
    await seedCustomer(context, rd);
    record('normal-authorized-customer-fixture', 'passed');
    const first = await visiblePair(nrPage, nr, rdPage, rd, 'Acceptance incident and automation', { incidents: true, automation: true });
    // A second initiation must resolve the same immutable system pair and may create
    // a distinct named mapping; the operator chooses capabilities independently.
    const second = await visiblePair(rdPage, rd, nrPage, nr, 'Acceptance automation only', { incidents: false, automation: true });
    expect(second.pairId).toBe(first.pairId);
    expect(second.mapping.id).not.toBe(first.mapping.id);
    state.pairing = { pairId: first.pairId, incidentMapping: first.mapping, automationOnlyMapping: second.mapping };
    fs.writeFileSync(path.join(fixture, 'private-state.json'), JSON.stringify(state, null, 2), { mode: 0o600 });
    record('opposite-side-initiation-one-pair-distinct-mappings', 'passed');
    await account(nrPage, nr);
    const selected = nrPage.getByTestId(identifiers.netratel.row).filter({ hasText: first.mapping.name });
    await selected.getByTestId(identifiers.netratel.view).click();
    await expect(nrPage.getByTestId(identifiers.netratel.final)).toContainText('Connection configuration');
    await nrPage.getByRole('button', { name: /^(Back to connections|Close)$/ }).click();
    const beforeTest = JSON.parse(execFileSync('python3', [path.join(__dirname, 'native_pair.py'), 'counts'], { encoding: 'utf8' }));
    await selected.getByTestId(identifiers.netratel.test).click();
    await expect(selected.getByTestId('connection-test-result')).toBeVisible();
    const tested = (await jsonRequest(context, nr, '/api/v1/admin/system-connections'))
      .find(row => row.mapping?.id === first.mapping.id);
    expect(tested?.lastTest?.success, 'Optional test must confirm current authenticated mapping access').toBe(true);
    const afterTest = JSON.parse(execFileSync('python3', [path.join(__dirname, 'native_pair.py'), 'counts'], { encoding: 'utf8' }));
    expect(afterTest).toEqual(beforeTest);
    record('rendered-view-and-optional-test', 'passed');
    record('side-effect-free-test-current-counts', 'passed');
    await visualMatrix(nrPage, 'netratel-visible-test-result');
    await context.close();
  } finally { await browser.close(); }
}
module.exports = { product, record, jsonRequest, login, account, visualMatrix, identifiers, state, root, fixture, evidence };
if (require.main === module) main().catch(error => { record('actual-rendered-pair-acceptance', 'failed', { exceptionType: error.constructor.name, reason: String(error.message).split('\n')[0].slice(0, 240) }); process.exitCode = 1; });
