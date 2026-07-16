// LiteCircuit global helpers
window.lc = {
    toggleTheme() {
        const root = document.documentElement;
        const next = root.dataset.theme === 'dark' ? 'light' : 'dark';
        root.dataset.theme = next;
        localStorage.setItem('lc-theme', next);
    },
    scrollToBottom(id) {
        const el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
    }
};
