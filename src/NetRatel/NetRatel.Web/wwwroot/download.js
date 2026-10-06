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
    const transfer = { request: null, cancelled: false, settled: false, finish: null };
    const interrupted = "Upload was interrupted. Its outcome is unknown. Verify the destination before retrying.";
    const notify = (method, ...args) => {
      if (callbacks) { void callbacks.invokeMethodAsync(method, ...args).catch(() => {}); }
    };
    const finish = (error) => {
      if (transfer.settled) { return; }
      transfer.settled = true;
      window.netratelFileTransfers.uploads.delete(id);
      if (error) { reject(error); } else { resolve(); }
    };
    transfer.finish = finish;
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
        } else {
          finish(new Error(`Upload failed with status ${request.status}. ${interrupted}`));
        }
      };
      // A network error or abort cannot establish whether the remote write
      // already completed. Reissuing the PUT would create a second mutation.
      request.onerror = () => finish(new Error(interrupted));
      request.onabort = () => {
        if (transfer.cancelled) {
          finish(new Error("Upload was cancelled."));
        } else {
          finish(new Error(interrupted));
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
    if (transfer.request) {
      transfer.request.abort();
    } else {
      transfer.finish(new Error("Upload was cancelled."));
    }
  }
};
