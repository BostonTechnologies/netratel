// Load after the pinned MudEx event bridge and before Blazor boots. Delegate
// UI events still travel into popouts, while document startup/visibility
// belongs to the original page and must not run a second Blazor startup.
(() => {
  if (!window.MudExPopoutEvents || window.netratelPopoutLifecycleInstalled) return;
  window.netratelPopoutLifecycleInstalled = true;
  const bridged = document.addEventListener.bind(document);
  const native = EventTarget.prototype.addEventListener;
  const lifecycle = new Set(['DOMContentLoaded', 'readystatechange', 'visibilitychange']);
  document.addEventListener = function (type, listener, options) {
    if (lifecycle.has(type)) return native.call(document, type, listener, options);
    return bridged(type, listener, options);
  };
})();
