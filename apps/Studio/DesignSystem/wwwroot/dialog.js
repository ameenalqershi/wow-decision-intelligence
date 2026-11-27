// Native modal handles focus containment and Escape. Explicitly restore the opener.
export function show(dialog) {
    if (dialog.open) return;
    const opener = document.activeElement;
    dialog.addEventListener('close', () => {
        if (opener instanceof HTMLElement && opener.isConnected) opener.focus();
    }, { once: true });
    dialog.showModal();
}
export function close(dialog) {
    if (dialog.open) dialog.close();
}
