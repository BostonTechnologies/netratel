(() => {
  const contexts = new WeakMap();
  const documents = new Map();
  const pendingUploads = new Map();
  window.addEventListener('pagehide', () => {
    for (const owner of documents.values()) owner.dispose(); documents.clear();
    for (const [id, controller] of pendingUploads) { controller.abort(); window.netratelFileTransfers?.cancel(id); }
    pendingUploads.clear();
  });
  const layoutValid = value => value && typeof value === 'object' && value.grid && value.panels &&
    Object.keys(value.panels).every(id => ['tree', 'files', 'remote-preview', 'remote-details'].includes(id));
  const join = (parent, name) => `${parent.replace(/[\\/]$/, '')}${/^[A-Za-z]:/.test(parent) ? '\\' : '/'}${name}`;
  const uploadFile = async (file, tenant, agent, path, id, callbacks, replacement = null, version = null, owner = null) => {
    if (file.size > 16 * 1024 ** 3) throw new Error('Upload exceeds the 16 GiB transfer limit.');
    const preparation = new AbortController();
    pendingUploads.set(id, preparation);
    try {
      const response = await fetch('/file-browser/transfers/antiforgery', { credentials: 'same-origin', cache: 'no-store', signal: preparation.signal });
      if (!response.ok) throw new Error('Upload authorization is unavailable.');
      const { token } = await response.json();
      preparation.signal.throwIfAborted();
      if (owner && !owner.active) throw new Error('Upload cancelled before dispatch.');
      const target = replacement || join(path, file.name);
      const url = `/file-browser/transfers/upload-entry?tenantId=${tenant}&agentId=${encodeURIComponent(agent)}&path=${encodeURIComponent(target)}&replace=${!!replacement}&expectedVersion=${encodeURIComponent(version || '')}`;
      await window.netratelFileTransfers.upload({ files: [file] }, url, id, callbacks, { RequestVerificationToken: token });
    } finally { pendingUploads.delete(id); }
  };
  window.netratelExplorer = {
    async renderPdf(container, url, id) {
      if (typeof container === 'string') container = window.MudExPopoutEvents?.byId(container) || document.getElementById(container);
      documents.get(id)?.dispose();
      if (!container?.isConnected || !url) throw new Error('The PDF preview was closed.');
      let cancelled = false;
      documents.set(id, { dispose() { cancelled = true; documents.delete(id); } });
      const pdfjs = await import('/js/viewers/pdfjs-dist/6.5.438/build/pdf.min.mjs');
      if (cancelled || !container.isConnected) return;
      pdfjs.GlobalWorkerOptions.workerSrc = '/js/viewers/pdfjs-dist/6.5.438/build/pdf.worker.min.mjs';
      const assets = '/js/viewers/pdfjs-dist/6.5.438/';
      let destroyed = false, pdf = null, rendering = null, page = 1, scale = 1, revision = 0;
      const controls = document.createElement('div');
      const status = document.createElement('span');
      status.setAttribute('role', 'status');
      const canvas = document.createElement('canvas');
      canvas.setAttribute('aria-label', 'PDF page');
      canvas.style.maxWidth = '100%';
      const loading = pdfjs.getDocument({ url, withCredentials: true, disableAutoFetch: true, disableStream: true,
        rangeChunkSize: 65536, isEvalSupported: false, enableXfa: false, maxImageSize: 16000000, canvasMaxAreaInBytes: 64000000,
        useSystemFonts: false, cMapUrl: assets + 'cmaps/', cMapPacked: true,
        standardFontDataUrl: assets + 'standard_fonts/', wasmUrl: assets + 'wasm/' });
      const owner = {
        dispose() {
          if (destroyed) return;
          destroyed = true;
          clearTimeout(deadline);
          observer.disconnect();
          rendering?.cancel();
          loading.destroy().catch(error => console.debug('PDF cleanup:', error.name));
          canvas.width = canvas.height = 0;
          container.replaceChildren();
          documents.delete(id);
        }
      };
      const observer = new MutationObserver(() => { if (!container.isConnected) owner.dispose(); });
      let deadline = setTimeout(() => { owner.dispose(); container.textContent = 'PDF loading exceeded its 30-second budget. Use Download.'; }, 30000);
      observer.observe(container.ownerDocument.body, { childList: true, subtree: true });
      documents.set(id, owner);
      const render = async () => {
        if (destroyed || !pdf) return;
        const current = ++revision;
        clearTimeout(deadline);
        rendering?.cancel();
        const pageDeadline = setTimeout(() => {
          if (current !== revision) return;
          owner.dispose(); container.textContent = 'PDF page exceeded its 15-second work budget. Use Download.';
        }, 15000);
        deadline = pageDeadline;
        try {
          const selected = await pdf.getPage(page);
          if (destroyed || current !== revision) { clearTimeout(pageDeadline); return; }
          const viewport = selected.getViewport({ scale });
          if (viewport.width * viewport.height > 16000000 || Math.max(viewport.width, viewport.height) > 16000)
            throw new Error('PDF page exceeds the 16 million pixel rendering limit. Use Download.');
          canvas.width = Math.ceil(viewport.width); canvas.height = Math.ceil(viewport.height);
          rendering = selected.render({ canvas, canvasContext: canvas.getContext('2d'), viewport, annotationMode: 0 });
          await rendering.promise;
          clearTimeout(pageDeadline);
          if (!destroyed && current === revision) status.textContent = ` Page ${page} / ${pdf.numPages} · ${Math.round(scale * 100)}% `;
          selected.cleanup();
        } catch (error) {
          clearTimeout(pageDeadline);
          if (!destroyed && current === revision && error.name !== 'RenderingCancelledException') status.textContent = error.message;
        }
      };
      const button = (label, action) => {
        const element = document.createElement('button');
        element.type = 'button'; element.textContent = label;
        element.addEventListener('click', action);
        controls.append(element);
      };
      button('Previous page', () => { if (page > 1) { page--; void render(); } });
      button('Next page', () => { if (pdf && page < pdf.numPages) { page++; void render(); } });
      button('Zoom out', () => { scale = Math.max(0.25, scale - 0.25); void render(); });
      button('Zoom in', () => { scale = Math.min(3, scale + 0.25); void render(); });
      controls.append(status);
      container.replaceChildren(controls, canvas);
      status.textContent = 'Loading PDF…';
      try {
        pdf = await loading.promise;
        if (!destroyed) await render();
      } catch (error) {
        if (!destroyed) { owner.dispose(); container.textContent = 'PDF preview unavailable: ' + error.message; }
      }
    },
    async renderDocx(frame, stream, id) {
      if (typeof frame === 'string') frame = window.MudExPopoutEvents?.byId(frame) || document.getElementById(frame);
      documents.get(id)?.dispose();
      let cancelled = false;
      documents.set(id, { dispose() { cancelled = true; documents.delete(id); } });
      let bytes = await stream.arrayBuffer();
      if (cancelled || !frame?.isConnected) throw new Error('The document preview was closed.');
      const token = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(24))));
      const origin = location.origin;
      const script = source => `<script nonce="${token}" src="${origin}${source}"><\/script>`;
      // The opaque sandbox receives bounded bytes by postMessage. It can load
      // only these nonce-authorized local scripts, data/blob images and inline
      // styles; tenant content cannot access the app origin, fetch, submit, or run scripts.
      const markup = `<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'nonce-${token}'; style-src 'unsafe-inline'; img-src data: blob:; font-src data: blob:; connect-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'"><style>body{margin:0;background:#eee;font-family:system-ui}#content{min-height:100vh}</style>${script('/js/viewers/jszip/3.10.1/jszip.min.js')}${script('/js/viewers/docx-preview/0.3.3/docx-preview.min.js')}</head><body><div id="content"></div><script nonce="${token}">addEventListener('message',async event=>{if(event.data?.token!==${JSON.stringify(token)}||!event.data.bytes)return;try{document.getElementById('content').replaceChildren();await docx.renderAsync(event.data.bytes,document.getElementById('content'),null,{useBase64URL:true,renderAltChunks:false,renderComments:false});event.source.postMessage({token:${JSON.stringify(token)},state:'rendered'},'*')}catch(error){event.source.postMessage({token:${JSON.stringify(token)},state:'failed',message:error.message},'*')}})<\/script></body></html>`;
      let settled = false;
      let resolveReady, rejectReady;
      const ready = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
      const finish = (error) => { if (settled) return; settled = true; clearTimeout(timeout); error ? rejectReady(error) : resolveReady(); };
      const receive = event => {
        if (event.source !== frame.contentWindow || event.data?.token !== token) return;
        finish(event.data.state === 'rendered' ? null : new Error(event.data.message || 'Document could not be rendered.'));
      };
      const observer = new MutationObserver(() => { if (!frame.isConnected) owner.dispose(); });
      const owner = {
        dispose() {
          finish(new Error('Document preview ended.'));
          window.removeEventListener('message', receive);
          observer.disconnect();
          frame.onload = null;
          bytes = null;
          frame.srcdoc = '';
          documents.delete(id);
        }
      };
      const timeout = setTimeout(() => { finish(new Error('Document renderer exceeded its 15-second work budget.')); owner.dispose(); }, 15_000);
      documents.set(id, owner);
      window.addEventListener('message', receive);
      frame.onload = () => {
        if (bytes && frame.isConnected) frame.contentWindow.postMessage({ token, bytes: bytes.slice(0) }, '*');
      };
      observer.observe(frame.ownerDocument.body, { childList: true, subtree: true });
      frame.srcdoc = markup;
      await ready;
    },
    disposeDocument(id) { documents.get(id)?.dispose(); },
    async initializeMudEx() {
      // BlazorJS 2.3.0 packages only this fingerprinted module. Import it before
      // any MudEx component can ask the broken unfingerprinted loader for CSS.
      await import('/_content/BlazorJS/BlazorJS.nueudvf3ku.lib.module.js');
      await import('/_content/MudBlazor.Extensions/js/mudBlazorExtensions.all.min.js');
      if (!window.BlazorJS || !window.MudBlazorExtensions) throw new Error('Pinned MudEx runtime did not initialize.');
    },
    isNarrow() { return matchMedia('(max-width: 959px)').matches; },
    get(scope, name) {
      try {
        const raw = localStorage.getItem(`${scope}.${name}`);
        if (name !== 'layout') return raw;
        if (!raw || raw.length > 65536) return null;
        const stored = JSON.parse(raw);
        return stored.version === 1 && layoutValid(stored.layout) ? JSON.stringify(stored.layout) : null;
      } catch (error) { console.debug('Explorer preferences unavailable:', error.name); return null; }
    },
    set(scope, name, value) {
      if (name === 'layout') {
        if (value.length > 65536) throw new Error('Layout is too large. Reset it before saving.');
        const layout = JSON.parse(value);
        if (!layoutValid(layout)) throw new Error('Unsupported explorer layout.');
        value = JSON.stringify({ version: 1, layout });
      }
      try { localStorage.setItem(`${scope}.${name}`, value); }
      catch (error) { console.debug('Explorer preference could not be saved:', error.name); }
    },
    attach(root, callbacks) {
      if (contexts.has(root)) return;
      const context = { callbacks, uploads: new Set(), config: null, focus: null, active: true };
      context.media = matchMedia('(max-width: 959px)');
      context.resize = () => callbacks.invokeMethodAsync('OnViewportChanged', context.media.matches).catch(error => console.debug('Explorer resize owner ended:', error.name));
      context.media.addEventListener('change', context.resize);
      const command = event => {
        if (event.target.closest('input,textarea,[contenteditable=true]')) return;
        if (event.metaKey && !event.ctrlKey) Object.defineProperty(event, 'ctrlKey', { value: true });
      };
      const drag = event => {
        if (!Array.from(event.dataTransfer?.types || []).includes('Files')) return;
        event.preventDefault(); event.stopImmediatePropagation();
      };
      const drop = async event => {
        if (!Array.from(event.dataTransfer?.types || []).includes('Files')) return;
        drag(event);
        const config = context.config;
        if (!config?.canUpload) return;
        const files = Array.from(event.dataTransfer.files);
        if (!files.length || files.length > 128) return;
        try { if (!await callbacks.invokeMethodAsync('OnExternalUploadStarted') || !context.active) return; }
        catch (error) { console.debug('Upload owner ended before dispatch:', error.name); return; }
        let failure = null;
        try {
          for (const file of files) {
            if (!context.active) break;
            const id = crypto.randomUUID();
            context.uploads.add(id);
            try { await uploadFile(file, config.tenant, config.agent, config.path, id, callbacks, null, null, context); }
            finally { context.uploads.delete(id); }
          }
        } catch (error) { failure = error.message; }
        finally { if (context.active) await callbacks.invokeMethodAsync('OnExternalUploadCompleted', failure).catch(error => console.debug('Upload owner ended:', error.name)); }
      };
      context.handlers = { click: command, keydown: command, dragover: drag, drop };
      context.observer = new MutationObserver(() => { if (!root.isConnected) window.netratelExplorer.detach(root); });
      context.observer.observe(root.ownerDocument.body, { childList: true, subtree: true });
      for (const [name, handler] of Object.entries(context.handlers)) root.addEventListener(name, handler, true);
      contexts.set(root, context);
    },
    update(root, tenant, agent, path, canUpload) {
      const context = contexts.get(root);
      if (context) context.config = { tenant, agent, path, canUpload };
    },
    upload: async (input, tenant, agent, path, id, callbacks, replacement = null, version = null) => {
      const file = input?.files?.[0];
      if (!file) return;
      const owner = contexts.get(input.closest?.('[data-testid="full-explorer"]'));
      owner?.uploads.add(id);
      try { await uploadFile(file, tenant, agent, path, id, callbacks, replacement, version, owner); }
      finally { owner?.uploads.delete(id); input.value = ''; }
    },
    holdFocus(root) {
      const context = contexts.get(root);
      if (!context) return;
      const docs = window.MudExPopoutEvents?.documents() || [root.ownerDocument];
      const active = docs.find(doc => doc.hasFocus()) || root.ownerDocument;
      const elements = [...root.querySelectorAll('*'), ...docs.filter(doc => doc !== root.ownerDocument).flatMap(doc => Array.from(doc.querySelectorAll('*')))];
      context.focus = { element: active.activeElement, positions: elements.filter(element => element.scrollTop || element.scrollLeft).map(element => [element, element.scrollTop, element.scrollLeft]) };
    },
    focus(root) {
      const focus = contexts.get(root)?.focus;
      for (const [element, top, left] of focus?.positions || []) { element.scrollTop = top; element.scrollLeft = left; }
      const element = focus?.element?.isConnected ? focus.element : root.querySelector('[tabindex]');
      if (element?.ownerDocument !== root.ownerDocument) element?.ownerDocument.defaultView?.focus();
      element?.focus({ preventScroll: true });
    },
    cancelUploads(root) {
      for (const id of contexts.get(root)?.uploads || []) window.netratelExplorer.cancelUpload(id);
    },
    cancelUpload(id) { pendingUploads.get(id)?.abort(); window.netratelFileTransfers.cancel(id); },
    detach(root) {
      const context = contexts.get(root);
      if (!context) return;
      context.active = false;
      for (const [name, handler] of Object.entries(context.handlers)) root.removeEventListener(name, handler, true);
      context.observer.disconnect();
      context.media.removeEventListener('change', context.resize);
      for (const id of context.uploads) window.netratelExplorer.cancelUpload(id);
      contexts.delete(root);
    }
  };
})();
