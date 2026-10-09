// Task acceptance against actual current products. All writes use authorized owner APIs.
// Read-only SQL proves durable receipt/history; private tokens remain in proxy memory.
const fs = require('fs');
const path = require('path');
const http = require('http');
const crypto = require('crypto');
const child = require('child_process');
function requiredDirectory(name) {
  const value = process.env[name];
  if (!value || !path.isAbsolute(value)) throw new Error(name + ' must explicitly select an absolute owned fixture/source directory');
  return path.resolve(value);
}
const runtimeRoot = requiredDirectory('NETRATEL_PAIRING_RUNTIME_ROOT');
const fixture = path.join(runtimeRoot, 'actual-pair');
const statePath = path.join(fixture, 'private-state.json');
const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
const evidence = path.join(fixture, 'business-evidence');
fs.mkdirSync(evidence, { recursive: true, mode: 0o700 });
const nrSource = requiredDirectory('NETRATEL_PAIRING_SOURCE_ROOT');
const rdSource = requiredDirectory('RATELDESK_PAIRING_SOURCE_ROOT');
const docker = ['--host=unix:///var/run/docker.sock'];
const dockerEnv = { ...process.env };
for (const key of ['DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_TLS', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH']) delete dockerEnv[key];
function dockerCall(args, options = {}) {
  return child.execFileSync('docker', docker.concat(args), { env: dockerEnv, encoding: 'utf8', timeout: 60000, maxBuffer: 2 * 1024 * 1024, ...options });
}
function requireProof(condition, message) { if (!condition) throw new Error(message); }
function safeJson(file, value) { fs.writeFileSync(file, JSON.stringify(value, null, 2), { mode: 0o600 }); }
function sql(product, query) {
  // No state mutation, owner grant seeding, secret unprotection or credential printing.
  const text = dockerCall(['exec', state.container, 'psql', '-XqAt', '-v', 'ON_ERROR_STOP=1', '-U', 'postgres', '-d', state.products[product].database,
    '-c', 'BEGIN TRANSACTION READ ONLY; ' + query + '; COMMIT;']);
  return JSON.parse(text.trim().split('\n').filter(Boolean).pop() || 'null');
}
function literal(value) { return "'" + String(value).replaceAll("'", "''") + "'"; }
function counts() {
  return {
    netratel: sql('netratel', `SELECT json_build_object('flows',(SELECT count(*) FROM "FlowRuns"),'actions',(SELECT count(*) FROM "FlowActions"),'jobs',(SELECT count(*) FROM "JobRuns"))`),
    rateldesk: sql('rateldesk', `SELECT json_build_object('incidents',(SELECT count(*) FROM "Tickets" WHERE "Discriminator"='Incident'),'receipts',(SELECT count(*) FROM "IncidentCreateReceipts"),'tasks',(SELECT count(*) FROM "Tickets" WHERE "Discriminator"='RequestTask'))`),
  };
}
async function until(label, operation, seconds = 60) {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    const result = await operation();
    if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error(label + ' did not complete within its bounded deadline');
}
function upstream(method, route, headers, body) {
  return new Promise((resolve, reject) => {
    const clean = { ...headers }; delete clean.host; delete clean.connection; delete clean['transfer-encoding'];
    clean['content-length'] = body.length;
    const request = http.request({ host: '127.0.0.1', port: state.products.rateldesk.apiPort, path: route, method, headers: clean, timeout: 30000 }, response => {
      const parts = []; let length = 0;
      response.on('data', part => { length += part.length; if (length > 512 * 1024) response.destroy(new Error('Actual peer response exceeded fixture bound')); else parts.push(part); });
      response.on('error', reject);
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: Buffer.concat(parts) }));
    });
    request.on('timeout', () => request.destroy(new Error('Actual peer request exceeded fixture deadline')));
    request.on('error', reject); request.end(body);
  });
}
async function runProxy() {
  requireProof(Number.isInteger(state.products.rateldesk.proxyPort), 'Launcher must allocate rateldesk.proxyPort before starting the fixture proxy');
  const controlToken = crypto.randomBytes(32).toString('base64url');
  safeJson(path.join(fixture, 'proxy-private.json'), { controlToken });
  let armed = false, lost = false, original = null, callback = null;
  const observations = [];
  const server = http.createServer(async (incoming, outgoing) => {
    try {
      const chunks = []; let size = 0;
      for await (const chunk of incoming) { size += chunk.length; requireProof(size <= 256 * 1024, 'Actual fixture request exceeded bound'); chunks.push(chunk); }
      const body = Buffer.concat(chunks);
      if (incoming.url === '/__fixture/health') { outgoing.writeHead(200, { 'content-type': 'application/json' }); outgoing.end('{"ready":true}'); return; }
      if (incoming.url === '/__fixture/control') {
        requireProof(incoming.headers['x-fixture-control'] === controlToken, 'Fixture control requires its private capability');
        const command = JSON.parse(body.toString('utf8') || '{}');
        let result;
        if (command.action === 'arm') { armed = true; result = { armed: true }; }
        else if (command.action === 'observations') result = { lost, observations };
        else if (command.action === 'replay') {
          requireProof(original !== null && lost, 'An actual committed response-loss request must exist before replay');
          const reply = await upstream(original.method, original.route, original.headers, original.body);
          requireProof(reply.status === 200, 'Identical original incident replay must return its original committed receipt with HTTP 200');
          result = { status: reply.status, receipt: JSON.parse(reply.body.toString('utf8')) };
        } else if (command.action === 'cached-receiver-status') {
          requireProof(original !== null, 'An actual issued business credential must exist');
          const headers = { ...original.headers }; delete headers['idempotency-key'];
          const authorization = headers.authorization;
          requireProof(typeof authorization === 'string' && authorization.startsWith('Bearer '), 'The original actual business authorization must remain in private proxy memory');
          const token = authorization.slice('Bearer '.length);
          const parts = token.split('.');
          requireProof(parts.length === 3, 'Revocation proof requires the actual signed bearer expiry');
          const claims = JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8'));
          requireProof(Number.isSafeInteger(claims.exp) && claims.exp > 0, 'Actual signed bearer expiry is required to distinguish revocation from natural expiry');
          const reply = await upstream('GET', '/api/v1/integrations/netratel/capabilities', headers, Buffer.alloc(0));
          result = { status: reply.status, expiresAtUnixSeconds: claims.exp, credentialSha256: crypto.createHash('sha256').update(token).digest('hex') };
        } else if (command.action === 'callback-replay') {
          requireProof(callback !== null, 'An actual authenticated callback must have been captured');
          const reply = await upstream(callback.method, callback.route, callback.headers, callback.body);
          requireProof(reply.status === 204, 'Identical actual provider result callback replay must remain harmless'); result = { status: reply.status };
        } else throw new Error('Unknown fixture control operation');
        outgoing.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' }); outgoing.end(JSON.stringify(result)); return;
      }
      const reply = await upstream(incoming.method, incoming.url, incoming.headers, body);
      if (incoming.method === 'POST' && incoming.url === '/api/v1/orchestration/provider/callback' && reply.status === 204) {
        const value = JSON.parse(body.toString('utf8'));
        if (value.status === 'succeeded') callback = { method: incoming.method, route: incoming.url, headers: { ...incoming.headers }, body };
      }
      if (incoming.method === 'POST' && incoming.url === '/api/v1/incidents/' && incoming.headers['idempotency-key']) {
        const key = incoming.headers['idempotency-key'];
        observations.push({ method: 'POST', status: reply.status, key, bodySha256: crypto.createHash('sha256').update(body).digest('hex') });
        if (armed && !lost && reply.status === 201) {
          const committed = sql('rateldesk', `SELECT json_build_object('receipts', (SELECT count(*) FROM "IncidentCreateReceipts" WHERE "Key"=${literal(key)}), 'incidents', (SELECT count(*) FROM "Tickets" t JOIN "IncidentCreateReceipts" r ON r."IncidentId"=t."Id" WHERE r."Key"=${literal(key)} AND t."Discriminator"='Incident'))`);
          requireProof(committed.receipts === 1 && committed.incidents === 1, 'Receiver must independently commit exactly one receipt/incident before its success response is lost');
          original = { method: incoming.method, route: incoming.url, headers: { ...incoming.headers }, body }; lost = true;
          observations.push({ committedBeforeLoss: true, key, receiptCount: committed.receipts, incidentCount: committed.incidents });
          outgoing.destroy(); return;
        }
      }
      const headers = { ...reply.headers }; delete headers['transfer-encoding']; delete headers.connection; headers['content-length'] = reply.body.length;
      outgoing.writeHead(reply.status, headers); outgoing.end(reply.body);
    } catch (error) {
      if (!outgoing.destroyed) { outgoing.writeHead(502, { 'content-type': 'application/json' }); outgoing.end('{"code":"fixture-upstream-failure"}'); }
      // No body, token, endpoint or arbitrary exception serialization is persisted.
      fs.appendFileSync(path.join(evidence, 'proxy-errors.log'), error.constructor.name + '\n', { mode: 0o600 });
    }
  });
  server.listen(state.products.rateldesk.proxyPort, '127.0.0.1', () => console.log('Owned actual-business proxy ready'));
  const close = () => { original = null; callback = null; server.close(() => process.exit(0)); };
  process.on('SIGTERM', close); process.on('SIGINT', close);
  setTimeout(close, 20 * 60 * 1000).unref();
}
async function control(action) {
  const privateControl = JSON.parse(fs.readFileSync(path.join(fixture, 'proxy-private.json'), 'utf8'));
  const response = await fetch(`http://127.0.0.1:${state.products.rateldesk.proxyPort}/__fixture/control`, { method: 'POST',
    headers: { 'content-type': 'application/json', 'X-Fixture-Control': privateControl.controlToken }, body: JSON.stringify({ action }), signal: AbortSignal.timeout(30000) });
  requireProof(response.ok, 'Owned actual-business fixture control failed'); return response.json();
}
async function owner(entry) {
  const { request } = require(path.join(runtimeRoot, 'browser/node_modules/@playwright/test'));
  const api = await request.newContext({ baseURL: entry.api, extraHTTPHeaders: { Origin: entry.web, 'X-Requested-With': 'XMLHttpRequest', 'X-NetRatel-Account-Request': '1' } });
  const endpoint = entry.name === 'netratel' ? '/api/v2/local-auth/login' : '/api/v1/local-auth/login';
  const response = await api.post(endpoint, { data: { email: entry.email, password: state.adminPassword, rememberMe: false } });
  requireProof(response.status() === 204, `Actual ${entry.name} owner login failed (HTTP ${response.status()})`);
  return api;
}
async function apiJson(api, route, data, method = 'GET', allowed = null) {
  const response = await api.fetch(route, { method, data, timeout: 30000 });
  if (allowed && allowed.includes(response.status())) return { status: response.status(), body: await response.json() };
  if (!response.ok()) {
    let code = 'unknown';
    try { const body = await response.json(); if (typeof body.code === 'string' && /^[a-z0-9_-]{1,80}$/.test(body.code)) code = body.code; } catch {}
    throw new Error(`Actual owner ${method} ${route} returned HTTP ${response.status()} (${code})`);
  }
  return response.status() === 204 ? null : response.json();
}
function product(name) { const entry = state.products[name]; return { ...entry, name, api: `http://127.0.0.1:${entry.apiPort}`, web: `http://127.0.0.1:${entry.webPort}` }; }
async function startClient(nrApi, tenant) {
  const output = path.join(nrSource, 'src/NetRatel/NetRatel.Client/bin/Debug/net10.0');
  requireProof(fs.existsSync(path.join(output, 'NetRatel.Client')), 'Actual current Debug Client output is required');
  const issued = await apiJson(nrApi, `/api/v1/tenants/${tenant}/enrollment-codes`, { validForMinutes: 5, maxUses: 1, note: 'Disposable actual source-pair acceptance' }, 'POST');
  requireProof(typeof issued.enrollmentCode === 'string', 'Actual enrollment response must contain its single-use code');
  const container = 'pairing-native-client-' + state.fixtureId;
  const trust = state.products.netratel.clientTrustBundle;
  requireProof(trust && fs.existsSync(trust), 'Owned TLS CA combined trust bundle is required');
  const clientConfig = path.join(fixture, 'clientsettings.json');
  safeJson(clientConfig, { Client: { ApiBaseUrl: product('netratel').api, EnrollmentCode: issued.enrollmentCode, Environment: 'Prod' },
    Gateway: { Endpoint: state.products.netratel.gatewayEndpoint, TelemetrySlowIntervalSeconds: 5 },
    RemoteSupport: { Handover: { Enabled: false } } });
  let created = false;
  try {
    dockerCall(['create', '--name', container, '--network', 'host', '--label', 'netratel.pairing.fixture=' + state.fixtureId,
      '-e', 'SSL_CERT_FILE=/run/pairing-trust.pem', '-e', 'DOTNET_ENVIRONMENT=Production', '-e', 'NetRatel_CREDENTIAL_MACHINE_ID=' + crypto.randomUUID(),
      '-w', '/client', 'mcr.microsoft.com/dotnet/runtime:10.0', '/client/NetRatel.Client', '--service']);
    created = true;
    const updated = JSON.parse(fs.readFileSync(statePath, 'utf8')); updated.clientContainer = container; safeJson(statePath, updated);
    const binary = path.join(output, 'NetRatel.Client.dll');
    const binarySha256 = crypto.createHash('sha256').update(fs.readFileSync(binary)).digest('hex');
    dockerCall(['cp', output + '/.', container + ':/client']);
    dockerCall(['cp', clientConfig, container + ':/client/clientsettings.json']);
    dockerCall(['cp', trust, container + ':/run/pairing-trust.pem']);
    dockerCall(['start', container]);
    const copiedSha256 = dockerCall(['exec', container, 'sha256sum', '/client/NetRatel.Client.dll']).trim().split(/\s+/)[0];
    requireProof(copiedSha256 === binarySha256, 'Owned native Client must execute the exact current binary copied for this acceptance');
    receipt.clientBinarySha256 = binarySha256; safeJson(path.join(evidence, 'receipt.json'), receipt);
    return container;
  } catch (error) {
    if (created) dockerCall(['rm', '-f', container]);
    throw error;
  } finally { fs.rmSync(clientConfig, { force: true }); }
}
const receipt = { fixtureId: state.fixtureId, results: [], stages: {}, historyPreserved: false, cleanupComplete: false };
function record(stage, details = {}) {
  receipt.results.push({ stage, status: 'passed', ...details }); receipt.stages[stage] = 'passed';
  safeJson(path.join(evidence, 'receipt.json'), receipt); console.log(stage + ': passed');
}
async function actualIncident(nrApi, rdApi, mapping, sample) {
  const tenant = Number(mapping.mapping.netRatelTenantId);
  const flowBase = `/api/v1/tenants/${tenant}/flows`;
  const connectors = await apiJson(nrApi, flowBase + '/connectors');
  const connector = connectors.find(c => String(c.id).toLowerCase() === mapping.mapping.id.toLowerCase());
  requireProof(connector?.canExecute, 'Final pairing Save must provision its usable tenant-owned Flow connector automatically');
  const graph = await apiJson(nrApi, flowBase + '/template');
  for (const node of graph.nodes) if (node.kind === 4) { node.connectorId = connector.id; node.connectorRevision = connector.revision; }
  const validation = await apiJson(nrApi, flowBase + '/validate', graph, 'POST'); requireProof(validation.valid, 'Actual owner Flow graph must validate');
  let flow = await apiJson(nrApi, flowBase, { name: 'Actual source-pair disk incident' }, 'POST');
  flow = await apiJson(nrApi, flowBase + '/' + flow.id + '/draft', { expectedRevision: flow.revision, name: flow.name, graph }, 'PUT');
  const version = await apiJson(nrApi, flowBase + '/' + flow.id + '/publish', { expectedRevision: flow.revision }, 'POST');
  flow = await apiJson(nrApi, flowBase + '/' + flow.id);
  if (!flow.enabled) flow = await apiJson(nrApi, flowBase + '/' + flow.id + '/enabled', { expectedRevision: flow.revision, enabled: true }, 'PUT');
  const before = counts();
  const tested = await apiJson(nrApi, `/api/v1/admin/system-connections/${mapping.pairId}/mappings/${mapping.mapping.id}/test`, null, 'POST');
  requireProof(tested.success, 'Actual authenticated named mapping test must succeed');
  const dryRun = await apiJson(nrApi, flowBase + '/dry-run', { graph, input: { agentId: sample.agentId, ruleId: crypto.randomUUID(), ruleName: 'Actual disk acceptance', clientName: 'Actual production Client', resource: sample.disk.scope, metric: 'DiskFreeSpace', severity: 'Warning', numericValue: sample.disk.freeBytes, serviceState: null, observedAtUtc: sample.observedAtUtc } }, 'POST');
  requireProof(dryRun.valid && !dryRun.skipped && dryRun.incident, 'Actual dry-run must render the intended incident without effects');
  requireProof(JSON.stringify(counts()) === JSON.stringify(before), 'Optional Test and Flow validation/dry-run must not create an incident, task, Flow action or job');
  record('sideEffectFreeTest');
  await control('arm');
  const monitoring = `/api/v2/tenants/${tenant}/monitoring`;
  const current = await apiJson(nrApi, monitoring + '/configuration');
  const ruleId = crypto.randomUUID();
  // Controlled owner rule on a genuine complete current sample. No fabricated telemetry.
  const breach = Number(sample.disk.totalBytes) + 1024, recovery = breach + 1024;
  const targets = { mode: 1, agentIds: [sample.agentId], groupIds: [] };
  const condition = { kind: 2, unit: 1, breachThreshold: breach, recoveryThreshold: recovery, resourceName: sample.disk.scope, servicePlatform: null, expectedServiceStates: [] };
  const preview = await apiJson(nrApi, monitoring + '/targets/preview', { targets, condition }, 'POST');
  requireProof(preview.agentIds.includes(sample.agentId), 'Current owner must be allowed to monitor the genuinely enrolled disk');
  await apiJson(nrApi, monitoring + '/rules/' + ruleId, { expectedConfigurationRevision: current.revision, reason: 'Controlled current disk threshold for disposable pairing acceptance', rule: {
    tenantId: tenant, ruleId, revision: 1, evaluationRevision: 1, name: 'Actual source-pair disk threshold', enabled: true, severity: 1,
    targets, condition, breachHold: '00:00:01', recoveryHold: '00:00:01', freshnessBudget: '00:01:05', publishedFlowVersionId: version.id,
  } }, 'PUT');
  const original = await until('Actual monitored Flow committed incident receipt', async () => {
    const rows = sql('rateldesk', `SELECT COALESCE(json_agg(json_build_object('id',"Id",'key',"Key",'fingerprint',"Fingerprint",'incidentId',"IncidentId",'namespaceId',"SourceNamespaceId",'accepted',"AcceptedJson")), '[]'::json) FROM "IncidentCreateReceipts" WHERE "SourceNamespaceId"=${literal(mapping.mapping.id)}::uuid`);
    requireProof(rows.length <= 1, 'One controlled occurrence must never create duplicate receiver receipts'); return rows.length === 1 ? rows[0] : null;
  });
  const run = await until('Actual sender original receipt recovery after lost success', async () => {
    const rows = await apiJson(nrApi, flowBase + '/' + flow.id + '/runs');
    requireProof(rows.length <= 1, 'One controlled monitoring occurrence must produce one Flow run');
    if (rows[0]?.status !== 4) return null;
    const detail = await apiJson(nrApi, flowBase + '/' + flow.id + '/runs/' + rows[0].id);
    requireProof(detail.actions.length === 1 && detail.actions[0].status === 4, 'Actual Flow action must durably succeed with its original committed receiver receipt');
    requireProof(detail.actions[0].receipt.incidentId === original.incidentId, 'Actual sender must record the original receiver incident identity'); return detail;
  }, 120);
  const accepted = JSON.parse(original.accepted).integrationReceipt;
  requireProof(accepted?.key === original.key && accepted.fingerprint === original.fingerprint && accepted.sourceNamespaceId === mapping.mapping.id && accepted.incidentId === original.incidentId,
    'The durable original accepted response must preserve its exact mapping namespace, action key, fingerprint and incident identity');
  const observations = await control('observations');
  requireProof(observations.lost && observations.observations.filter(r => r.committedBeforeLoss && r.key === original.key).length === 1,
    'This exact original action HTTP 201 must be dropped only after independent durable receipt and incident commit');
  const replayed = await control('replay');
  requireProof(replayed.receipt.integrationReceipt?.incidentId === original.incidentId && JSON.stringify(replayed.receipt) === JSON.stringify(JSON.parse(original.accepted)),
    'Identical original business action must return the exact originally committed accepted response and receipt');
  const after = counts(); requireProof(after.rateldesk.incidents === before.rateldesk.incidents + 1 && after.rateldesk.receipts === before.rateldesk.receipts + 1, 'Identical replay must remain exactly one incident and one receipt');
  receipt.incidentCount = 1; receipt.receiptCount = 1; receipt.incidentId = original.incidentId; receipt.receiptId = original.id; receipt.flowRunId = run.run.id;
  record('incidentReceiptReplay', { incidentId: original.incidentId, receiptId: original.id, key: original.key, fingerprint: original.fingerprint, mappingId: mapping.mapping.id, committedBeforeLoss: true });
}
async function actualAutomation(nrApi, rdApi, connection, sample) {
  const mapping = connection.mapping, name = 'Actual source-pair native command';
  const marker = 'pairing-native-result-' + state.fixtureId;
  const job = await apiJson(nrApi, '/api/v1/jobs/', { name, folderPath: '/', description: 'Bounded genuine production Client command', tenantId: Number(mapping.netRatelTenantId), clientIdentity: '', agentId: sample.agentId, expectedRuntimeSeconds: 30, graceSeconds: 10, hardTimeoutSeconds: 60 }, 'POST');
  const command = "remaining=45; while [ \"$remaining\" -gt 0 ]; do if test -f /client/acceptance-command-release; then printf '%s\\n' " + marker + "; exit 0; fi; remaining=$((remaining-1)); sleep 1; done; exit 70";
  await apiJson(nrApi, `/api/v1/jobs/${job.id}/steps`, { ordinal: 1, type: 0, runner: 'bash', command, scriptId: null, payloadJson: null, enabled: true }, 'POST');
  const templateId = crypto.randomUUID();
  const service = await apiJson(rdApi, '/api/v1/services/', { name, description: 'Disposable scoped actual automation', allowedCustomerIds: mapping.ratelDeskCustomerId ? [mapping.ratelDeskCustomerId] : [], allowedOrganizationIds: [mapping.ratelDeskOrganizationId] }, 'POST');
  const schema = JSON.stringify({ fields: [], tasks: [{ id: templateId, name, order: 1, type: 'automation', autoStart: true, dependsOn: [], expectedRuntimeMinutes: 1, graceRuntimeMinutes: 1 }] });
  const form = await apiJson(rdApi, '/api/v1/request-forms/', { serviceId: service.id, title: name, description: 'Disposable actual form task callback', organizationId: mapping.ratelDeskOrganizationId, allowedOrganizationIds: [mapping.ratelDeskOrganizationId], releaseStatus: 0, jsonSchema: schema }, 'POST');
  const binding = await apiJson(rdApi, '/api/v1/admin/orchestration/bindings', { systemConnectionId: mapping.id, requestFormId: form.id, taskTemplateId: templateId, orchestrationRequestDefinitionId: String(job.id), orchestrationRequestDefinitionName: name, orchestrationJobDefinitionId: String(job.id), orchestrationJobDefinitionName: name }, 'POST');
  requireProof(binding.enabled && binding.systemConnectionId === mapping.id, 'Actual automation binding must target the selected named mapping');
  const customers = await apiJson(rdApi, '/api/v1/customers');
  const customer = customers.find(c => c.organizationId === mapping.ratelDeskOrganizationId && c.isEnabled !== false);
  requireProof(customer, 'Actual task request requires an existing authorized customer');
  const parent = await apiJson(rdApi, '/api/v1/requests/', { title: name, description: 'Actual source-pair native execution result', priority: 0, organizationId: mapping.ratelDeskOrganizationId, customerId: customer.id, serviceId: service.id, requestFormId: form.id, payloadJson: '{}' }, 'POST');
  const tasks = await apiJson(rdApi, '/api/v1/request-tasks/?requestId=' + encodeURIComponent(parent.id));
  requireProof(tasks.items?.length === 1, 'Actual HTTP form creation must generate one automation task');
  const taskId = tasks.items[0].id;
  const acknowledged = await until('Actual recorded task acknowledgement before command release', async () => {
    const current = await apiJson(rdApi, '/api/v1/request-tasks/' + taskId);
    requireProof(current.status !== 4, 'Actual task must acknowledge its native execution');
    if (current.status !== 2 || !current.orchestratorExecutionId || !current.orchestrationExternalRequestId) return null;
    const run = await apiJson(nrApi, '/api/v1/jobruns/' + current.orchestratorExecutionId);
    return run.status === 1 ? current : null;
  }, 30);
  const bindingProof = sql('netratel', `SELECT COALESCE(json_agg(json_build_object('tenantId',"TenantId",'agentId',"AgentId",'executionId',"ExecutionId",'mappingId',"LinkId",'taskId',"RequestTaskId",'parentId',"ParentRequestId",'externalRequestId',"RequestId")), '[]'::json) FROM "ManagedOrchestrationRequestBindings" WHERE "RequestTaskId"=${literal(taskId)} AND "ParentRequestId"=${literal(parent.id)}`);
  requireProof(bindingProof.length === 1 && bindingProof[0].agentId === sample.agentId && bindingProof[0].mappingId === mapping.id && bindingProof[0].executionId === acknowledged.orchestratorExecutionId && bindingProof[0].tenantId === Number(mapping.netRatelTenantId) && String(bindingProof[0].externalRequestId) === acknowledged.orchestrationExternalRequestId,
    'Actual acknowledged execution must preserve one server-owned selected mapping/task/agent binding');
  const nativeContainer = JSON.parse(fs.readFileSync(statePath, 'utf8')).clientContainer;
  requireProof(nativeContainer === 'pairing-native-client-' + state.fixtureId, 'Command release must belong to the owned native Client');
  dockerCall(['exec', nativeContainer, 'touch', '/client/acceptance-command-release']);
  const completed = await until('Actual recorded native execution and provider result', async () => {
    const current = await apiJson(rdApi, '/api/v1/request-tasks/' + taskId);
    requireProof(current.status !== 4 && current.lastAutomationStatus !== 'failed', 'Actual automation task failed rather than returning its native result');
    return current.status === 3 && current.lastAutomationStatus === 'succeeded' ? current : null;
  }, 120);
  const executionId = completed.orchestratorExecutionId;
  requireProof(executionId && completed.completedAt && completed.orchestrationExternalRequestId, 'Actual acknowledged task must preserve correlated execution and completion identity');
  const run = await apiJson(nrApi, '/api/v1/jobruns/' + executionId);
  requireProof(run.status === 2 && run.agentId === sample.agentId && run.jobId === job.id, 'Actual current production Client job must succeed on the selected authorized agent');
  const activities = sql('netratel', `SELECT COALESCE(json_agg(json_build_object('agentId',"AgentId",'result',"ResultJson")), '[]'::json) FROM "JobTaskActivities" WHERE "JobRunId"=${literal(executionId)}::bigint`);
  requireProof(activities.length === 1, 'Actual execution must record exactly one native task activity');
  const output = JSON.parse(activities[0].result);
  requireProof(output.exitCode === 0 && Array.isArray(output.stdout) && output.stdout.includes(marker), 'Actual native command result must include the unique marker and successful exit code');
  const result = JSON.parse(completed.resultJson);
  requireProof(result.status === 'succeeded' && String(result.orchestrationRunId) === String(executionId) && String(result.resultJson).includes(marker), 'Actual authenticated provider callback must carry the correlated execution result');
  const before = await apiJson(rdApi, `/api/v1/requests/${parent.id}/worklogs`);
  const successWorklogs = before.filter(r => String(r.notesText).includes("reported 'succeeded'"));
  requireProof(successWorklogs.length === 1 && successWorklogs[0].technicianId === null && String(successWorklogs[0].notesText).includes(executionId),
    'Actual machine callback must create exactly one correlated success worklog without impersonating a human technician');
  await control('callback-replay');
  const after = await apiJson(rdApi, `/api/v1/requests/${parent.id}/worklogs`);
  requireProof(JSON.stringify(before.map(r => r.id).sort()) === JSON.stringify(after.map(r => r.id).sort()), 'Actual terminal callback replay must not duplicate task worklogs');
  receipt.executionId = String(executionId); receipt.resultReceived = true; receipt.taskId = taskId; receipt.parentRequestId = parent.id;
  record('nativeAutomationResult', { executionId: String(executionId), taskId, parentRequestId: parent.id, nativeExitCode: output.exitCode, callbackReplayHarmless: true });
}
async function runBusiness() {
  let container = null, nrApi = null, rdApi = null;
  try {
    receipt.sources = { netratel: child.execFileSync('git', ['rev-parse', 'HEAD'], { cwd: nrSource, encoding: 'utf8' }).trim(), rateldesk: child.execFileSync('git', ['rev-parse', 'HEAD'], { cwd: rdSource, encoding: 'utf8' }).trim() };
    nrApi = await owner(product('netratel')); rdApi = await owner(product('rateldesk'));
    const nrRows = await apiJson(nrApi, '/api/v1/admin/system-connections');
    const rdRows = await apiJson(rdApi, '/api/v1/admin/system-connections');
    requireProof(state.pairing?.incidentMapping?.id && state.pairing?.automationOnlyMapping?.id, 'Actual rendered pairing evidence must identify both explicitly saved mappings');
    const incident = nrRows.find(r => r.mapping?.id === state.pairing.incidentMapping.id && r.mapping.createIncidents && String(r.status).toLowerCase() === 'connected');
    const automation = rdRows.find(r => r.mapping?.id === state.pairing.automationOnlyMapping.id && r.mapping.runAutomation && !r.mapping.createIncidents && String(r.status).toLowerCase() === 'connected');
    requireProof(incident && automation, 'Both actual requested business capabilities must have saved authorized mappings');
    requireProof(Number(incident.mapping.netRatelTenantId) === Number(automation.mapping.netRatelTenantId), 'Bounded fixture mappings must select the same existing NetRatel tenant');
    const baseline = sql('netratel', 'SELECT json_build_object(\'agents\',(SELECT count(*) FROM "Agents"),\'runs\',(SELECT count(*) FROM "JobRuns"),\'actions\',(SELECT count(*) FROM "FlowActions"))');
    requireProof(baseline.agents === 0 && baseline.runs === 0 && baseline.actions === 0, 'Disposable business acceptance requires no seeded agent, execution or Flow action');
    container = await startClient(nrApi, Number(incident.mapping.netRatelTenantId));
    const sample = await until('Actual enrolled Client admitted disk telemetry', async () => {
      const samples = await apiJson(nrApi, '/api/v2/agent-telemetry');
      const current = samples.find(s => s.tenantId === Number(incident.mapping.netRatelTenantId) && s.isAuthoritative);
      if (!current) return null;
      const disk = current.disks.find(d => d.collectionQuality === 1 && typeof d.totalBytes === 'number' && typeof d.freeBytes === 'number' && d.totalBytes > 0 && d.freeBytes >= 0 && d.collectionId && d.collectedAtUtc);
      if (!disk) return null;
      const committed = sql('netratel', `SELECT json_build_object('owners',count(*),'heartbeat',COALESCE(min(o."LastHeartbeatSequence"),0),'registration',COALESCE(min(s."CommittedRegistrationOrdinal"),0)) FROM "ClientConnectionOwners" o JOIN "MonitoringEvidenceStreams" s ON s."TenantId"=o."TenantId" AND s."AgentId"=o."AgentId" AND s."ConnectionId"=o."ConnectionId" AND s."ConnectionEpoch"=o."ConnectionEpoch" WHERE o."AgentId"=${literal(current.agentId)}::uuid AND o."Active" AND s."Active" AND o."PresenceExpiresAtUtc">clock_timestamp() AND o."AuthenticationExpiresAtUtc">clock_timestamp()`);
      return committed.owners === 1 && committed.heartbeat > 0 && committed.registration > 0 ? { ...current, disk } : null;
    }, 60);
    record('actualNativeClientAdmission', { agentId: sample.agentId, diskScope: sample.disk.scope, actualCollectionId: sample.disk.collectionId });
    await actualIncident(nrApi, rdApi, incident, sample);
    await actualAutomation(nrApi, rdApi, automation, sample);
    // Normal process restart; read preserved real histories after current owner login.
    child.execFileSync('python3', [path.join(__dirname, 'native_pair.py'), 'restart', 'netratel'], { env: process.env, timeout: 90000, stdio: 'ignore' });
    child.execFileSync('python3', [path.join(__dirname, 'native_pair.py'), 'restart', 'rateldesk'], { env: process.env, timeout: 90000, stdio: 'ignore' });
    await nrApi.dispose(); await rdApi.dispose(); nrApi = await owner(product('netratel')); rdApi = await owner(product('rateldesk'));
    const preserved = await apiJson(rdApi, '/api/v1/request-tasks/' + receipt.taskId);
    requireProof(preserved.orchestratorExecutionId === receipt.executionId && preserved.lastAutomationStatus === 'succeeded', 'Restart must preserve actual task execution/result history');
    const durable = counts(); requireProof(durable.rateldesk.receipts === 1 && durable.rateldesk.incidents === 1 && durable.netratel.jobs === 1, 'Restart must preserve exactly one incident/receipt and actual job history');
    receipt.historyPreserved = true; record('historyPreserved');
  } catch (error) {
    receipt.results.push({ stage: 'actualBusinessAcceptance', status: 'failed', exceptionType: error.constructor.name, reason: String(error.message).split('\n')[0].slice(0, 240) });
    safeJson(path.join(evidence, 'receipt.json'), receipt); throw error;
  } finally {
    if (nrApi) await nrApi.dispose(); if (rdApi) await rdApi.dispose();
    if (container) {
      const logs = dockerCall(['logs', container]); fs.writeFileSync(path.join(evidence, 'private-native-client.log'), logs, { mode: 0o600 });
      dockerCall(['rm', '-f', container]);
      requireProof(!dockerCall(['ps', '-aq', '--filter', 'name=^/' + container + '$']).trim(), 'Owned native Client must be positively removed');
      receipt.cleanupComplete = true; safeJson(path.join(evidence, 'receipt.json'), receipt);
    }
  }
}
if (require.main === module) {
  (process.argv[2] === 'proxy' ? runProxy() : runBusiness()).catch(error => { console.error('Actual business fixture failed: ' + String(error.message).split('\n')[0].slice(0, 240)); process.exitCode = 1; });
}
module.exports = { control, counts, sql, runBusiness };
