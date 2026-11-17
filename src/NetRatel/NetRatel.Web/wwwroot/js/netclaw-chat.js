import { saveText } from "./chat-drafts.js";

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
    element.addEventListener("input", () => saveText(element.dataset.chatDraftScope, element.value));
    element.addEventListener("keydown", event => {
        if (event.key !== "Enter" || event.ctrlKey || event.metaKey) return;
        if (event.isComposing || event.keyCode === 229) { event.stopPropagation(); return; }
        if (event.altKey && element.dataset.chatDraftScope) {
            event.preventDefault();
            event.stopPropagation();
            if (event.repeat || element.disabled || element.readOnly ||
                element.value.length - (element.selectionEnd - element.selectionStart) >= element.maxLength) return;
            element.setRangeText("\n", element.selectionStart, element.selectionEnd, "end");
            element.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertLineBreak", data: "\n" }));
            return;
        }
        if (event.shiftKey || event.altKey) return;
        // Blazor delegates key events at the document. Do not submit an IME
        // composition or a held key, and never insert a newline for Send.
        event.preventDefault();
        if (event.repeat) event.stopPropagation();
    });
}
