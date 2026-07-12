/* ==========================================
   TAB: Leaderboards
   ========================================== */

function updateDateRangeIndicator() {
    const tf = document.getElementById('timeframe-select').value;
    const dateStr = getSelectedDate();

    // Safely parse the local date to prevent timezone bugs
    const parts = dateStr.split('-');
    const target = new Date(parts[0], parts[1] - 1, parts[2]);

    const formatDate = d => d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
    const subtractDays = (d, days) => new Date(d.getTime() - days * 86400000);

    let text = '';
    if (tf === 'day') {
        text = formatDate(target);
    } else if (tf === 'week') {
        text = `${formatDate(subtractDays(target, 6))} → ${formatDate(target)}`;
    } else if (tf === 'month') {
        text = `${formatDate(subtractDays(target, 29))} → ${formatDate(target)}`;
    } else if (tf === 'year') {
        text = `${formatDate(subtractDays(target, 364))} → ${formatDate(target)}`;
    } else {
        text = `First Data Record → ${formatDate(target)}`;
    }

    document.getElementById('leaderboard-date-range').innerText = text;
}

async function fetchLeaderboardData() {
    try {
        updateDateRangeIndicator();

        const timeframe = document.getElementById('timeframe-select').value;
        const res = await fetch(`/api/leaderboard?timeframe=${timeframe}&date=${getSelectedDate()}`);
        currentLeaderboardData = await res.json();
        renderLeaderboard();
    } catch (err) { console.error(err); }
}

function handleSearch() { searchQuery = document.getElementById('app-search').value.toLowerCase(); renderLeaderboard(); }

function setLeaderboardMode(mode) {
    currentLeaderboardMode = mode;
    document.getElementById('btn-mode-focus').className = mode === 'focus' ? 'px-5 py-2 rounded-lg text-sm font-bold bg-blue-500 text-white shadow-[0_0_15px_rgba(56,189,248,0.4)] transition-all' : 'px-5 py-2 rounded-lg text-sm font-bold text-slate-400 hover:text-white hover:bg-slate-800 transition-all';
    document.getElementById('btn-mode-runtime').className = mode === 'runtime' ? 'px-5 py-2 rounded-lg text-sm font-bold bg-blue-500 text-white shadow-[0_0_15px_rgba(56,189,248,0.4)] transition-all' : 'px-5 py-2 rounded-lg text-sm font-bold text-slate-400 hover:text-white hover:bg-slate-800 transition-all';
    document.getElementById('col-trend-header').innerText = mode === 'focus' ? 'Rank' : 'Trend';
    renderLeaderboard();
}

function sortLeaderboard(criteria) {
    if (criteria === 'focus') currentLeaderboardData.sort((a, b) => b.focusedMinutes - a.focusedMinutes);
    if (criteria === 'runtime') currentLeaderboardData.sort((a, b) => b.activeMinutes - a.activeMinutes);
    if (criteria === 'efficiency') currentLeaderboardData.sort((a, b) => {
        let effA = a.activeMinutes > 0 ? (a.focusedMinutes / a.activeMinutes) : 0;
        let effB = b.activeMinutes > 0 ? (b.focusedMinutes / b.activeMinutes) : 0;
        return effB - effA;
    });
    renderHTMLTable(currentLeaderboardData, true);
}

function renderLeaderboard() {
    if (!currentLeaderboardData || currentLeaderboardData.length === 0) { document.getElementById('leaderboard-body').innerHTML = '<tr><td colspan="6" class="text-center py-12 text-slate-500 font-bold bg-slate-800/20 rounded-xl">No data found. Time to open some apps!</td></tr>'; return; }
    let renderData = currentLeaderboardData.filter(app => app.appName.toLowerCase().includes(searchQuery));
    const isAllTime = document.getElementById('timeframe-select').value === 'all';

    if (currentLeaderboardMode === 'focus') {
        renderData.sort((a, b) => b.focusedMinutes - a.focusedMinutes);
        let prevSorted = [...currentLeaderboardData].sort((a, b) => b.prevFocusedMinutes - a.prevFocusedMinutes);
        renderData.forEach((app, index) => {
            app.currentRank = index + 1;
            app.trendState = (app.prevFocusedMinutes === 0 || isAllTime) ? 'NEW' : ((prevSorted.findIndex(p => p.appName === app.appName) + 1) - app.currentRank);
        });
    } else {
        renderData.sort((a, b) => b.activeMinutes - a.activeMinutes);
        renderData.forEach((app, index) => {
            app.currentRank = index + 1;
            app.trendDiff = app.activeMinutes - app.prevActiveMinutes;
            app.trendState = (app.prevActiveMinutes === 0 || isAllTime) ? 'NEW' : 'DIFF';
        });
    }
    renderHTMLTable(renderData, false);
}


function renderHTMLTable(dataArray, isManualSort) {
    document.getElementById('leaderboard-body').innerHTML = dataArray.map((app, index) => {
        let efficiency = app.activeMinutes > 0 ? Math.round((app.focusedMinutes / app.activeMinutes) * 100) : 0;

        let barColor = efficiency > 80 ? 'from-emerald-400 to-emerald-500 shadow-[0_0_10px_rgba(52,211,153,0.4)]' :
            (efficiency < 20 ? 'from-rose-400 to-rose-500 shadow-[0_0_10px_rgba(251,113,113,0.4)]' : 'from-blue-400 to-blue-500 shadow-[0_0_10px_rgba(56,189,248,0.4)]');

        let trendHtml = `<span class="text-slate-600 text-xs font-bold">-</span>`;
        if (!isManualSort) {
            if (app.trendState === 'NEW') trendHtml = `<span class="text-blue-400 text-[10px] font-bold bg-blue-500/10 border border-blue-500/20 px-1.5 py-0.5 rounded uppercase tracking-wider">New</span>`;
            else if (currentLeaderboardMode === 'focus') {
                if (app.trendState > 0) trendHtml = `<span class="text-emerald-400 text-xs font-bold tracking-wider">▲ ${app.trendState}</span>`;
                else if (app.trendState < 0) trendHtml = `<span class="text-rose-400 text-xs font-bold tracking-wider">▼ ${Math.abs(app.trendState)}</span>`;
            }
            else if (currentLeaderboardMode === 'runtime') {
                if (app.trendDiff > 0) trendHtml = `<span class="text-amber-400 text-xs font-bold tracking-wider">▲ ${formatTime(app.trendDiff)}</span>`;
                else if (app.trendDiff < 0) trendHtml = `<span class="text-emerald-400 text-xs font-bold tracking-wider">▼ ${formatTime(Math.abs(app.trendDiff))}</span>`;
            }
        }

        let rankHtml = `<div class="font-bold text-slate-500 text-lg">#${index + 1}</div>`;
        if (index === 0) rankHtml = `<div class="w-8 h-8 mx-auto bg-gradient-to-br from-yellow-300 to-amber-500 text-amber-950 rounded-full flex items-center justify-center font-black shadow-[0_0_15px_rgba(251,191,36,0.4)] text-sm">1</div>`;
        else if (index === 1) rankHtml = `<div class="w-8 h-8 mx-auto bg-gradient-to-br from-slate-200 to-slate-400 text-slate-800 rounded-full flex items-center justify-center font-black shadow-[0_0_15px_rgba(148,163,184,0.3)] text-sm">2</div>`;
        else if (index === 2) rankHtml = `<div class="w-8 h-8 mx-auto bg-gradient-to-br from-orange-300 to-orange-600 text-orange-950 rounded-full flex items-center justify-center font-black shadow-[0_0_15px_rgba(249,115,22,0.3)] text-sm">3</div>`;

        let focusClass = currentLeaderboardMode === 'focus' ? 'text-blue-400 font-black text-lg text-glow-blue' : 'text-slate-400 font-bold text-md';
        let runClass = currentLeaderboardMode === 'runtime' ? 'text-blue-400 font-black text-lg text-glow-blue' : 'text-slate-400 font-bold text-md';
        let firstLetter = app.appName.charAt(0).toUpperCase();

        return `
                <tr class="group bg-slate-800/30 border border-slate-700/50 hover:bg-slate-700/60 hover:border-slate-500/50 transition-all duration-300 cursor-pointer hover:shadow-lg hover:-translate-y-1" onclick="openDrilldown('${app.appName}')">
                    <td class="py-3 px-4 rounded-l-2xl text-center align-middle w-24 border-y border-l border-slate-700/30 group-hover:border-slate-500/50 transition-colors">
                        ${rankHtml}
                        <div class="mt-1.5">${trendHtml}</div>
                    </td>
                    <td class="py-3 px-2 align-middle border-y border-slate-700/30 group-hover:border-slate-500/50 transition-colors">
                        <div class="flex items-center space-x-4">
                            <div class="w-10 h-10 rounded-xl bg-slate-900 border border-slate-600/50 flex items-center justify-center text-slate-300 font-black text-lg shadow-inner group-hover:border-blue-500/50 transition-colors">
                                ${firstLetter}
                            </div>
                            <span class="font-bold text-slate-100 text-base group-hover:text-blue-400 transition-colors tracking-wide">${app.appName}</span>
                        </div>
                    </td>
                    <td class="py-3 px-2 align-middle border-y border-slate-700/30 group-hover:border-slate-500/50 transition-colors">
                        <span class="text-xs font-bold text-slate-300 bg-slate-800/80 px-3 py-1.5 rounded-lg border border-slate-700/50 shadow-inner block w-max">${app.category}</span>
                    </td>
                    <td class="py-3 px-4 align-middle text-right ${focusClass} transition-all border-y border-slate-700/30 group-hover:border-slate-500/50">
                        ${formatTime(app.focusedMinutes)}
                    </td>
                    <td class="py-3 px-4 align-middle text-right ${runClass} transition-all border-y border-slate-700/30 group-hover:border-slate-500/50">
                        ${formatTime(app.activeMinutes)}
                    </td>
                    <td class="py-3 px-4 align-middle rounded-r-2xl w-56 border-y border-r border-slate-700/30 group-hover:border-slate-500/50 transition-colors relative">
                        <div class="flex items-center space-x-3 pr-8">
                            <span class="w-12 text-right text-xs font-black tracking-wider ${efficiency > 80 ? 'text-emerald-400' : (efficiency < 20 ? 'text-rose-400' : 'text-slate-300')}">${efficiency}%</span>
                            <div class="w-full bg-slate-950 rounded-full h-3 shadow-inner overflow-hidden border border-slate-700/80">
                                <div class="bg-gradient-to-r ${barColor} h-full rounded-full transition-all duration-700 ease-out" style="width: ${efficiency}%"></div>
                            </div>
                        </div>
                        <button class="absolute right-3 top-1/2 -translate-y-1/2 opacity-0 group-hover:opacity-100 transition-opacity text-slate-500 hover:text-rose-400 p-1.5 rounded-lg hover:bg-rose-500/10 border border-transparent hover:border-rose-500/20" onclick="event.stopPropagation(); hideApp('${app.appName}')" title="Hide Application">
                            <svg xmlns="http://www.w3.org/2000/svg" class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13.875 18.825A10.05 10.05 0 0112 19c-4.478 0-8.268-2.943-9.543-7a9.97 9.97 0 011.563-3.029m5.858.908a3 3 0 114.243 4.243M9.878 9.878l4.242 4.242M9.88 9.88l-3.29-3.29m7.532 7.532l3.29 3.29M3 3l3.59 3.59m0 0A9.953 9.953 0 0112 5c4.478 0 8.268 2.943 9.543 7a10.025 10.025 0 01-4.132 5.411m0 0L21 21" /></svg>
                        </button>
                    </td>
                </tr>
                <tr class="h-3"></tr>
            `;
    }).join('');
}

async function hideApp(appName) { await fetch('/api/hide', { method: 'POST', body: appName }); fetchLeaderboardData(); }

// Registers this tab's entry point with the tab system (see app.js)
Dashboard.tabs.leaderboard = { onEnter: fetchLeaderboardData };
