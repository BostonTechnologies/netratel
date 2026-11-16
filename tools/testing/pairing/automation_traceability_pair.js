// Opt-in extension of the existing owned source-pair fixture. Only normal owner
// APIs perform writes; SQL independently reads committed authority and history.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

module.exports = async function automationTraceabilityPair(h) {
  const { nrApi, rdApi, connection, sample, apiJson, sql, literal, until, requireProof, control, record,
    runtimeRoot, state, evidence } = h;
  const tenant = Number(connection.mapping.netRatelTenantId);
  const mappingId = crypto.randomUUID();
  const mapping = { ...connection.mapping, id: mappingId, name: 'Synthetic automation traceability',
    createIncidents: true, runAutomation: true, useIncidentTemplates: true };
  await apiJson(rdApi, `/api/v1/admin/system-connections/${connection.pairId}/mappings/${mappingId}`, mapping, 'PUT');
  const flowBase = `/api/v1/tenants/${tenant}/flows`;
  const connector = await until('Both directions share one approved mapping', async () =>
    (await apiJson(nrApi, flowBase + '/connectors')).find(x => x.id === mappingId && x.canExecute));
  const defaults = await apiJson(rdApi, '/api/v1/internal-incident-templates/defaults/system-job-failure/draft');
  const saved = await apiJson(rdApi, '/api/v1/internal-incident-templates/', {
    ...defaults, name: 'Synthetic internal directory failure', organizationId: mapping.ratelDeskOrganizationId
  }, 'POST');
  const presentation = await apiJson(rdApi, `/api/v1/internal-incident-templates/${saved.id}/publish`,
    { expectedVersion: saved.version, retired: false }, 'POST');
  const cataloguePath = `/api/v2/tenants/${tenant}/connectors/rateldesk/${mappingId}/incident-templates`;
  const beforeBrowse = h.counts();
  const catalogue = await apiJson(nrApi, cataloguePath);
  requireProof(catalogue.catalogueState === 'available' && catalogue.templates.some(x =>
    JSON.stringify(x.presentation) === JSON.stringify(presentation)), 'The bounded connector must expose the exact approved published revision');
  const preview = await apiJson(nrApi, cataloguePath + '/preview', { presentation, sourceKind: 'job' }, 'POST');
  requireProof(preview.plainText.includes('5 of 5 targets failed') && preview.plainText.includes('List requested directory'),
    'Read-only provider preview must render the shared five-target synthetic fixture');
  requireProof(JSON.stringify(h.counts()) === JSON.stringify(beforeBrowse), 'Catalogue and preview must have no business effects');

  const name = 'Synthetic directory automation';
  const query = '?organizationId=' + encodeURIComponent(mapping.ratelDeskOrganizationId) + '&connectionId=' + mappingId;
  const created = await apiJson(rdApi, '/api/v1/admin/orchestration/catalog/request-definitions' + query,
    { name, folderPath: '/', tenantId: tenant, target: { version: 1, source: 2, agentIds: [sample.agentId] },
      runner: 'bash', command: "printf '%s\\n' 'Early successful directory preparation'", executionPolicy: { threadCount: 1, nodeFailurePolicy: 1 }, inputs: [] }, 'POST');
  const jobId = String(created.orchestrationJobDefinitionId);
  for (let ordinal = 2; ordinal <= 6; ordinal++)
    await apiJson(nrApi, `/api/v1/jobs/${jobId}/steps`, { ordinal, type: 0, runner: 'bash',
      command: ordinal === 6 ? 'ls /synthetic-automation-directory-does-not-exist' : "printf '%s\\n' 'Successful preparation'",
      scriptId: null, payloadJson: null, enabled: true }, 'POST');
  const trigger = { id: crypto.randomUUID(), kind: 7, name: 'Directory job failed', position: { x: 0, y: 0 },
    executionTrigger: { sources: [1], outcomes: [2], scope: 2, definitionReferences: [{ sourceKind: 1, id: jobId }], aggregateCorrelatedEvents: true } };
  const action = { id: crypto.randomUUID(), kind: 4, name: 'Create internal directory report', position: { x: 250, y: 0 },
    connectorId: mappingId, connectorRevision: connector.revision, incidentPresentation: presentation };
  const graph = { schemaVersion: 2, nodes: [trigger, action], edges: [{ id: crypto.randomUUID(),
    sourceNodeId: trigger.id, sourcePort: 'event', targetNodeId: action.id, targetPort: 'in' }], viewport: { scale: 1 } };
  requireProof((await apiJson(nrApi, flowBase + '/validate', graph, 'POST')).valid, 'Explicit v2 action must validate before publication');
  let flow = await apiJson(nrApi, flowBase, { name: 'Synthetic directory incident', useTemplate: false }, 'POST');
  flow = await apiJson(nrApi, `${flowBase}/${flow.id}/draft`, { expectedRevision: flow.revision, name: flow.name, graph }, 'PUT');
  await apiJson(nrApi, `${flowBase}/${flow.id}/publish`, { expectedRevision: flow.revision }, 'POST');
  flow = await apiJson(nrApi, `${flowBase}/${flow.id}`);
  await apiJson(nrApi, `${flowBase}/${flow.id}/enabled`, { expectedRevision: flow.revision, enabled: true }, 'PUT');
  const templateId = crypto.randomUUID();
  const service = await apiJson(rdApi, '/api/v1/services/', { name: 'Synthetic directory service', description: 'Owned isolated failure',
    allowedCustomerIds: [], allowedOrganizationIds: [mapping.ratelDeskOrganizationId] }, 'POST');
  const form = await apiJson(rdApi, '/api/v1/request-forms/', { serviceId: service.id, title: name, description: 'Owned isolated failure',
    organizationId: mapping.ratelDeskOrganizationId, allowedOrganizationIds: [mapping.ratelDeskOrganizationId], releaseStatus: 1,
    jsonSchema: JSON.stringify({ fields: [], tasks: [{ id: templateId, name: 'Check requested directory', order: 1, type: 'automation',
      autoStart: true, dependsOn: [], expectedRuntimeMinutes: 1, graceRuntimeMinutes: 0, isCritical: false, failurePolicy: 'Continue', maxRetries: 0 }] }) }, 'POST');
  await apiJson(rdApi, '/api/v1/admin/orchestration/bindings', { systemConnectionId: mappingId, requestFormId: form.id,
    taskTemplateId: templateId, orchestrationRequestDefinitionId: String(created.requestDefinitionId),
    orchestrationRequestDefinitionName: name, orchestrationJobDefinitionId: jobId, orchestrationJobDefinitionName: name }, 'POST');
  await control('arm-trace');
  await control('hold-terminal-callbacks');
  const submitted = await apiJson(rdApi, '/api/v1/self-service/requests', { requestFormId: form.id, title: name,
    description: 'Deliberate disposable directory failure', payloadJson: '{}' }, 'POST');
  const accepted = await until('Original v2 incident commits and sender recovers the lost success', async () => {
    const rows = sql('rateldesk', `SELECT COALESCE(json_agg(json_build_object('incidentId',r."IncidentId",'accepted',r."AcceptedJson",'plainText',p."PlainText",'context',p."ContextJson")), '[]'::json) FROM "IncidentCreateReceipts" r JOIN "AutomationIncidentReports" p ON p."IncidentId"=r."IncidentId" WHERE r."SourceNamespaceId"=${literal(mappingId)}::uuid`);
    requireProof(rows.length <= 1, 'One aggregate action must retain one incident/report');
    if (rows.length === 0) return null;
    const runs = await apiJson(nrApi, `${flowBase}/${flow.id}/runs`);
    if (runs.length !== 1 || runs[0].status !== 4) return null;
    const detail = await apiJson(nrApi, `${flowBase}/${flow.id}/runs/${runs[0].id}`);
    requireProof(detail.actions.length === 1 && detail.actions[0].status === 4, 'Incident delivery must succeed independently of source failure');
    return { ...rows[0], flowRunId: runs[0].id };
  }, 120);
  const wire = JSON.parse(accepted.accepted), context = JSON.parse(accepted.context), receipt = wire.integrationReceipt;
  requireProof(receipt.contractVersion === 'rateldesk.incident-create.v2' && receipt.originRelation.status === 'linked' &&
    receipt.originRelation.sourceRequestId === submitted.requestId && receipt.presentation.contentDigest === presentation.contentDigest,
    'Accepted receipt must bind the exact native action, pinned revision and trusted source request');
  requireProof(context.origin.request.reference === submitted.trackingId && context.origin.submittedBy?.displayName &&
    context.origin.requestedFor?.displayName && context.origin.service.name === service.name,
    'Authoritative source and separate human identities must survive admission and delivery without a submission key');
  requireProof(context.targets.length === 1 && context.targets[0].failedSteps.some(s => s.ordinal === 6 && s.exitCode === 2 &&
    s.errorExcerpt?.includes('No such file or directory')), 'A later native failed activity must retain its own exit code and permitted stderr');
  requireProof(accepted.plainText.includes('1 of 1 targets failed') && accepted.plainText.includes(submitted.trackingId) &&
    !wire.subject.includes('No such file'), 'Internal diagnostics and the customer-safe subject must remain separate');
  const beforeReplay = h.counts();
  const replay = await control('replay-trace');
  requireProof(JSON.stringify(replay.receipt) === JSON.stringify(wire) && JSON.stringify(beforeReplay) === JSON.stringify(h.counts()),
    'Original v2 replay must return the exact stored acceptance without duplicate effects');
  await control('release-terminal-callbacks');
  const task = await until('Original failed source outcome remains failed after successful delivery', async () => {
    const tasks = await apiJson(rdApi, '/api/v1/request-tasks/?requestId=' + submitted.requestId);
    return tasks.items?.length === 1 && tasks.items[0].lastAutomationStatus === 'failed' ? tasks.items[0] : null;
  });
  const runId = String(task.orchestratorExecutionId);
  const original = await apiJson(nrApi, '/api/v1/jobruns/' + runId);
  requireProof(original.status === 3 && original.isAggregate && original.targetCount === 1, 'Successful incident delivery must preserve the failed native aggregate');
  for (const ticketId of [submitted.requestId, accepted.incidentId]) {
    const relations = await apiJson(rdApi, `/api/v1/tickets/${ticketId}/relations/`);
    requireProof(relations.items.filter(x => x.relationType === 2).length === 1, 'Both ticket workspaces must read the same reciprocal relation');
  }
  for (const reference of [submitted.trackingId, receipt.trackingId]) {
    const found = await until('Verified local external-reference projection catches up', async () => {
      const result = await apiJson(nrApi, '/api/v1/global-search/automation?q=' + encodeURIComponent(reference));
      return result.totalCount === 3 ? result : null;
    });
    requireProof(found.items.some(x => x.url === `/jobs/${jobId}/runs/${runId}`) &&
      found.items.some(x => x.url === `/requests?search=${context.nativeRequestId}`) &&
      found.items.some(x => x.url === `/flows?tenantId=${tenant}&run=${accepted.flowRunId}`), 'Both references must locate three distinct permitted native resources');
  }
  const proof = { sourceRequestId: submitted.requestId, sourceReference: submitted.trackingId, nativeRequestId: context.nativeRequestId,
    jobId, runId, flowRunId: accepted.flowRunId, incidentId: accepted.incidentId, incidentReference: receipt.trackingId,
    sourceOutcome: 'Failed', deliveryOutcome: 'Succeeded', nativeTargets: 1, syntheticPreviewTargets: 5, failedOrdinal: 6, childExitCode: 2,
    presentation, fingerprint: receipt.fingerprint, replayStable: true, linked: true, resourcesPerReference: 3 };
  record('automationTraceabilityHttp', proof);
  await rendered(h, { tenant, jobId, runId, flowId: flow.id, actionId: action.id, flowRunId: accepted.flowRunId,
    sourceRequestId: submitted.requestId, sourceReference: submitted.trackingId, incidentId: accepted.incidentId,
    incidentReference: receipt.trackingId, nativeRequestId: context.nativeRequestId });
  fs.writeFileSync(path.join(evidence, 'automation-traceability.json'), JSON.stringify(proof, null, 2), { mode: 0o600 });
  record('automationTraceability', proof);
  return proof;
};

async function rendered(h, ids) {
  const { chromium, expect } = require(path.join(h.runtimeRoot, 'browser/node_modules/@playwright/test'));
  const { product, login } = require('./browser_pair');
  const browser = await chromium.launch({ headless: true });
  const modes = ['light', 'dark'].flatMap(theme => [{ theme, width: 1440, height: 1000, custom: false, zoom: 1 },
    { theme, width: 390, height: 844, custom: false, zoom: 1 }, { theme, width: 1440, height: 1000, custom: true, zoom: 1 },
    { theme, width: 1440, height: 1000, custom: false, zoom: 2 }]);
  try {
    for (const mode of modes) {
      const profile = path.join(h.fixture, 'traceability-zoom-profile');
      const extension = path.join(process.env.RATELDESK_PAIRING_SOURCE_ROOT, 'tests/ux/native-zoom-extension');
      const options = { viewport: { width: mode.width, height: mode.height }, colorScheme: mode.theme, ignoreHTTPSErrors: true };
      const context = mode.zoom === 2 ? await chromium.launchPersistentContext(profile, { ...options, channel: 'chromium',
        headless: true, args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`] }) : await browser.newContext(options);
      try {
        const page = await context.newPage(); page.setDefaultTimeout(30000);
        const setZoom = async () => {
          if (mode.zoom !== 2) return;
          await expect.poll(() => context.serviceWorkers().length).toBeGreaterThan(0);
          await context.serviceWorkers()[0].evaluate(async () => {
            const tab = (await chrome.tabs.query({})).find(x => /^https?:\/\/127\.0\.0\.1:/.test(x.url));
            await chrome.tabs.setZoom(tab.id, 2);
            if (await chrome.tabs.getZoom(tab.id) !== 2) throw new Error('Native browser zoom was not applied');
          });
          await expect.poll(() => page.evaluate(() => innerWidth)).toBe(mode.width / 2);
        };
        for (const name of ['netratel', 'rateldesk']) {
          const entry = product(name); await login(page, entry);
          const route = name === 'netratel' ? `/jobs/${ids.jobId}/runs/${ids.runId}` : `/incidents/${ids.incidentId}`;
          await page.goto(entry.web + route);
          await setZoom();
          if (mode.custom) await page.addStyleTag({ content: ':root{--mud-palette-primary:#6868aa;--mud-palette-divider:#9381aa}' });
          const region = page.getByRole('region', { name: name === 'netratel' ? 'Related automation resources' : 'Automation reports', exact: true });
          await expect(region).toBeVisible(); await expect(region).toContainText(ids.sourceReference);
          if (name === 'netratel') await expect(region).toContainText(ids.incidentReference);
          else await expect(region).toContainText('1 of 1 targets failed');
          const link = region.getByRole('link').first(); await link.focus(); await expect(link).toBeFocused();
          await capture(page, path.join(h.evidence, `traceability-${name}-${mode.theme}-${mode.width}${mode.custom ? '-custom' : ''}-${mode.zoom}x.png`), mode.zoom);
        }
        const nr = product('netratel');
        await page.goto(nr.web + `/flows?tenantId=${ids.tenant}&search=Synthetic%20directory%20incident`);
        await setZoom();
        if (mode.custom) await page.addStyleTag({ content: ':root{--mud-palette-primary:#6868aa;--mud-palette-divider:#9381aa}' });
        await page.getByTestId('flow-edit').first().click();
        if (!await page.getByTestId('flow-select-node').isVisible()) await page.getByTestId('flow-palette-toggle').click();
        await page.getByTestId('flow-select-node').selectOption(ids.actionId);
        if (await page.getByTestId('flow-properties-toggle').getAttribute('aria-expanded') !== 'true')
          await page.getByTestId('flow-properties-toggle').click();
        const fields = page.getByTestId('flow-incident-presentation');
        await expect(fields.getByLabel('Use RatelDesk incident template', { exact: true })).toBeChecked();
        await fields.getByRole('button', { name: 'Preview with synthetic failure', exact: true }).click();
        await expect(fields.frameLocator('iframe').getByText('5 of 5 targets failed', { exact: false }).first()).toBeVisible();
        await capture(page, path.join(h.evidence, `traceability-template-${mode.theme}-${mode.width}${mode.custom ? '-custom' : ''}-${mode.zoom}x.png`), mode.zoom);
      } finally { await context.close(); if (mode.zoom === 2) fs.rmSync(profile, { recursive: true, force: true }); }
    }
  } finally { await browser.close(); }
}

async function capture(page, file, zoom) {
  if (zoom !== 2) return page.screenshot({ path: file, fullPage: true, animations: 'disabled',
    mask: [page.locator('input[type=password], [data-sensitive]')] });
  const mask = await page.addStyleTag({ content: 'input[type=password], [data-sensitive] { visibility: hidden !important; }' });
  const session = await page.context().newCDPSession(page);
  try {
    const surface = await session.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
    fs.writeFileSync(file, Buffer.from(surface.data, 'base64'), { mode: 0o600 });
  } finally { await session.detach(); await mask.evaluate(element => element.remove()); }
}
