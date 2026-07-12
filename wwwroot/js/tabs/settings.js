/* ==========================================
   TAB: Settings
   ========================================== */

async function unhideApp(appName) { await fetch('/api/unhide', { method: 'POST', body: appName }); loadHiddenApps(); }

async function loadHiddenApps() {
    const res = await fetch('/api/hidden-apps'); const apps = await res.json();
    const list = document.getElementById('hidden-apps-list');
    if (apps.length === 0) list.innerHTML = '<div class="text-slate-500 italic text-sm font-semibold bg-slate-800/30 p-4 rounded-xl text-center border border-slate-700/50">No apps hidden.</div>';
    else list.innerHTML = apps.map(app => `<div class="flex items-center justify-between bg-slate-800/40 border border-slate-700/50 p-3 rounded-xl shadow-sm"><span class="font-bold text-slate-300 tracking-wide">${app}</span><button onclick="unhideApp('${app}')" class="text-xs font-bold bg-blue-500/20 text-blue-400 px-3 py-1.5 rounded-lg hover:bg-blue-500/40 transition-colors border border-blue-500/30">Unhide</button></div>`).join('');
}

async function loadSettings() { const res = await fetch('/api/settings'); const data = await res.json(); document.getElementById('retention-select').value = data.retentionDays; }

async function saveRetention() {
    const val = document.getElementById('retention-select').value;
    await fetch('/api/settings/retention', { method: 'POST', body: val });
    const status = document.getElementById('retention-status');
    status.classList.remove('hidden');
    setTimeout(() => status.classList.add('hidden'), 3000);
}

// Registers this tab's entry point with the tab system (see app.js)
Dashboard.tabs.settings = {
    onEnter: () => {
        loadHiddenApps();
        loadSettings();
    }
};
