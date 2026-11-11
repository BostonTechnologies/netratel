'use strict';
function admissionIdentity(value) {
  const ids = [value?.RequestId, value?.RequestTaskId, value?.CorrelationId];
  if (!ids.every(id => typeof id === 'string' && /^[a-zA-Z0-9_.:-]{1,128}$/.test(id)))
    throw new Error('The actual admission tuple must contain bounded fixture identities');
  return ids.join(':');
}
module.exports = { admissionIdentity };
