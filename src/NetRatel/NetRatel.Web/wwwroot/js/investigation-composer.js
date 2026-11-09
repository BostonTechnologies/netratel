export function read(id) {
    try { return JSON.parse(sessionStorage.getItem(`netratel-investigation-composer:${id}`) || 'null'); }
    catch { return null; }
}
export function save(id, text, pending) {
    sessionStorage.setItem(`netratel-investigation-composer:${id}`, JSON.stringify({ text, pending }));
}
export function clear(id) { sessionStorage.removeItem(`netratel-investigation-composer:${id}`); }
