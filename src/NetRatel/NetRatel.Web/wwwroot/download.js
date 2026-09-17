window.saveFile = (fileName, contentType, bytesBase64) => {
  const link = document.createElement('a');
  link.download = fileName;
  link.href = "data:" + contentType + ";base64," + bytesBase64;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
};

window.netratelDownloads = {
  downloads: new Map(),
  start: (url, id, callbacks) => {
    const frame = document.createElement('iframe');
    frame.style.display = 'none';
    const transfer = { frame, callbacks, poll: 0, lastStatus: null };
    window.netratelDownloads.downloads.set(id, transfer);
    frame.src = url;
    document.body.appendChild(frame);
    transfer.poll = window.setInterval(() => window.netratelDownloads.poll(id), 500);
    void window.netratelDownloads.poll(id);
  },
  poll: async (id) => {
    const transfer = window.netratelDownloads.downloads.get(id);
    if (!transfer) { return; }
    const statusUrl = `/file-browser/transfers/download/${encodeURIComponent(id)}/status`;
    try {
      const response = await fetch(statusUrl, { credentials: 'same-origin' });
      if (!response.ok) { return; }
      const status = await response.json();
      transfer.lastStatus = status;
      if (transfer.callbacks) {
        try {
          await transfer.callbacks.invokeMethodAsync('OnDownloadStatus', status.state, status.bytesTransferred, status.expectedBytes, status.failureCode);
        } catch (_) {
          // A dialog can close while its native attachment finishes. The transfer still needs cleanup.
        }
      }
      if (['completed', 'failed', 'cancelled'].includes(status.state)) {
        window.netratelDownloads.cleanup(id);
      }
    } catch (_) {
      // The transfer status is best-effort UI telemetry; the native stream owns bytes.
    }
  },
  cancel: async (id) => {
    const transfer = window.netratelDownloads.downloads.get(id);
    if (!transfer) { return; }
    transfer.frame.remove();
    try {
      const response = await fetch(`/file-browser/transfers/download/${encodeURIComponent(id)}/cancel`, { method: 'POST', credentials: 'same-origin' });
      if (!response.ok) { throw new Error(`Download cancellation failed with status ${response.status}.`); }
      // Keep polling until the server's authoritative terminal state reaches Blazor.
      await window.netratelDownloads.poll(id);
    } catch (_) {
      const bytesTransferred = transfer.lastStatus?.bytesTransferred ?? 0;
      const expectedBytes = transfer.lastStatus?.expectedBytes ?? null;
      try {
        await transfer.callbacks?.invokeMethodAsync('OnDownloadStatus', 'failed', bytesTransferred, expectedBytes, 'cancel_failed');
      } finally {
        window.netratelDownloads.cleanup(id);
      }
    }
  },
  cleanup: (id) => {
    const transfer = window.netratelDownloads.downloads.get(id);
    if (!transfer) { return; }
    window.clearInterval(transfer.poll);
    transfer.frame.remove();
    window.netratelDownloads.downloads.delete(id);
  }
};

window.netratelFileTransfers = {
  pick: (input) => input.click(),
  uploads: new Map(),
  upload: (input, url, id, callbacks) => new Promise((resolve, reject) => {
    const file = input.files && input.files[0];
    if (!file) { reject(new Error("Choose a file to upload.")); return; }
    const maximumAttempts = 5;
    const transfer = { request: null, retryTimer: 0, attempt: 1, cancelled: false, settled: false, finish: null };
    const notify = (method, ...args) => {
      if (callbacks) { void callbacks.invokeMethodAsync(method, ...args).catch(() => {}); }
    };
    const finish = (error) => {
      if (transfer.settled) { return; }
      transfer.settled = true;
      window.clearTimeout(transfer.retryTimer);
      window.netratelFileTransfers.uploads.delete(id);
      if (error) { reject(error); } else { resolve(); }
    };
    transfer.finish = finish;
    const retry = (reason) => {
      if (transfer.cancelled || transfer.attempt >= maximumAttempts) {
        finish(new Error(reason));
        return;
      }

      transfer.request = null;
      transfer.attempt += 1;
      const delay = 1000 * Math.pow(2, transfer.attempt - 2);
      notify("OnUploadRetrying", transfer.attempt, maximumAttempts);
      notify("OnUploadProgress", 0, file.size);
      transfer.retryTimer = window.setTimeout(send, delay);
    };
    const send = () => {
      if (transfer.cancelled || transfer.settled) { return; }
      const request = new XMLHttpRequest();
      transfer.request = request;
      request.open("PUT", url, true);
      request.upload.onprogress = (event) => {
        if (event.lengthComputable) {
          notify("OnUploadProgress", event.loaded, event.total);
        }
      };
      request.onload = () => {
        if (request.status >= 200 && request.status < 300) {
          finish();
        } else if (request.getResponseHeader("X-NetRatel-File-Transfer-Retryable") === "true") {
          retry(`Remote upload could not reconnect after ${maximumAttempts} attempts.`);
        } else {
          finish(new Error(`Upload failed with status ${request.status}.`));
        }
      };
      request.onerror = () => retry(`Upload failed before reaching the server after ${maximumAttempts} attempts.`);
      request.onabort = () => {
        if (transfer.cancelled) {
          finish(new Error("Upload was cancelled."));
        } else {
          retry(`Remote upload was interrupted after ${maximumAttempts} attempts.`);
        }
      };
      request.send(file);
    };

    window.netratelFileTransfers.uploads.set(id, transfer);
    send();
  }),
  cancel: (id) => {
    const transfer = window.netratelFileTransfers.uploads.get(id);
    if (!transfer) { return; }
    transfer.cancelled = true;
    window.clearTimeout(transfer.retryTimer);
    if (transfer.request) {
      transfer.request.abort();
    } else {
      transfer.finish(new Error("Upload was cancelled."));
    }
  }
};
