const transcripts = new WeakMap();
const composers = new WeakSet();

export function followOutput(element, force) {
    if (!element) return;
    let state = transcripts.get(element);
    if (!state) {
        state = { following: true };
        transcripts.set(element, state);
        element.addEventListener("scroll", () => {
            state.following = element.scrollHeight - element.scrollTop - element.clientHeight < 96;
        }, { passive: true });
    }
    if (force || state.following) {
        element.scrollTop = element.scrollHeight;
        state.following = true;
    }
}

export function prepareComposer(element) {
    if (!element || composers.has(element)) return;
    composers.add(element);
    element.addEventListener("keydown", event => {
        if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) event.preventDefault();
    });
}
