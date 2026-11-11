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
