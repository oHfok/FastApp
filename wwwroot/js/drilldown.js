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
        const [detailRes, allTimeRes, allAppsRes, pinRes] = await Promise.all([
            fetch(`/api/app-details?appName=${encodeURIComponent(appName)}`),
            fetch(`/api/leaderboard?timeframe=all&date=${getLocalTodayStr()}`),
            fetch(`/api/all-apps`),
            fetch(`/api/settings/pin`)
        ]);
        const data = await detailRes.json();
        const allTimeBoard = await allTimeRes.json();
        const allApps = await allAppsRes.json();
        const pinData = await pinRes.json();
        const hasPin = pinData.hasPin ?? pinData.HasPin ?? false;

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
        const todayBonusMinutes = data.todayBonusMinutes ?? data.TodayBonusMinutes ?? 0;
        const effectiveLimit = dailyLimitMinutes + todayBonusMinutes;

        document.getElementById('dd-limit-input').value = dailyLimitMinutes > 0 ? dailyLimitMinutes : '';
        document.getElementById('dd-strict-toggle').checked = strictFocusMode;

        // If a PIN is set, changing the limit at all — including clearing it back
        // to "no limit" — requires it. Otherwise the PIN/extend system is
        // pointless: why ask for a time extension when you could just delete the
        // limit outright with zero friction.
        document.getElementById('dd-limit-pin-row').style.display = hasPin ? 'flex' : 'none';
        document.getElementById('dd-limit-pin').value = '';

        const barWrap = document.getElementById('dd-limit-bar-wrap');
        const extendWrap = document.getElementById('dd-extend-wrap');
        if (dailyLimitMinutes > 0) {
            const pct = Math.min(100, (todayMinutes / effectiveLimit) * 100);
            const overLimit = todayMinutes >= effectiveLimit;
            const bonusNote = todayBonusMinutes > 0 ? ` (includes +${formatTime(todayBonusMinutes)} extension)` : '';
            barWrap.style.display = 'block';
            document.getElementById('dd-limit-bar-fill').style.width = `${pct}%`;
            document.getElementById('dd-limit-bar-fill').style.background = overLimit ? 'var(--rose)' : 'var(--brass)';
            document.getElementById('dd-limit-bar-caption').textContent =
                `${formatTime(todayMinutes)} of ${formatTime(effectiveLimit)} today${overLimit ? ' — limit reached' : ''}${bonusNote}`;

            extendWrap.style.display = 'block';
            document.getElementById('dd-extend-controls').style.display = hasPin ? 'block' : 'none';
            document.getElementById('dd-extend-no-pin').style.display = hasPin ? 'none' : 'block';
            document.getElementById('dd-extend-btn').onclick = () => extendLimit(appName);
        } else {
            barWrap.style.display = 'none';
            extendWrap.style.display = 'none';
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
    const pin = document.getElementById('dd-limit-pin').value;

    const status = document.getElementById('dd-limit-status');
    const flash = (text, isError) => {
        status.textContent = text;
        status.style.color = isError ? 'var(--rose)' : 'var(--teal)';
        status.style.display = 'block';
        setTimeout(() => { status.style.display = 'none'; }, 2500);
    };

    try {
        const res = await fetch('/api/update-limit', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ appName, dailyLimitMinutes, strictFocusMode, pin })
        });
        const result = await res.json();
        if (!res.ok) {
            flash(result.error || 'Incorrect PIN.', true);
            return;
        }
        flash('Saved.', false);
        openDrilldown(appName); // refresh the bar/caption against the new limit
    } catch (err) {
        flash('Could not reach the app.', true);
    }
}

async function extendLimit(appName) {
    const extraMinutes = parseInt(document.getElementById('dd-extend-minutes').value, 10);
    const pinInput = document.getElementById('dd-extend-pin');
    const pin = pinInput.value;
    const status = document.getElementById('dd-extend-status');

    const flash = (text, isError) => {
        status.textContent = text;
        status.style.color = isError ? 'var(--rose)' : 'var(--teal)';
        status.style.display = 'block';
        setTimeout(() => { status.style.display = 'none'; }, 2500);
    };

    if (!pin) { flash('Enter your PIN first.', true); return; }

    try {
        const res = await fetch('/api/extend-limit', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ appName, pin, extraMinutes })
        });
        const result = await res.json();
        if (!res.ok) {
            flash(result.error || 'Incorrect PIN.', true);
            return;
        }
        pinInput.value = '';
        flash(`Extended by ${extraMinutes} min.`, false);
        openDrilldown(appName); // refresh the bar against the new effective limit
    } catch (err) {
        flash('Could not reach the app.', true);
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
