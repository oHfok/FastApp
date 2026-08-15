/* ==========================================================
   TAB: ALL APPLICATIONS
   Full library, card grid, search.
   ========================================================== */

let allAppsData = [];
let allAppsSearch = '';

async function loadAllApps() {
    try {
        const res = await fetch('/api/all-apps');
        allAppsData = await res.json();
        renderAllApps();
    } catch (err) { console.error(err); }
}

function handleAllAppsSearch() {
    allAppsSearch = document.getElementById('allapps-search').value.toLowerCase();
    renderAllApps();
}

function renderAllApps() {
    const grid = document.getElementById('allapps-grid');
    const filtered = allAppsData
        .filter(a => (a.appName || a.AppName || '').toLowerCase().includes(allAppsSearch))
        .sort((a, b) => (a.appName || a.AppName).localeCompare(b.appName || b.AppName));

    if (filtered.length === 0) {
        grid.innerHTML = `<div class="empty-state">No applications found.</div>`;
        return;
    }

    grid.innerHTML = filtered.map(app => {
        const name = app.appName || app.AppName;
        const cat = app.category || app.Category || 'Other';
        const focus = app.totalFocus ?? app.TotalFocus ?? 0;
        const runtime = app.totalRuntime ?? app.TotalRuntime ?? 0;
        return `
            <div class="card allapps-card" onclick="openDrilldown('${name}')">
                <div class="allapps-card-head">
                    <div class="allapps-icon" style="color:${catColor(cat)}">${name.charAt(0).toUpperCase()}</div>
                    <div style="min-width:0;">
                        <div class="allapps-name">${name}</div>
                        <div class="allapps-cat cat-link" onclick="event.stopPropagation(); openCategoryDetail('${cat.replace(/'/g, "&#39;")}')">${cat}</div>
                    </div>
                </div>
                <div class="allapps-metrics">
                    <div><span>Focus</span><span class="v mono" style="color:var(--brass)">${formatTime(focus)}</span></div>
                    <div style="text-align:right;"><span>Running</span><span class="v mono">${formatTime(runtime)}</span></div>
                </div>
            </div>`;
    }).join('');
}

Dashboard.tabs.allapps = { onEnter: loadAllApps };
