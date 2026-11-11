'use strict';
function safeFailure(error) {
  const type = ['Error', 'TypeError', 'SyntaxError', 'TimeoutError'].includes(error?.constructor?.name) ? error.constructor.name : 'Error';
  const frame = String(error?.stack || '').match(/\b(self_service_pair|business_pair|browser_pair|delete_pair)\.js:(\d+):\d+/);
  return { exceptionType: type, reasonCode: 'actual_business_assertion_or_transport_failed',
    helperFile: frame ? frame[1] + '.js' : null, helperLine: frame ? Number(frame[2]) : null };
}
module.exports = { safeFailure };
