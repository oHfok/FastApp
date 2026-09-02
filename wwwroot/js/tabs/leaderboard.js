/* ==========================================================
   TAB: LEADERBOARD
   Default scope: All Time. Spotify-style day-over-day rank
   deltas, Olympic-medal styling for the top 3.
   ========================================================== */

let lbData = [];
let lbSearch = '';
let lbRankBy = 'focus';

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
    const basis = lbRankBy === 'uptime' ? 'Ranked by uptime.' : 'Ranked by focus time.';
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

        let medalOrRank = `<div class="full-lb-rank">${rank}</div>`;
        if (rank === 1) medalOrRank = `<div class="medal medal-gold">1</div>`;
        else if (rank === 2) medalOrRank = `<div class="medal medal-silver">2</div>`;
        else if (rank === 3) medalOrRank = `<div class="medal medal-bronze">3</div>`;

        let trendHtml = `<span class="trend-pill trend-flat">–</span>`;
        const prevRank = prevRankOf[app.appName];
        if (!showMovement) {
            trendHtml = '';
        } else if (!prevPrimaryMins(app)) {
            trendHtml = `<span class="trend-pill trend-new">NEW</span>`;
        } else if (prevRank !== undefined) {
            const delta = prevRank - rank;
            if (delta > 0) trendHtml = `<span class="trend-pill trend-up">&#9650; ${delta}</span>`;
            else if (delta < 0) trendHtml = `<span class="trend-pill trend-down">&#9660; ${Math.abs(delta)}</span>`;
            else trendHtml = `<span class="trend-pill trend-flat">–</span>`;
        }

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

Dashboard.tabs.leaderboard = { onEnter: fetchFullLeaderboard, refresh: fetchFullLeaderboard };
