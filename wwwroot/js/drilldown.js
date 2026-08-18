/* ==========================================================
   APP DETAIL PAGE (drawer)
   Per spec: name, all-time Focus-time ranking, category + editor,
   most-used day, 30-day usage stat (X/30 days, %), hide toggle.
   ========================================================== */

function setDrilldownTab(tab, btnEl) {
    document.querySelectorAll('#dd-tab-toggle button').forEach(b => b.classList.toggle('active', b === btnEl));
    document.getElementById('dd-tab-panel-overview').style.display = tab === 'overview' ? 'block' : 'none';
    document.getElementById('dd-tab-panel-limits').style.display = tab === 'limits' ? 'block' : 'none';
}

// Period Comparison's expand-to-chart, keyed by period ('today'/'week'/'month'/
// 'year'). Torn down and rebuilt fresh on every openDrilldown() call — the
// canvases they're attached to get destroyed and recreated too (innerHTML
// rebuild below), so a stale Chart.js instance would otherwise be left
// pointing at a detached canvas.
let compareChartInstances = {};

// Usage Trend's single chart + which app/granularity it's currently showing.
// The granularity toggle is static HTML (not rebuilt per app like the compare
// rows), so it needs somewhere to read "which app is this drawer even open
// for" from, rather than a closure.
let trendChartInstance = null;
let currentDrilldownAppName = null;

async function openDrilldown(appName) {
    document.getElementById('dd-overlay').classList.add('open');
    document.getElementById('dd-drawer').classList.add('open');
    // Always land back on Overview — reopening a different app while still on
    // Limits from a previous look would be a surprising place to land.
    setDrilldownTab('overview', document.querySelector('#dd-tab-toggle button[data-tab="overview"]'));

    Object.values(compareChartInstances).forEach(chart => chart.destroy());
    compareChartInstances = {};

    currentDrilldownAppName = appName;
    // Reset to the default granularity every time a (possibly different) app
    // opens, rather than staying on whatever the previous app was left on.
    document.querySelectorAll('#dd-trend-toggle button').forEach(b => b.classList.toggle('active', b.dataset.granularity === 'day'));
    loadUsageTrend(appName, 'day');

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

        // First/last opened — from DailyLogs' MIN/MAX Date for this app, which
        // (unlike SessionLogs) is never pruned by retention, so this covers the
        // app's whole tracked history rather than just the retention window.
        document.getElementById('dd-first-seen').textContent = data.firstSeen || 'N/A';
        document.getElementById('dd-last-seen').textContent = data.lastSeen || 'N/A';

        // Streaks — consecutive days with any tracked time for this app.
        const longestStreak = data.longestStreak || 0;
        const currentStreak = data.currentStreak || 0;
        const dayWord = (n) => `${n} day${n === 1 ? '' : 's'}`;
        document.getElementById('dd-longest-streak').textContent = dayWord(longestStreak);
        document.getElementById('dd-longest-streak-range').textContent = longestStreak > 0 ? (data.longestStreakRange || '') : '';
        document.getElementById('dd-current-streak').textContent = dayWord(currentStreak);
        document.getElementById('dd-current-streak-start').textContent = currentStreak > 0 ? `Since ${data.currentStreakStart || '—'}` : '';

        // The run happening right now IS the record — same brass-glow treatment
        // Overview's hero Focus card uses, plus a badge, so it actually reads as
        // "this is the number that matters" instead of just another stat.
        const streakTile = document.getElementById('dd-current-streak-tile');
        const isPersonalBest = data.isCurrentStreakBest ?? (currentStreak > 0 && currentStreak >= longestStreak);
        streakTile.classList.toggle('is-personal-best', isPersonalBest);
        let bestBadge = document.getElementById('dd-current-streak-badge');
        if (isPersonalBest) {
            if (!bestBadge) {
                bestBadge = document.createElement('div');
                bestBadge.id = 'dd-current-streak-badge';
                bestBadge.className = 'dd-personal-best-badge';
                bestBadge.textContent = 'Personal Best';
                streakTile.appendChild(bestBadge);
            }
        } else if (bestBadge) {
            bestBadge.remove();
        }

        // Period comparison — total (not average) focused hours, this period vs
        // the previous one, at each granularity. trendPill() handles the "no
        // prior data" / "NEW" / flat cases the same way Overview's hero block does.
        // Each row expands (click) into a line chart at the matching granularity —
        // see toggleCompareChart().
        const periodRows = [
            { key: 'today', label: 'Today', cur: data.todayFocusHours, prev: data.yesterdayFocusHours, prevLabel: 'yesterday' },
            { key: 'week', label: 'This Week', cur: data.thisWeekFocusHours, prev: data.lastWeekFocusHours, prevLabel: 'last week' },
            { key: 'month', label: 'This Month', cur: data.thisMonthFocusHours, prev: data.lastMonthFocusHours, prevLabel: 'last month' },
            { key: 'year', label: 'This Year', cur: data.thisYearFocusHours, prev: data.lastYearFocusHours, prevLabel: 'last year' }
        ];
        document.getElementById('dd-period-compare').innerHTML = periodRows.map(r => `
            <div>
                <div class="dd-compare-row" data-period="${r.key}">
                    <span class="dd-compare-label">${r.label}</span>
                    <span class="dd-compare-value">${formatHours(r.cur || 0)}</span>
                    ${trendPill(r.cur || 0, r.prev || 0, r.prevLabel)}
                    <span class="dd-compare-chevron">▾</span>
                </div>
                <div class="dd-compare-chart-wrap" id="dd-compare-chart-${r.key}" style="display:none;">
                    <canvas id="dd-compare-canvas-${r.key}"></canvas>
                </div>
            </div>`).join('');
        // Bound to the closure's appName rather than embedded in the HTML/onclick
        // string — app names aren't fully trustworthy either, same reasoning as
        // the Timeline ribbon's session tooltips earlier this session.
        document.querySelectorAll('#dd-period-compare .dd-compare-row').forEach(row => {
            row.onclick = () => toggleCompareChart(row.dataset.period, appName, row);
        });

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

// Expands/collapses a Period Comparison row into a line chart — current vs
// previous period, at whatever granularity that period's endpoint response
// uses (hourly for Today, daily for Week/Month, monthly for Year). Fetched
// lazily on first open, not on every drilldown load; cached per period after
// that so re-toggling the same row doesn't re-fetch.
async function toggleCompareChart(period, appName, rowEl) {
    const wrap = document.getElementById(`dd-compare-chart-${period}`);
    const isOpen = wrap.style.display !== 'none';
    if (isOpen) {
        wrap.style.display = 'none';
        rowEl.classList.remove('is-open');
        return;
    }
    wrap.style.display = 'block';
    rowEl.classList.add('is-open');
    if (compareChartInstances[period]) return; // already rendered — just toggling visibility

    try {
        const res = await fetch(`/api/app-period-breakdown?appName=${encodeURIComponent(appName)}&period=${period}`);
        const data = await res.json();
        const labels = data.labels ?? data.Labels ?? [];
        const current = data.current ?? data.Current ?? [];
        const previous = data.previous ?? data.Previous ?? [];

        const ctx = document.getElementById(`dd-compare-canvas-${period}`);
        if (!ctx || !window.Chart) return;
        const theme = getChartTheme(); // read fresh — themes recolor charts by re-rendering, not CSS alone

        compareChartInstances[period] = new Chart(ctx, {
            type: 'line',
            data: {
                labels,
                datasets: [
                    { label: 'This period', data: current, borderColor: theme.brass, backgroundColor: 'transparent', tension: 0.3, pointRadius: 2, borderWidth: 2 },
                    { label: 'Previous period', data: previous, borderColor: theme.teal, backgroundColor: 'transparent', tension: 0.3, pointRadius: 2, borderWidth: 2, borderDash: [4, 3] }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: { grid: { display: false }, ticks: { color: theme.tick, font: { family: theme.fontMono, size: 10 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 8 } },
                    y: { grid: { color: theme.grid }, ticks: { color: theme.tick, font: { size: 10 }, callback: (v) => formatTime(v) } }
                },
                plugins: {
                    legend: { labels: { color: theme.tick, font: { family: theme.fontBody, size: 10 }, boxWidth: 12, padding: 8 } },
                    tooltip: {
                        backgroundColor: theme.tooltipBg, titleColor: theme.tooltipTitle, bodyColor: theme.tooltipBody,
                        callbacks: { label: (c) => `${c.dataset.label}: ${formatTime(c.raw || 0)}` }
                    }
                }
            }
        });
    } catch (err) {
        console.error('Failed to load period breakdown', err);
        wrap.innerHTML = `<div class="empty-state" style="border:none;background:none;padding:10px 0;">Couldn't load comparison.</div>`;
    }
}

function setTrendGranularity(granularity, btnEl) {
    document.querySelectorAll('#dd-trend-toggle button').forEach(b => b.classList.toggle('active', b === btnEl));
    if (currentDrilldownAppName) loadUsageTrend(currentDrilldownAppName, granularity);
}

// Usage Trend — last 12 buckets, no comparison, just the plain history.
// Rendered both as a bar chart and as a compact numeric row underneath, per
// spec ("presented numerically and visually").
async function loadUsageTrend(appName, granularity) {
    try {
        const res = await fetch(`/api/app-usage-trend?appName=${encodeURIComponent(appName)}&granularity=${granularity}`);
        const data = await res.json();
        const labels = data.labels ?? data.Labels ?? [];
        const values = data.values ?? data.Values ?? [];

        document.getElementById('dd-trend-numbers').innerHTML = labels.map((l, i) => `
            <div class="dd-trend-cell">
                <div class="dd-trend-cell-label">${l}</div>
                <div class="dd-trend-cell-value">${formatTime(values[i] || 0)}</div>
            </div>`).join('');

        const ctx = document.getElementById('dd-trend-canvas');
        if (!ctx || !window.Chart) return;
        const theme = getChartTheme(); // read fresh — themes recolor charts by re-rendering, not CSS alone

        if (trendChartInstance) trendChartInstance.destroy();
        trendChartInstance = new Chart(ctx, {
            type: 'bar',
            data: { labels, datasets: [{ label: 'Focus', data: values, backgroundColor: theme.brass, borderRadius: 3 }] },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: { grid: { display: false }, ticks: { color: theme.tick, font: { family: theme.fontMono, size: 10 }, maxRotation: 0, autoSkip: true, maxTicksLimit: 12 } },
                    y: { grid: { color: theme.grid }, ticks: { color: theme.tick, font: { size: 10 }, callback: (v) => formatTime(v) } }
                },
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: theme.tooltipBg, titleColor: theme.tooltipTitle, bodyColor: theme.tooltipBody,
                        callbacks: { label: (c) => formatTime(c.raw || 0) }
                    }
                }
            }
        });
    } catch (err) {
        console.error('Failed to load usage trend', err);
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
