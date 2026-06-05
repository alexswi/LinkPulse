// Collocated dashboard module (loaded on demand by LinkPulseDashboard, §10). Its sole job is copying a
// ClientId to the clipboard for the "copyable ClientId" cell. Kept tiny and dependency-free; a failure
// is non-fatal because the full id is also exposed in the cell's title attribute for manual copy.

export function copyText(text) {
    if (navigator && navigator.clipboard && navigator.clipboard.writeText) {
        // Returns a promise; the caller awaits it and swallows any rejection (clipboard blocked, etc.).
        return navigator.clipboard.writeText(text);
    }

    return Promise.resolve();
}
