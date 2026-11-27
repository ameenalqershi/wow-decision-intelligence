// DOM inspection only, for the native host qualification fixture.
// A harmless negative control in the trusted fixture. No privileged IPC
// is exercised, and the fetch targets a local discard port, never an
// external service. CSP violation is required, not just a failed fetch.
const securityProbe = (async () => {
    const violations = new Set();
    const observe = e => violations.add(e.effectiveDirective);
    document.addEventListener('securitypolicyviolation', observe);
    try {
        const script = document.createElement('script');
        script.textContent = "document.documentElement.dataset.inlineProbeRan='yes'";
        document.head.append(script);
        script.remove();
        let fetchRejected = false;
        try { await fetch('http://127.0.0.1:9/wowdi-csp-probe'); }
        catch { fetchRejected = true; }
        // A violation event is queued asynchronously by the renderer.
        // Bounded wait observes that event; no policy or acceptance bypass.
        const deadline = performance.now() + 2000;
        while (performance.now() < deadline &&
               !([...violations].some(x => x === 'script-src' || x === 'script-src-elem') && violations.has('connect-src')))
            await new Promise(resolve => setTimeout(resolve, 20));
        return {
            inlineBlocked: document.documentElement.dataset.inlineProbeRan !== 'yes' &&
                [...violations].some(x => x === 'script-src' || x === 'script-src-elem'),
            connectBlocked: fetchRejected && violations.has('connect-src')
        };
    } finally { document.removeEventListener('securitypolicyviolation', observe); }
})();
window.wowdiHostProbe = {
    security: () => securityProbe,
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
