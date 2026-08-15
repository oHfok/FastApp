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

        // Daily limit + Strict Focus Mode — actually enforced by the WPF tracker now;
        // editing here sends a live message to the running app, same as category edits.
        const dailyLimitMinutes = data.dailyLimitMinutes ?? data.DailyLimitMinutes ?? 0;
        const strictFocusMode = data.strictFocusMode ?? data.StrictFocusMode ?? false;
        const todayMinutes = data.todayMinutes ?? data.TodayMinutes ?? 0;

        document.getElementById('dd-limit-input').value = dailyLimitMinutes > 0 ? dailyLimitMinutes : '';
        document.getElementById('dd-strict-toggle').checked = strictFocusMode;

        const barWrap = document.getElementById('dd-limit-bar-wrap');
        if (dailyLimitMinutes > 0) {
            const pct = Math.min(100, (todayMinutes / dailyLimitMinutes) * 100);
            const overLimit = todayMinutes >= dailyLimitMinutes;
            barWrap.style.display = 'block';
            document.getElementById('dd-limit-bar-fill').style.width = `${pct}%`;
            document.getElementById('dd-limit-bar-fill').style.background = overLimit ? 'var(--rose)' : 'var(--brass)';
            document.getElementById('dd-limit-bar-caption').textContent =
                `${formatTime(todayMinutes)} of ${formatTime(dailyLimitMinutes)} today${overLimit ? ' — limit reached' : ''}`;
        } else {
            barWrap.style.display = 'none';
        }

        document.getElementById('dd-limit-save-btn').onclick = () => saveLimitSetting(appName);

        // Hide/unhide button
        const hideBtn = document.getElementById('dd-hide-btn');
        hideBtn.textContent = 'Hide from statistics';
        hideBtn.onclick = () => hideAppFromDetail(appName);

    } catch (err) {
        console.error('Failed to load app details', err);
    }
}

async function saveLimitSetting(appName) {
    const rawValue = document.getElementById('dd-limit-input').value.trim();
    const dailyLimitMinutes = rawValue === '' ? 0 : Math.max(0, parseInt(rawValue, 10) || 0);
    const strictFocusMode = document.getElementById('dd-strict-toggle').checked;

    await fetch('/api/update-limit', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ appName, dailyLimitMinutes, strictFocusMode })
    });

    const status = document.getElementById('dd-limit-status');
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);

    openDrilldown(appName); // refresh the bar/caption against the new limit
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
