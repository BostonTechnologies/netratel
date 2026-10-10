export function focusInvalidField(editor) {
    const invalid = editor?.querySelector('[aria-invalid="true"]');
    if (!invalid) return;
    const input = invalid.matches('input,select,textarea') ? invalid : invalid.querySelector('input,select,textarea');
    input?.focus({ preventScroll: true });
    invalid.scrollIntoView({ block: 'center', inline: 'nearest' });
}
