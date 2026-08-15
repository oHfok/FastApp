/* ==========================================================
   APP DETAIL PAGE (drawer)
   Per spec: name, all-time Focus-time ranking, category + editor,
   most-used day, 30-day usage stat (X/30 days, %), hide toggle.
   ========================================================== */

async function openDrilldown(appName) {
    document.getElementById('dd-overlay').classList.add('open');
    document.getElementById('dd-drawer').classList.add('open');

    document.getElementById('dd-title').textContent = appName;
    document.getElementById('dd-icon').textContent = appName.charAt(0).toUpperCase();
    document.getElementById('dd-rank').textContent = 'Loading rank…';

    try {
        const [detailRes, allTimeRes, allAppsRes] = await Promise.all([
            fetch(`/api/app-details?appName=${encodeURIComponent(appName)}`),
            fetch(`/api/leaderboard?timeframe=all&date=${getLocalTodayStr()}`),
            fetch(`/api/all-apps`)
        ]);
        const data = await detailRes.json();
        const allTimeBoard = await allTimeRes.json();
        const allApps = await allAppsRes.json();

        if (data.error) { console.error(data.error); return; }

        // All-time ranking
        const sorted = [...allTimeBoard].sort((a, b) => b.focusedMinutes - a.focusedMinutes);
        const rankIdx = sorted.findIndex(a => a.appName.toLowerCase() === appName.toLowerCase());
        document.getElementById('dd-rank').textContent = rankIdx >= 0
            ? `#${rankIdx + 1} all-time by focus time`
            : 'Not ranked yet';

        // Category selector — /api/app-details doesn't return a category field, so
        // read the app's current category from /api/all-apps instead (it includes
        // hidden apps too, unlike the leaderboard, so this works even for hidden apps).
        const allAppsMatch = allApps.find(a => (a.appName || a.AppName || '').toLowerCase() === appName.toLowerCase());
        const currentCategory = (allAppsMatch && (allAppsMatch.category || allAppsMatch.Category)) || 'Other';

        const catSelect = document.getElementById('dd-category-select');
        let opts = allCategories.map(c => `<option value="${c}" ${currentCategory === c ? 'selected' : ''}>${c}</option>`).join('');
        if (currentCategory && !allCategories.includes(currentCategory)) opts += `<option value="${currentCategory}" selected>${currentCategory}</option>`;
        catSelect.innerHTML = opts;
        catSelect.onchange = () => updateCategory(appName, catSelect.value);

        // Most used day
        document.getElementById('dd-max-day').textContent = data.maxFocusDay || 'N/A';

        // 30-day behavior: "used X of last 30 days (Y%)"
        const daysActive = data.daysActive || 0;
        const consistency = data.consistency ?? Math.round((daysActive / 30) * 100);
        document.getElementById('dd-consistency').innerHTML =
            `Used <strong>${daysActive}</strong> of the last 30 days <span class="mono" style="color:var(--brass)">(${consistency}%)</span>`;
        document.getElementById('dd-pattern').textContent = data.usagePattern || '—';

        // Lifetime stats
        document.getElementById('dd-focus-all').textContent = formatHours(data.allTimeFocused || 0);
        document.getElementById('dd-running-all').textContent = formatHours(data.allTimeRunning || 0);
        document.getElementById('dd-afk-all').textContent = formatHours(data.allTimeAfk || 0);

        // Hide/unhide button
        const hideBtn = document.getElementById('dd-hide-btn');
        hideBtn.textContent = 'Hide from statistics';
        hideBtn.onclick = () => hideAppFromDetail(appName);

    } catch (err) {
        console.error('Failed to load app details', err);
    }
}

async function updateCategory(appName, category) {
    await fetch('/api/update-category', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ appName, category })
    });
    refreshActiveTab();
}

async function hideAppFromDetail(appName) {
    await fetch('/api/hide', { method: 'POST', body: appName });
    closeDrilldown();
    refreshActiveTab();
}

function refreshActiveTab() {
    const activeRail = document.querySelector('.rail-item.active');
    if (!activeRail) return;
    const mod = Dashboard.tabs[activeRail.dataset.view];
    if (mod && mod.onEnter) mod.onEnter();
}

function closeDrilldown() {
    document.getElementById('dd-overlay').classList.remove('open');
    document.getElementById('dd-drawer').classList.remove('open');
}
