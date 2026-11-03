/* ==========================================================
   TAB: LEADERBOARD
   Default scope: All Time. Spotify-style day-over-day rank
   deltas, Olympic-medal styling for the top 3.
   ========================================================== */

let lbData = [];
let lbSearch = '';
let lbRankBy = 'focus';
let lbMode = 'apps';
const lbOpenCats = new Set();   // expanded category rows, kept across polls

function setLbMode(mode, btnEl) {
    document.querySelectorAll('#lb-mode button').forEach(b => b.classList.toggle('active', b === btnEl));
    document.getElementById('lb-mode').dataset.value = mode;
    lbMode = mode;
    document.getElementById('lb-search').placeholder = mode === 'categories' ? 'Search categories or apps…' : 'Search apps…';
    updateLbSubtitle();
    renderFullLeaderboard();
}

function setLbTimeframe(tf, btnEl) {
    document.querySelectorAll('#lb-scope button').forEach(b => b.classList.toggle('active', b === btnEl));
    document.getElementById('lb-scope').dataset.value = tf;
    updateLbSubtitle();
    fetchFullLeaderboard();
}

function setLbRankBy(mode, btnEl) {
    document.querySelectorAll('#lb-rankby button').forEach(b => b.classList.toggle('active', b === btnEl));
    document.getElementById('lb-rankby').dataset.value = mode;
    lbRankBy = mode;
    updateLbSubtitle();
    renderFullLeaderboard();
}

// The movement half of the sentence only holds on scopes that have a previous
// period to compare against, so it is dropped rather than left as a promise the
// view cannot keep.
function updateLbSubtitle() {
    const sub = document.getElementById('lb-view-sub');
    if (!sub) return;
    const tf = document.getElementById('lb-scope').dataset.value || 'all';
    const basis = (lbMode === 'categories' ? 'Categories ranked by ' : 'Ranked by ')
                + (lbRankBy === 'uptime' ? 'uptime.' : 'focus time.');
    sub.textContent = (tf === 'all' || tf === 'year')
        ? basis
        : `${basis} Arrows show movement since the previous ${tf}.`;
}

function handleLbSearch() {
    lbSearch = document.getElementById('lb-search').value.toLowerCase();
    renderFullLeaderboard();
}

async function fetchFullLeaderboard() {
    const tf = document.getElementById('lb-scope').dataset.value || 'all';
    const container = document.getElementById('lb-list');
    if (isEmptyContainer(container)) container.innerHTML = loadingRowsHtml(6);
    try {
        lbData = await apiFetch(`/api/leaderboard?timeframe=${tf}&date=${getLocalTodayStr()}`,
                                { signal: abortableSignal('leaderboard') });
        renderFullLeaderboard();
    } catch (err) { if (!isAbort(err)) console.error('Leaderboard load failed', err); }
}

function renderFullLeaderboard() {
    const container = document.getElementById('lb-list');
    if (!lbData || lbData.length === 0) {
        container.innerHTML = `<div class="empty-state">No data yet. Time to open some apps.</div>`;
        return;
    }

    const primaryMins = a => lbRankBy === 'uptime' ? (a.totalMinutes || 0) : (a.focusedMinutes || 0);
    const prevPrimaryMins = a => lbRankBy === 'uptime' ? (a.prevTotalMinutes || 0) : (a.prevFocusedMinutes || 0);
    if (lbMode === 'categories') { renderCategoryLeaderboard(container, primaryMins, prevPrimaryMins); return; }

    // Rank against the FULL dataset, then filter for display. Both rankings used
    // to be computed from the search-filtered list, which meant a search silently
    // changed what the numbers meant: an app could be handed rank #1 (and the
    // gold medal that goes with it) purely because the other apps were typed out
    // of view, and the "movement since yesterday" arrows recomputed against a
    // different population every keystroke. Searching should change which rows
    // you see, not what they claim.
    const rankedNow = [...lbData].sort((a, b) => primaryMins(b) - primaryMins(a));
    const rankOf = {};
    rankedNow.forEach((a, i) => { rankOf[a.appName] = i + 1; });

    const rankedPrev = [...lbData].sort((a, b) => prevPrimaryMins(b) - prevPrimaryMins(a));
    const prevRankOf = {};
    rankedPrev.forEach((a, i) => { prevRankOf[a.appName] = i + 1; });

    const sorted = rankedNow.filter(a => a.appName.toLowerCase().includes(lbSearch));

    if (sorted.length === 0) {
        container.innerHTML = `<div class="empty-state">No apps match your search.</div>`;
        return;
    }

    // On All Time and Year there is no previous period to compare against, so the
    // backend returns zero for every app and every single row rendered "NEW" —
    // a badge on 100% of rows is worse than no badge, because it occupies a
    // column and teaches people to ignore that area. Hide it on those scopes.
    const tf = document.getElementById('lb-scope').dataset.value || 'all';
    const showMovement = tf !== 'all' && tf !== 'year';

    const primaryLabel = lbRankBy === 'uptime' ? 'Uptime' : 'Focused';
    const primaryTitle = lbRankBy === 'uptime'
        ? 'Total time the app was open, focused or not'
        : 'Time actively interacting with the app';

    // Column header row — describes what each column represents.
    const headerHtml = `
        <div class="full-lb-header${showMovement ? '' : ' no-movement'}">
            <span title="${showMovement ? "Rank, or movement vs. yesterday's ranking" : 'Rank'}">Rank</span>
            ${showMovement ? '<span></span>' : ''}
            <span>Application</span>
            <span title="${primaryTitle}">${primaryLabel}</span>
            <span title="Time the app was open but you were away from the keyboard">AFK</span>
            <span title="Focused time as a share of time the app was open and you were at the keyboard">Efficiency</span>
        </div>`;

    const rowsHtml = sorted.map((app) => {
        const rank = rankOf[app.appName];
        const totalMins = app.totalMinutes || 0;
        const activeMins = app.activeMinutes || 0;
        // AFK isn't a separate field from the backend — it's the gap between total
        // open time and active (non-AFK) time already returned per app.
        const afkMins = Math.max(0, totalMins - activeMins);
        const efficiency = activeMins > 0 ? Math.round((app.focusedMinutes / activeMins) * 100) : 0;
        // Efficiency describes, it does not grade. A media player or a game
        // legitimately scores low because it stays open while you watch or idle,
        // and rendering that in red read as a failing mark for ordinary use. The
        // ramp now runs neutral -> brass; the bar's width already carries the
        // magnitude, so colour does not need to also carry a verdict.
        const effColor = efficiency >= 70 ? 'var(--brass)'
                       : efficiency >= 35 ? 'var(--brass-dim)'
                       : 'var(--text-faint)';

        const medalOrRank = rankMarkerHtml(rank);
        const trendHtml = showMovement ? rankMoveHtml(rank, prevRankOf[app.appName], prevPrimaryMins(app)) : '';

        const cat = app.category || 'Other';
        return `
            <div class="full-lb-row${showMovement ? '' : ' no-movement'}" data-open-app="${escapeHtml(app.appName)}" role="button" tabindex="0">
                ${medalOrRank}
                ${showMovement ? `<div class="full-lb-trend">${trendHtml}</div>` : ''}
                <div class="full-lb-name-wrap">
                    <div class="full-lb-icon" style="${avatarStyle(cat)}">${escapeHtml((app.appName || '?').charAt(0).toUpperCase())}</div>
                    <div class="full-lb-name-col">
                        <div class="full-lb-app-name" title="${escapeHtml(app.appName)}">${escapeHtml(displayAppName(app.appName))}</div>
                        <div class="full-lb-cat cat-link" style="--cat-color:${catTextColor(cat)}" data-open-cat="${escapeHtml(cat)}" role="button" tabindex="0">${escapeHtml(cat)}</div>
                    </div>
                </div>
                <div class="full-lb-time">${formatTime(primaryMins(app))}</div>
                <div class="full-lb-afk">${formatTime(afkMins)}</div>
                <div>
                    <div class="full-lb-eff-bar"><div class="full-lb-eff-fill" style="width:${efficiency}%;background:${effColor}"></div></div>
                    <div class="full-lb-eff-label">${efficiency}% of active time focused</div>
                </div>
            </div>`;
    }).join('');

    container.innerHTML = headerHtml + rowsHtml;
}

function rankMarkerHtml(rank) {
    if (rank === 1) return `<div class="medal medal-gold">1</div>`;
    if (rank === 2) return `<div class="medal medal-silver">2</div>`;
    if (rank === 3) return `<div class="medal medal-bronze">3</div>`;
    return `<div class="full-lb-rank">${rank}</div>`;
}

function rankMoveHtml(rank, prevRank, prevMins) {
    if (!prevMins) return `<span class="trend-pill trend-new">NEW</span>`;
    const delta = prevRank - rank;
    if (delta > 0) return `<span class="trend-pill trend-up">&#9650; ${delta}</span>`;
    if (delta < 0) return `<span class="trend-pill trend-down">&#9660; ${Math.abs(delta)}</span>`;
    return `<span class="trend-pill trend-flat">–</span>`;
}

/* ---------- Categories mode ----------
   Built from the same per-app payload: each category is the sum of its apps.
   shortcut: the previous-period figures only cover apps used in the current
   period too (that is what /api/leaderboard returns), so an app dropped since
   last period doesn't count toward its category's "before" number. Return
   previous-only apps from the endpoint if that ever matters. */
function renderCategoryLeaderboard(container, primaryMins, prevPrimaryMins) {
    const byCat = new Map();
    for (const a of lbData) {
        const name = a.category || 'Other';
        let c = byCat.get(name);
        if (!c) byCat.set(name, c = { name, apps: [], mins: 0, prev: 0, total: 0, active: 0, focused: 0 });
        c.apps.push(a);
        c.mins += primaryMins(a);
        c.prev += prevPrimaryMins(a);
        c.total += a.totalMinutes || 0;
        c.active += a.activeMinutes || 0;
        c.focused += a.focusedMinutes || 0;
    }
    const cats = [...byCat.values()];
    cats.forEach(c => c.apps.sort((a, b) => primaryMins(b) - primaryMins(a)));

    // Ranked against every category, then filtered, for the same reason as the app view.
    const ranked = [...cats].sort((a, b) => b.mins - a.mins);
    const rankOf = new Map(ranked.map((c, i) => [c.name, i + 1]));
    const prevRankOf = new Map([...cats].sort((a, b) => b.prev - a.prev).map((c, i) => [c.name, i + 1]));
    const grand = cats.reduce((s, c) => s + c.mins, 0) || 1;

    const tf = document.getElementById('lb-scope').dataset.value || 'all';
    const showMovement = tf !== 'all' && tf !== 'year';
    const badges = categoryBadges(cats, showMovement);

    const shown = ranked.filter(c => c.name.toLowerCase().includes(lbSearch)
        || c.apps.some(a => a.appName.toLowerCase().includes(lbSearch)));
    if (shown.length === 0) {
        container.innerHTML = `<div class="empty-state">No categories or apps match your search.</div>`;
        return;
    }

    const primaryLabel = lbRankBy === 'uptime' ? 'Uptime' : 'Focused';
    const headerHtml = `
        <div class="full-lb-header${showMovement ? '' : ' no-movement'}">
            <span>Rank</span>
            ${showMovement ? '<span></span>' : ''}
            <span>Category</span>
            <span>${primaryLabel}</span>
            <span title="Share of all ${primaryLabel.toLowerCase()} time in this period">Share</span>
            <span title="How the category's time splits across its apps">Line-up</span>
        </div>`;

    const rowsHtml = shown.map(c => {
        const rank = rankOf.get(c.name);
        const open = lbOpenCats.has(c.name);
        const share = Math.round((c.mins / grand) * 100);
        const mvp = c.apps[0];
        const mvpShare = c.mins > 0 ? Math.round((primaryMins(mvp) / c.mins) * 100) : 0;
        const badgeHtml = (badges.get(c.name) || [])
            .map(b => `<span class="cat-badge" title="${escapeHtml(b.title)}">${b.icon} ${escapeHtml(b.label)}</span>`).join('');

        return `
            <div class="cat-lb-item${open ? ' open' : ''}">
                <div class="full-lb-row${showMovement ? '' : ' no-movement'}" data-toggle-cat="${escapeHtml(c.name)}" role="button" tabindex="0" aria-expanded="${open}">
                    ${rankMarkerHtml(rank)}
                    ${showMovement ? `<div class="full-lb-trend">${rankMoveHtml(rank, prevRankOf.get(c.name), c.prev)}</div>` : ''}
                    <div class="full-lb-name-wrap">
                        <div class="full-lb-icon" style="${avatarStyle(c.name)}">${escapeHtml(c.name.charAt(0).toUpperCase())}</div>
                        <div class="full-lb-name-col">
                            <div class="full-lb-app-name"><span class="cat-lb-chevron">&#9656;</span> ${escapeHtml(c.name)} ${badgeHtml}</div>
                            <div class="full-lb-cat">${c.apps.length} app${c.apps.length === 1 ? '' : 's'} · led by ${escapeHtml(displayAppName(mvp.appName))}</div>
                        </div>
                    </div>
                    <div class="full-lb-time">${formatTime(c.mins)}</div>
                    <div class="cat-lb-share">${share}%</div>
                    <div>
                        <div class="cat-lb-lineup">${lineupSegmentsHtml(c, primaryMins)}</div>
                        <div class="full-lb-eff-label">${escapeHtml(displayAppName(mvp.appName))} carries ${mvpShare}%</div>
                    </div>
                </div>
                ${open ? categoryDetailsHtml(c, primaryMins, primaryLabel, showMovement ? tf : null) : ''}
            </div>`;
    }).join('');

    container.innerHTML = headerHtml + rowsHtml;
}

function lineupSegmentsHtml(c, primaryMins) {
    if (c.mins <= 0) return '';
    return c.apps.filter(a => primaryMins(a) > 0).map(a =>
        `<span style="width:${(primaryMins(a) / c.mins) * 100}%;background:${appColor(a.appName)}" title="${escapeHtml(displayAppName(a.appName))}: ${formatTime(primaryMins(a))}"></span>`
    ).join('');
}

function categoryDetailsHtml(c, primaryMins, primaryLabel, prevLabel) {
    const efficiency = c.active > 0 ? Math.round((c.focused / c.active) * 100) : 0;
    const afk = Math.max(0, c.total - c.active);
    const stat = (label, value) => `<div class="cat-lb-stat"><div class="cat-lb-stat-label">${label}</div><div class="cat-lb-stat-value">${value}</div></div>`;

    const appsHtml = c.apps.map((a, i) => {
        const pct = c.mins > 0 ? (primaryMins(a) / c.mins) * 100 : 0;
        return `
            <div class="cat-lb-app" data-open-app="${escapeHtml(a.appName)}" role="button" tabindex="0">
                <span class="cat-lb-app-rank">${i + 1}</span>
                <span class="cat-lb-dot" style="background:${appColor(a.appName)}"></span>
                <span class="cat-lb-app-name" title="${escapeHtml(a.appName)}">${escapeHtml(displayAppName(a.appName))}</span>
                <span class="cat-lb-app-bar"><span style="width:${pct}%;background:${appColor(a.appName)}"></span></span>
                <span class="cat-lb-app-pct">${Math.round(pct)}%</span>
                <span class="cat-lb-app-time">${formatTime(primaryMins(a))}</span>
            </div>`;
    }).join('');

    return `
        <div class="cat-lb-details">
            <div class="cat-lb-stats">
                ${stat(primaryLabel, formatTime(c.mins))}
                ${stat('Uptime', formatTime(c.total))}
                ${stat('AFK', formatTime(afk))}
                ${stat('Efficiency', `${efficiency}%`)}
                ${prevLabel ? stat(`vs previous ${prevLabel}`, trendPill(c.mins, c.prev)) : ''}
            </div>
            <div class="cat-lb-apps">${appsHtml}</div>
            <button type="button" class="btn btn-ghost cat-lb-open" data-open-cat="${escapeHtml(c.name)}">Open ${escapeHtml(c.name)} &rarr;</button>
        </div>`;
}

// A handful of superlatives, each awarded to at most one category so they stay
// worth reading. Needs two or more categories, or every badge is trivially won.
function categoryBadges(cats, showMovement) {
    const out = new Map();
    const give = (c, b) => { if (c) (out.get(c.name) || out.set(c.name, []).get(c.name)).push(b); };
    const live = cats.filter(c => c.mins > 0);
    if (live.length < 2) return out;
    const best = (list, score) => list.reduce((w, c) => (w === null || score(c) > score(w) ? c : w), null);

    if (showMovement) {
        const risers = live.filter(c => c.prev > 0 && c.mins > c.prev);
        give(best(risers, c => c.mins / c.prev),
             { icon: '&#128640;', label: 'Rising', title: 'Biggest growth since the previous period' });
    }
    // 30 minutes of active time before efficiency counts, so a 2-minute category can't take it.
    give(best(live.filter(c => c.active >= 30), c => c.focused / c.active),
         { icon: '&#127919;', label: 'Locked in', title: 'Highest share of active time spent focused' });
    give(best(live.filter(c => c.apps.length >= 3), c => c.apps.length),
         { icon: '&#127914;', label: 'Big tent', title: 'Most apps in one category' });
    return out;
}

function toggleCategoryRow(name) {
    if (lbOpenCats.has(name)) lbOpenCats.delete(name); else lbOpenCats.add(name);
    renderFullLeaderboard();
    document.querySelector(`#lb-list [data-toggle-cat="${CSS.escape(name)}"]`)?.focus();
}

document.addEventListener('DOMContentLoaded', () => {
    const list = document.getElementById('lb-list');
    list.addEventListener('click', (e) => {
        if (e.target.closest(OPENER_SELECTOR)) return;   // app / category openers inside win
        const row = e.target.closest('[data-toggle-cat]');
        if (row) toggleCategoryRow(row.dataset.toggleCat);
    });
    list.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        const row = e.target.closest('[data-toggle-cat]');
        if (!row || e.target !== row) return;
        e.preventDefault();
        toggleCategoryRow(row.dataset.toggleCat);
    });
});

Dashboard.tabs.leaderboard = { onEnter: fetchFullLeaderboard, refresh: fetchFullLeaderboard };
