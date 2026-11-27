// DOM inspection only, for the native host qualification fixture.
window.wowdiHostProbe = {
    read: () => {
        const probe = document.getElementById('probe');
        return {
            direction: getComputedStyle(probe).direction,
            focusedId: document.activeElement?.id ?? '',
            documentUrl: document.location.href,
            hasFocus: document.hasFocus()
        };
    }
};
