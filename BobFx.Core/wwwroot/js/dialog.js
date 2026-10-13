export function open(dialog) {
    dialog.addEventListener('cancel', event => { event.preventDefault(); dialog.querySelector('button')?.click(); }, { once: true });
    dialog.showModal();
}
export function close(dialog) { dialog.close(); }
