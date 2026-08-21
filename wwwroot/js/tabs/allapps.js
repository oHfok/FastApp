/* ==========================================================
   TAB: ALL APPLICATIONS
   Full library, card grid, search.
   ========================================================== */

let allAppsData = [];
let allAppsSearch = '';
// 'time' | 'name'. Time first: alphabetical order has no relationship to
// relevance, so the library used to open on Applicationframehost and a 0m
// installer while the apps with hundreds of hours sat pages down.
let allAppsSort = 'time';
// Roughly half of tracked executables have never been focused at all —
// installers, updaters, shell hosts. Hidden by default, one click away.
let allAppsShowBackground = false;

async function loadAllApps() {
    const grid = document.getElementById('allapps-grid');
    if (isEmptyContainer(grid)) grid.innerHTML = loadingRowsHtml(6);
    try {
        allAppsData = await apiFetch('/api/all-apps', { signal: abortableSignal('allapps') });
        renderAllApps();
    } catch (err) { if (!isAbort(err)) console.error('All apps load failed', err); }
}

function handleAllAppsSearch() {
    allAppsSearch = document.getElementById('allapps-search').value.toLowerCase();
    renderAllApps();
}

function setAllAppsSort(mode, btnEl) {
    allAppsSort = mode;
    document.querySelectorAll('#allapps-sort button').forEach(b => b.classList.toggle('active', b === btnEl));
    renderAllApps();
}

function toggleAllAppsBackground(inputEl) {
    allAppsShowBackground = inputEl.checked;
    renderAllApps();
}

function renderAllApps() {
    const grid = document.getElementById('allapps-grid');
    const filtered = allAppsData
        .filter(a => (a.appName || '').toLowerCase().includes(allAppsSearch))
        .filter(a => allAppsShowBackground || (a.totalFocus || 0) >= 1)
        .sort((a, b) => allAppsSort === 'name'
            ? a.appName.localeCompare(b.appName)
            : (b.totalFocus || 0) - (a.totalFocus || 0));

    const hiddenCount = allAppsData.filter(a => (a.totalFocus || 0) < 1).length;
    const countEl = document.getElementById('allapps-count');
    if (countEl) {
        countEl.textContent = allAppsShowBackground || hiddenCount === 0
            ? `${filtered.length} app${filtered.length === 1 ? '' : 's'}`
            : `${filtered.length} app${filtered.length === 1 ? '' : 's'} · ${hiddenCount} never focused`;
    }

    if (filtered.length === 0) {
        grid.innerHTML = `<div class="empty-state">No applications found.</div>`;
        return;
    }

    grid.innerHTML = filtered.map(app => {
        const name = app.appName;
        const cat = app.category || 'Other';
        const focus = app.totalFocus ?? 0;
        const runtime = app.totalRuntime ?? 0;
        return `
            <div class="card allapps-card" data-open-app="${escapeHtml(name)}" role="button" tabindex="0">
                <div class="allapps-card-head">
                    <div class="allapps-icon" style="${avatarStyle(cat)}">${escapeHtml((name || '?').charAt(0).toUpperCase())}</div>
                    <div style="min-width:0;">
                        <div class="allapps-name" title="${escapeHtml(name)}">${escapeHtml(displayAppName(name))}</div>
                        <div class="allapps-cat cat-link" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0">${escapeHtml(cat)}</div>
                    </div>
                </div>
                <div class="allapps-metrics">
                    <div><span>Focus</span><span class="v mono" style="color:var(--brass)">${formatTime(focus)}</span></div>
                    <div style="text-align:right;"><span>Running</span><span class="v mono">${formatTime(runtime)}</span></div>
                </div>
            </div>`;
    }).join('');
}

Dashboard.tabs.allapps = { onEnter: loadAllApps, refresh: loadAllApps };
