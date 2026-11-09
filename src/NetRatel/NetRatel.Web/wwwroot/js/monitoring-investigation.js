// Reserve synchronously in the native click event, before Blazor's async handler.
// Neither attachment, rendering nor hover creates a tab or sends a request.
const launches = new Map();
export function attach(button, context) {
    const storageKey = `netratel-monitoring-investigation:${context}`;
    const clicked = () => {
        let launch = launches.get(context);
        if (!launch) {
            let requestId;
            try { requestId = sessionStorage.getItem(storageKey); } catch { }
            if (!/^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(requestId || '')) requestId = crypto.randomUUID();
            try { sessionStorage.setItem(storageKey, requestId); } catch { }
            launch = { requestId, tab: null, url: null }; launches.set(context, launch);
        }
        if (launch.url) { window.open(launch.url, '_blank', 'noopener'); return; }
        if (!launch.tab || launch.tab.closed) {
            launch.tab = window.open('', '_blank');
            if (launch.tab) {
                launch.tab.opener = null;
                launch.tab.document.title = 'Preparing investigation';
                launch.tab.document.body.textContent = 'Preparing your local investigation. No message has been sent to Netclaw.';
            }
        }
    };
    button.addEventListener('click', clicked);
    return { dispose() { button.removeEventListener('click', clicked); } };
}
export function reservation(context) {
    const launch = launches.get(context);
    if (!launch) throw new Error('Investigation requires an explicit operator click.');
    return launch.requestId;
}
export function complete(context, url) {
    const launch = launches.get(context);
    if (!launch || !/^\/ai\/ops\?tenantId=\d+&investigation=[a-f0-9-]+$/i.test(url)) return false;
    launch.url = url;
    if (launch.tab && !launch.tab.closed) { launch.tab.location.replace(url); launch.tab = null; return true; }
    return false;
}
export function fail(context) {
    const launch = launches.get(context);
    if (launch?.tab && !launch.tab.closed) launch.tab.close();
    if (launch) launch.tab = null;
}
