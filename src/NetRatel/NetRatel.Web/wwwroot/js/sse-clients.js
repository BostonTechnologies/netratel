window.netratelClients = (function () {
  let es = null;
  let dotnet = null;

  function connect(url, dotnetRef) {
    disconnect();

    dotnet = dotnetRef;
    es = new EventSource(url, { withCredentials: true });

    es.addEventListener('client-upsert', e => {
      if (dotnet) {
        dotnet.invokeMethodAsync('HandleUpsert', e.data).catch(() => {});
      }
    });

    es.addEventListener('client-delete', e => {
      if (dotnet) {
        dotnet.invokeMethodAsync('HandleDelete', e.data).catch(() => {});
      }
    });

    es.onerror = () => {
      // Browser will auto-reconnect; nothing else to do.
    };
  }

  function disconnect() {
    if (es) {
      es.close();
      es = null;
    }
  }

  return { connect, disconnect };
})();