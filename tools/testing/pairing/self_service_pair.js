// Real rendered self submission and native provider outcomes in the owned fixture.
const path = require('path');
const crypto = require('crypto');
const child = require('child_process');

module.exports = async function selfServicePair(h) {
  const { runtimeRoot, fixture, state, nrApi, rdApi, connection, sample, apiJson, sql, literal,
    until, requireProof, control, dockerCall, record, evidence } = h;
  const { chromium, expect, request } = require(path.join(runtimeRoot, 'browser/node_modules/@playwright/test'));
  const mapping = connection.mapping;
  const owner = sql('rateldesk', `SELECT json_build_object('id',u."Id",'name',u."Name",'organizationId',u."OrganizationId",'links',(SELECT count(*) FROM "CustomerAuthLinks" l WHERE l."LocalAccountId"=u."Id")) FROM "Users" u JOIN "AspNetUsers" a ON a."Id"=u."Id" WHERE a."Email"=${literal(state.products.rateldesk.email)}`);
  requireProof(owner && owner.organizationId === mapping.ratelDeskOrganizationId && owner.links === 0,
    'Rendered acceptance must begin with an enabled unlinked local owner already in the saved connection organization');
  const web = `http://127.0.0.1:${state.products.rateldesk.webPort}`;
  const ordinaryEmail = 'self-requester-' + state.fixtureId + '@example.test';
  // Technician creation deliberately leaves the supported local account unlinked.
  // It grants normal organization-scoped access, with no instance-admin exception.
  const activation = await apiJson(rdApi, '/api/v1/local-auth/users', { displayName: 'Synthetic self requester',
    email: ordinaryEmail, role: 'Technician', organizationId: mapping.ratelDeskOrganizationId }, 'POST');
  await apiJson(rdApi, '/api/v1/local-auth/activate', { email: ordinaryEmail,
    activationToken: activation.activationToken, newPassword: state.adminPassword }, 'POST');
  const ordinary = sql('rateldesk', `SELECT json_build_object('id',u."Id",'name',u."Name",'organizationId',u."OrganizationId",'administrator',a."IsInstanceAdministrator",'links',(SELECT count(*) FROM "CustomerAuthLinks" l WHERE l."LocalAccountId"=u."Id")) FROM "Users" u JOIN "AspNetUsers" a ON a."Id"=u."Id" WHERE u."Id"=${literal(activation.userId)}`);
  requireProof(ordinary && !ordinary.administrator && ordinary.links === 0 && ordinary.organizationId === mapping.ratelDeskOrganizationId,
    'The ordinary rendered requester must begin unlinked in its original authorized organization');
  const ordinaryApi = await request.newContext({ baseURL: `http://127.0.0.1:${state.products.rateldesk.apiPort}`,
    extraHTTPHeaders: { Origin: web, 'X-Requested-With': 'XMLHttpRequest' } });
  const login = await ordinaryApi.post('/api/v1/local-auth/login', { data: { email: ordinaryEmail, password: state.adminPassword, rememberMe: false } });
  requireProof(login.status() === 204, 'Ordinary local requester must authenticate normally');
  const browser = await chromium.launch({ headless: true });
  let context, page;
  try {
    for (const mode of ['self-success', 'self-failure', 'on-behalf', 'lost-callback']) {
      const requester = mode === 'self-success' ? ordinary : owner;
      const requesterEmail = mode === 'self-success' ? ordinaryEmail : state.products.rateldesk.email;
      const requesterApi = mode === 'self-success' ? ordinaryApi : rdApi;
      if (context) await context.close();
      context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
      page = await context.newPage();
      await page.goto(web + '/login');
      await page.locator('[data-testid="local-login-form"][data-interactive="true"]').waitFor({ state: 'attached', timeout: 90000 });
      await page.locator('input[name="email"]').fill(requesterEmail);
      await page.locator('input[name="password"]').fill(state.adminPassword);
      await page.getByTestId('local-login-form').locator('button[type="submit"]').click();
      await expect(page).not.toHaveURL(/\/login(?:[/?]|$)/);
      const failed = mode === 'self-failure', held = mode === 'lost-callback';
      if (held) {
        await control('hold-terminal-callbacks');
        await control('block-execution-reads', 'netratel');
      }
      const name = 'Rendered native self request ' + mode;
      const marker = 'rendered-self-' + state.fixtureId + '-' + mode;
      const gate = '/client/self-service-release-' + mode;
      const command = `remaining=45; while [ "$remaining" -gt 0 ]; do if test -f ${gate}; then printf '%s\\n' ${marker}; exit ${failed ? 42 : 0}; fi; remaining=$((remaining-1)); sleep 1; done; exit 70`;
      const query = '?organizationId=' + encodeURIComponent(mapping.ratelDeskOrganizationId) + '&connectionId=' + encodeURIComponent(mapping.id);
      const job = await apiJson(rdApi, '/api/v1/admin/orchestration/catalog/request-definitions' + query,
        { name, folderPath: '/', tenantId: Number(mapping.netRatelTenantId),
          target: { version: 1, source: 2, agentIds: [sample.agentId] }, runner: 'bash', command,
          executionPolicy: { threadCount: 1, nodeFailurePolicy: 1 }, inputs: [] }, 'POST');
      const template = crypto.randomUUID();
      const tasks = [{ id: template, name, order: 1, type: 'automation', autoStart: true, dependsOn: [], expectedRuntimeMinutes: 1, graceRuntimeMinutes: 0 }];
      if (held) tasks.push({ id: crypto.randomUUID(), name: 'Dependent manual review', order: 2, type: 'manual', autoStart: false, dependsOn: [template] });
      const service = await apiJson(rdApi, '/api/v1/services/', { name, description: 'Owned rendered self-service acceptance', allowedCustomerIds: [], allowedOrganizationIds: [mapping.ratelDeskOrganizationId] }, 'POST');
      const form = await apiJson(rdApi, '/api/v1/request-forms/', { serviceId: service.id, title: name,
        description: 'Synthetic native execution proof', organizationId: mapping.ratelDeskOrganizationId,
        allowedOrganizationIds: [mapping.ratelDeskOrganizationId], releaseStatus: 0,
        jsonSchema: JSON.stringify({ title: name, fields: [{ id: 'choice', name: 'choice', label: 'Synthetic choice', type: 'text', required: true }], tasks }) }, 'POST');
      const binding = await apiJson(rdApi, '/api/v1/admin/orchestration/bindings', { systemConnectionId: mapping.id,
        requestFormId: form.id, taskTemplateId: template, orchestrationRequestDefinitionId: String(job.requestDefinitionId),
        orchestrationRequestDefinitionName: name, orchestrationJobDefinitionId: String(job.orchestrationJobDefinitionId), orchestrationJobDefinitionName: name }, 'POST');
      requireProof(binding.systemConnectionId === mapping.id && binding.enabled, 'Self form must retain the exact saved binding');
      await page.goto(web + '/self-service/request/' + form.id);
      await page.getByLabel('Synthetic choice', { exact: false }).fill('Opt1');
      if (mode === 'on-behalf') {
        await page.getByRole('button', { name: 'Select', exact: true }).click();
        await page.getByPlaceholder('Search users').fill(state.products.rateldesk.email);
        await page.getByRole('button', { name: 'Select ' + owner.name, exact: true }).first().click();
      } else {
        await expect(page.getByText('Submit this request as yourself.', { exact: true })).toBeVisible();
        await expect(page.getByTestId('submission-organization')).toContainText('Acceptance RatelDesk organization');
      }
      await page.getByRole('button', { name: 'Submit', exact: true }).click();
      if (mode === 'on-behalf') await page.getByRole('button', { name: 'Yes', exact: true }).click();
      await expect(page.getByRole('heading', { name: 'Request submitted', exact: true })).toBeVisible({ timeout: 90000 });
      const receiptElement = page.getByTestId('submitted-request-receipt');
      const submitted = { requestId: await receiptElement.getAttribute('data-request-id'), trackingId: await receiptElement.getAttribute('data-tracking-id') };
      requireProof(submitted.requestId && submitted.trackingId, 'Rendered form must expose its real committed receipt');
      const saved = sql('rateldesk', `SELECT json_build_object('submissionId',"SelfServiceSubmissionId",'title',"Title",'description',"Description",'payloadJson',"PayloadJson") FROM "Tickets" WHERE "Id"=${literal(submitted.requestId)} AND "RequestFormId"=${literal(form.id)}`);
      requireProof(/^[0-9a-f]{32}$/.test(saved.submissionId), 'Rendered form must persist its retry identity');
      const submissionId = saved.submissionId.replace(/^(........)(....)(....)(....)(............)$/, '$1-$2-$3-$4-$5');
      const originalDto = { submissionId, requestFormId: form.id, title: saved.title, description: saved.description,
        payloadJson: saved.payloadJson, requestedForPersonId: mode === 'on-behalf' ? owner.id : null,
        requestedForPersonSource: mode === 'on-behalf' ? 0 : null };
      const retry = await apiJson(requesterApi, '/api/v1/self-service/requests', originalDto, 'POST');
      requireProof(retry.requestId === submitted.requestId, 'Exact correction/retry must retain one original request');
      const rows = await apiJson(rdApi, '/api/v1/request-tasks/?requestId=' + encodeURIComponent(submitted.requestId));
      const automation = rows.items.filter(task => task.type === 2);
      requireProof(automation.length === 1, 'One form submission must generate exactly one automation task');
      const taskId = automation[0].id;
      const admitted = await until('Rendered request native admission', async () => {
        const task = await apiJson(rdApi, '/api/v1/request-tasks/' + taskId);
        return task.orchestratorExecutionId && task.orchestrationExternalRequestId ? task : null;
      }, 60);
      const proof = sql('netratel', `SELECT json_agg(json_build_object('requestId',"RequestId",'parentId',"ParentRequestId",'taskId',"RequestTaskId",'runId',"ExecutionId",'mappingId',"LinkId",'tenantId',"TenantId",'correlationId',"CorrelationId")) FROM "ManagedOrchestrationRequestBindings" WHERE "RequestTaskId"=${literal(taskId)}`);
      requireProof(proof?.length === 1 && proof[0].parentId === submitted.requestId && proof[0].mappingId === mapping.id &&
        proof[0].runId === admitted.orchestratorExecutionId && String(proof[0].requestId) === admitted.orchestrationExternalRequestId &&
        proof[0].correlationId === admitted.automationAttemptCorrelationId && proof[0].tenantId === Number(mapping.netRatelTenantId),
      'Actual provider admission must match the exact rendered request/task/attempt and saved mapping');
      const requestProof = sql('rateldesk', `SELECT json_build_object('requester',t."CustomerId",'organizationId',t."OrganizationId",'linkOwner',l."LocalAccountId") FROM "Tickets" t JOIN "CustomerAuthLinks" l ON l."CustomerId"=t."CustomerId" WHERE t."Id"=${literal(submitted.requestId)} AND l."LocalAccountId"=${literal(requester.id)}`);
      requireProof(requestProof?.organizationId === requester.organizationId && requestProof.linkOwner === requester.id,
        'Persisted self requester must retain the authenticated account and original eligible organization');
      if (mode === 'self-success') {
        const contact = await apiJson(rdApi, '/api/v1/customers/' + requestProof.requester);
        await apiJson(rdApi, '/api/v1/customers/' + requestProof.requester,
          { ...contact, name: contact.name + ' reviewed', identityUserId: 'untrusted-client-value' }, 'PUT');
        const identity = sql('rateldesk', `SELECT json_build_object('identityUserId',"IdentityUserId") FROM "Customers" WHERE "Id"=${literal(requestProof.requester)}`);
        requireProof(identity.identityUserId === requester.id, 'Ordinary contact editing and client input must not replace the server-owned identity binding');
      }
      if (mode === 'self-success') {
        await page.goto(web + '/self-service/my-requests');
        await page.getByRole('textbox', { name: 'Search tracking ID or title' }).fill(submitted.trackingId);
        await expect(page.getByRole('link', { name: 'Open request ' + submitted.trackingId + ': ' + saved.title, exact: true })).toBeVisible();
        const owned = await apiJson(ordinaryApi, '/api/v1/self-service/requests?q=' + encodeURIComponent(submitted.trackingId));
        requireProof(owned.items.length === 1 && owned.items[0].id === submitted.requestId,
          'The newly linked ordinary requester must find its actual request without the instance-admin exception');
      }
      await page.goto(web + '/requests/' + submitted.requestId);
      await page.getByRole('tab', { name: /^Tasks/ }).click();
      dockerCall(['exec', state.clientContainer || ('pairing-native-client-' + state.fixtureId), 'touch', gate]);
      if (held) {
        await until('Actual terminal callback withheld at transport boundary', async () => (await control('callback-fault-state')).held.length > 0, 60);
        child.execFileSync('python3', [path.join(__dirname, 'native_pair.py'), 'restart', 'rateldesk'], { env: process.env, timeout: 90000, stdio: 'ignore' });
        const unknown = await until('Restart-safe durable execution deadline', async () => {
          const task = await apiJson(rdApi, '/api/v1/request-tasks/' + taskId);
          return task.lastAutomationStatus === 'ExecutionUnknownManualReconcile' ? task : null;
        }, 150);
        requireProof(unknown.orchestratorExecutionId === admitted.orchestratorExecutionId && unknown.automationTimedOutAt,
          'Timeout must retain the exact admitted run and persisted unknown outcome');
        const parent = await apiJson(rdApi, '/api/v1/requests/' + submitted.requestId);
        requireProof(parent.workflowStatus === 'NeedsReconciliation', 'Unknown outcome must hold the parent');
        await page.goto(web + '/requests/' + submitted.requestId);
        await page.getByRole('tab', { name: /^Tasks/ }).click();
        const unknownRow = page.locator('[data-task-id="' + taskId + '"]');
        await expect(unknownRow).toContainText('Timed out');
        await expect(unknownRow.getByRole('link', { name: 'Open recovery' })).toBeVisible();
        await expect(unknownRow.getByRole('button', { name: 'Retry task', exact: true })).toHaveCount(0);
        const heldDependents = await apiJson(rdApi, '/api/v1/request-tasks/?requestId=' + encodeURIComponent(submitted.requestId));
        requireProof(heldDependents.items.some(task => task.type === 1 && task.isBlocked),
          'Unknown execution must leave dependent work blocked without offering an execution retry');
        const transport = await control('restore-execution-reads', 'netratel');
        requireProof(transport.blockedReads > 0, 'Actual deadline processing must have observed the unavailable provider read');
        const delivered = await control('release-terminal-callbacks');
        requireProof(delivered.delivered === 1, 'Exactly one original held terminal result must be released');
      }
      const completed = await until('Actual rendered submission final native result', async () => {
        const task = await apiJson(rdApi, '/api/v1/request-tasks/' + taskId);
        return task.lastAutomationStatus === (failed ? 'failed' : 'succeeded') ? task : null;
      }, 120);
      requireProof(completed.orchestratorExecutionId === admitted.orchestratorExecutionId, 'Final outcome must update the original attempt');
      const parent = await until('Committed parent workflow recomputation', async () => {
        const current = await apiJson(rdApi, '/api/v1/requests/' + submitted.requestId);
        return current.workflowStatus && !['NeedsReconciliation', 'Running', 'NotStarted'].includes(current.workflowStatus) ? current : null;
      }, 30);
      requireProof(parent.workflowStatus !== 'NeedsReconciliation', 'Late terminal outcome must recover its parent hold');
      const actualRun = await apiJson(nrApi, '/api/v1/jobruns/' + admitted.orchestratorExecutionId);
      requireProof(actualRun.status === (failed ? 3 : 2), 'Provider terminal state must be truthful for controlled success/failure');
      let liveConvergenceMilliseconds = null;
      if (!held) {
        const observedAt = Date.now();
        await expect(page.locator('[data-task-id="' + taskId + '"]')).toContainText(failed ? 'Failed' : 'Completed', { timeout: 1500 });
        await expect(page.getByTestId('request-workflow-summary')).toContainText(parent.workflowStatus.replace(/([a-z])([A-Z])/g, '$1 $2'), { timeout: 1500 });
        const expectedHeader = ['New', 'Waiting reply', 'Replied', 'In progress', 'Pending approval', 'On hold', 'Resolved'][parent.state];
        await expect(page.getByTestId('request-committed-state')).toContainText(expectedHeader, { timeout: 1500 });
        liveConvergenceMilliseconds = Date.now() - observedAt;
        requireProof(liveConvergenceMilliseconds <= 1500, 'Healthy committed state must converge through the actual gateway stream within the remaining two-second budget');
      }

      if (held) {
        const dependents = await apiJson(rdApi, '/api/v1/request-tasks/?requestId=' + encodeURIComponent(submitted.requestId));
        requireProof(dependents.items.some(task => task.type === 1 && !task.isBlocked), 'Late same-attempt success must unblock dependent work');
      }
      await page.reload();
      await expect(page.getByTestId('request-workflow-summary')).toBeVisible({ timeout: 90000 });
      await page.screenshot({ path: path.join(evidence, mode + '-rendered-outcome.png'), fullPage: true, animations: 'disabled' });
      record('renderedSelfService-' + mode, { requestId: submitted.requestId, customerId: requestProof.requester,
        organizationId: requester.organizationId, requesterKind: mode === 'self-success' ? 'ordinary-local' : 'local-administrator', ordinaryMyRequestsVerified: mode === 'self-success', taskId, connectionId: mapping.id, jobId: String(job.requestDefinitionId),
        providerRequestId: admitted.orchestrationExternalRequestId, providerRunId: admitted.orchestratorExecutionId,
        correlationId: admitted.automationAttemptCorrelationId, providerState: actualRun.status,
        localStatus: completed.lastAutomationStatus, parentStatus: parent.workflowStatus, submissionRetryRetained: true, liveConvergenceMilliseconds });
    }
  } finally {
    if (context) await context.close(); await browser.close(); await ordinaryApi.dispose();
  }
};
