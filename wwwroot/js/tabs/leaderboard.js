/* ==========================================================
   TAB: LEADERBOARD
   Default scope: All Time. Spotify-style day-over-day rank
   deltas, Olympic-medal styling for the top 3.
   ========================================================== */

let lbData = [];
let lbSearch = '';

function setLbTimeframe(tf, btnEl) {
    document.querySelectorAll('#lb-scope button').forEach(b => b.classList.toggle('active', b === btnEl));
    document.getElementById('lb-scope').dataset.value = tf;
    fetchFullLeaderboard();
}

function handleLbSearch() {
    lbSearch = document.getElementById('lb-search').value.toLowerCase();
    renderFullLeaderboard();
}

async function fetchFullLeaderboard() {
    const tf = document.getElementById('lb-scope').dataset.value || 'all';
    try {
        const res = await fetch(`/api/leaderboard?timeframe=${tf}&date=${getLocalTodayStr()}`);
        lbData = await res.json();
        renderFullLeaderboard();
    } catch (err) { console.error(err); }
}

function renderFullLeaderboard() {
    const container = document.getElementById('lb-list');
    if (!lbData || lbData.length === 0) {
        container.innerHTML = `<div class="empty-state">No data yet. Time to open some apps.</div>`;
        return;
    }

    const filtered = lbData.filter(a => a.appName.toLowerCase().includes(lbSearch));
    const sorted = [...filtered].sort((a, b) => b.focusedMinutes - a.focusedMinutes);

    // Rank yesterday's data (by prevFocusedMinutes) to compute the day-over-day delta.
    const prevSorted = [...filtered].sort((a, b) => (b.prevFocusedMinutes || 0) - (a.prevFocusedMinutes || 0));
    const prevRankOf = {};
    prevSorted.forEach((a, i) => { prevRankOf[a.appName] = i + 1; });

    if (sorted.length === 0) {
        container.innerHTML = `<div class="empty-state">No apps match your search.</div>`;
        return;
    }

    // Column header row — describes what each column represents.
    const headerHtml = `
        <div class="full-lb-header">
            <span title="Rank, or movement vs. yesterday's ranking">Rank</span>
            <span></span>
            <span>Application</span>
            <span title="Time actively interacting with the app">Focused</span>
            <span title="Time the app was open but you were away from the keyboard">AFK</span>
            <span title="Focused time as a share of total time the app was open">Efficiency</span>
        </div>`;

    const rowsHtml = sorted.map((app, i) => {
        const rank = i + 1;
        const totalMins = app.totalMinutes || 0;
        const activeMins = app.activeMinutes || 0;
        // AFK isn't a separate field from the backend — it's the gap between total
        // open time and active (non-AFK) time already returned per app.
        const afkMins = Math.max(0, totalMins - activeMins);
        const efficiency = activeMins > 0 ? Math.round((app.focusedMinutes / activeMins) * 100) : 0;
        const effColor = efficiency > 80 ? 'var(--teal)' : (efficiency < 20 ? 'var(--rose)' : 'var(--brass)');

        let medalOrRank = `<div class="full-lb-rank">${rank}</div>`;
        if (rank === 1) medalOrRank = `<div class="medal medal-gold">1</div>`;
        else if (rank === 2) medalOrRank = `<div class="medal medal-silver">2</div>`;
        else if (rank === 3) medalOrRank = `<div class="medal medal-bronze">3</div>`;

        let trendHtml = `<span class="trend-pill trend-flat">–</span>`;
        const prevRank = prevRankOf[app.appName];
        if (!app.prevFocusedMinutes) {
            trendHtml = `<span class="trend-pill trend-new">NEW</span>`;
        } else if (prevRank !== undefined) {
            const delta = prevRank - rank;
            if (delta > 0) trendHtml = `<span class="trend-pill trend-up">&#9650; ${delta}</span>`;
            else if (delta < 0) trendHtml = `<span class="trend-pill trend-down">&#9660; ${Math.abs(delta)}</span>`;
            else trendHtml = `<span class="trend-pill trend-flat">–</span>`;
        }

        return `
            <div class="full-lb-row" onclick="openDrilldown('${app.appName}')">
                ${medalOrRank}
                <div class="full-lb-trend">${trendHtml}</div>
                <div class="full-lb-name-wrap">
                    <div class="full-lb-icon">${app.appName.charAt(0).toUpperCase()}</div>
                    <div class="full-lb-name-col">
                        <div class="full-lb-app-name">${app.appName}</div>
                        <div class="full-lb-cat">${app.category || 'Uncategorized'}</div>
                    </div>
                </div>
                <div class="full-lb-time">${formatTime(app.focusedMinutes)}</div>
                <div class="full-lb-afk">${formatTime(afkMins)}</div>
                <div>
                    <div class="full-lb-eff-bar"><div class="full-lb-eff-fill" style="width:${efficiency}%;background:${effColor}"></div></div>
                    <div class="full-lb-eff-label">${efficiency}% efficiency</div>
                </div>
            </div>`;
    }).join('');

    container.innerHTML = headerHtml + rowsHtml;
}

Dashboard.tabs.leaderboard = { onEnter: fetchFullLeaderboard };
