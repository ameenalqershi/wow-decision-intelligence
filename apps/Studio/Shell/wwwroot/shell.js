// Pointer capture and DOM focus are presentation-only host interop.
// The authoritative ratio is committed in the C# session.
const registrations = new WeakMap();
export function attach(split, separator, receiver) {
    detach(split);
    const controller = new AbortController(), signal = controller.signal;
    let drag = null;
    const clamp = value => Math.max(.25, Math.min(.75, value));
    const share = x => {
        const bounds = split.getBoundingClientRect();
        if (bounds.width <= 0) return null;
        const rtl = getComputedStyle(split).direction === 'rtl';
        const atStart = split.dataset.dock === 'start';
        const logical = rtl ? bounds.right - x : x - bounds.left;
        return clamp(atStart ? 1 - logical / bounds.width : logical / bounds.width);
    };
    const show = value => {
        split.style.setProperty('--shell-primary', value + 'fr');
        split.style.setProperty('--shell-secondary', (1-value) + 'fr');
    };
    const reset = () => show(Number(split.dataset.share));
    separator.addEventListener('pointerdown', e => {
        if (e.button !== 0 || split.dataset.inspectorVisible !== 'true' || split.closest('.wow-shell').dataset.busy === 'true') return;
        const value = share(e.clientX);
        if(value === null)return;
        drag = {id:e.pointerId, value};
        separator.setPointerCapture(e.pointerId);
        separator.focus();
        e.preventDefault();
    }, {signal});
    separator.addEventListener('pointermove', e => {
        if (drag?.id !== e.pointerId) return;
        const value = share(e.clientX);
        if(value!==null){drag.value=value;show(value);}
    }, {signal});
    separator.addEventListener('pointerup', async e => {
        if (drag?.id !== e.pointerId) return;
        const value=drag.value; drag=null;
        try { await receiver.invokeMethodAsync('CommitPointerResize',value); }
        catch (error) { console.warn('Shell resize was not committed', error); }
        finally { reset(); }
    }, {signal});
    const cancel=e=>{if(drag?.id===e.pointerId){drag=null;reset();}};
    separator.addEventListener('pointercancel',cancel,{signal});
    separator.addEventListener('lostpointercapture',cancel,{signal});
    // Prevent scrolling only for keys actually handled by the component.
    split.closest('.wow-shell').addEventListener('keydown',e=>{
        if((e.target.matches('[role=tab]')&&['Home','End','ArrowLeft','ArrowRight','Delete'].includes(e.key))||
           (e.target===separator&&['Home','End','ArrowLeft','ArrowRight'].includes(e.key))) e.preventDefault();
    },{signal});
    registrations.set(split,controller);
}
export function detach(split){registrations.get(split)?.abort();registrations.delete(split);}
export function focusTab(root,key){
    requestAnimationFrame(()=>{
        const button=Array.from(root.querySelectorAll('[data-tab-key]')).find(e=>e.dataset.tabKey===key);
        (button??root.querySelector('[data-new-tab]'))?.focus();
    });
}
