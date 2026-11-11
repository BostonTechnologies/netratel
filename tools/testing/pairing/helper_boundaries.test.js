'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { safeFailure } = require('./safe_failure');
test('malformed upstream payload never enters exported failure diagnostics', () => {
  const error = new SyntaxError('Unexpected sensitive-response-body at byte 1');
  error.stack = error.message + '\n at /owned/self_service_pair.js:123:45';
  assert.deepEqual(safeFailure(error), { exceptionType: 'SyntaxError',
    reasonCode: 'actual_business_assertion_or_transport_failed', helperFile: 'self_service_pair.js', helperLine: 123 });
  assert.equal(JSON.stringify(safeFailure(error)).includes('sensitive-response-body'), false);
});
test('unknown error types and stack locations cannot supply exported text', () => {
  const actual = safeFailure({ constructor: { name: 'sensitive-response-body' }, stack: 'private-host:11:22' });
  assert.deepEqual(actual, { exceptionType: 'Error', reasonCode: 'actual_business_assertion_or_transport_failed', helperFile: null, helperLine: null });
});
test('shared assertion wrapper preserves the bounded caller without exporting its message', () => {
  const error = new Error('sensitive-response-body');
  error.stack = error.message + '\n at requireProof (/owned/business_pair.js:29:66)\n at selfServicePair (/owned/self_service_pair.js:198:9)';
  assert.deepEqual(safeFailure(error), { exceptionType: 'Error',
    reasonCode: 'actual_business_assertion_or_transport_failed', helperFile: 'self_service_pair.js', helperLine: 198 });
  assert.equal(JSON.stringify(safeFailure(error)).includes('sensitive-response-body'), false);
});
const { admissionIdentity } = require('./admission_identity');
test('admission observation matches production General JSON naming', () => {
  const actualWireBody = { RequestId: 'request-1', RequestTaskId: 'task-1', CorrelationId: 'attempt-1', Payload: { private: 'ignored' } };
  assert.equal(admissionIdentity(actualWireBody), 'request-1:task-1:attempt-1');
});
test('malformed admission identity fails without exporting the body', () => {
  assert.throws(() => admissionIdentity({ RequestId: 'sensitive-response-body\n' }),
    { message: 'The actual admission tuple must contain bounded fixture identities' });
});
