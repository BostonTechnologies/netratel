'use strict';
function safeFailure(error) {
  const type = ['Error', 'TypeError', 'SyntaxError', 'TimeoutError'].includes(error?.constructor?.name) ? error.constructor.name : 'Error';
  const frames = [...String(error?.stack || '').matchAll(/\b(self_service_pair|automation_traceability_pair|business_pair|browser_pair|delete_pair)\.js:(\d+):\d+/g)];
  // A shared requireProof wrapper precedes the actual form/recovery assertion.
  const frame = frames.find(candidate => ['self_service_pair', 'automation_traceability_pair'].includes(candidate[1])) || frames[0];
  return { exceptionType: type, reasonCode: 'actual_business_assertion_or_transport_failed',
    helperFile: frame ? frame[1] + '.js' : null, helperLine: frame ? Number(frame[2]) : null };
}
module.exports = { safeFailure };
