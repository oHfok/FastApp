/* ==========================================
   DRILLDOWN — per-app detail side panel.
   Shared UI, not tied to any single tab (opened from
   leaderboard rows). Lives outside the tab system.
   ========================================== */

async function openDrilldown(appName) {
    // 1. Immediately slide the panel open so the user knows it's working
    document.getElementById('drilldown-panel').classList.remove('translate-x-full');

    document.getElementById('drilldown-title').innerText = appName;
    document.getElementById('dd-icon').innerText = appName.charAt(0).toUpperCase();

    let row = typeof currentLeaderboardData !== 'undefined' ? currentLeaderboardData.find(a => a.appName === appName) : null;
    document.getElementById('dd-category').innerText = row ? row.category : 'Details';

    try {
        // 2. Fetch the data (using encodeURIComponent for safe spaces)
        const res = await fetch(`/api/app-details?appName=${encodeURIComponent(appName)}`);
        const data = await res.json();

        // 3. Prevent silent freezing if the C# backend throws an error
        if (data.error) {
            console.error("Backend Error:", data.error);
            alert("Backend Error: " + data.error);
            return;
        }

        // NEW: Path & Behavioral DNA Bindings
        document.getElementById('dd-path').innerText = data.executablePath || "Path not recorded";
        document.getElementById('dd-path').title = data.executablePath || "";
        document.getElementById('dd-path-btn').onclick = () => openAppFolder(data.executablePath);

        document.getElementById('dd-consistency').innerHTML = `${data.consistency}% <span class="text-sm font-medium text-slate-500 tracking-normal ml-1">(${data.daysActive || 0} of 30 days)</span>`;
        document.getElementById('dd-pattern').innerText = data.usagePattern;

        // Existing Bindings
        document.getElementById('dd-week-avg').innerText = formatHours(data.weekAvg);
        document.getElementById('dd-week-trend').innerHTML = getInlineTrendHtml(data.weekAvg, data.prevWeekAvg);

        document.getElementById('dd-month-avg').innerText = formatHours(data.monthAvg);
        document.getElementById('dd-month-trend').innerHTML = getInlineTrendHtml(data.monthAvg, data.prevMonthAvg);

        document.getElementById('dd-year-avg').innerText = formatHours(data.yearAvg);
        document.getElementById('dd-year-trend').innerHTML = getInlineTrendHtml(data.yearAvg, data.prevYearAvg);

        document.getElementById('dd-max-focus').innerText = data.maxFocusDay;
        document.getElementById('dd-max-running').innerText = data.maxRunningDay;

        document.getElementById('dd-focus').innerText = formatHours(data.allTimeFocused);
        document.getElementById('dd-running').innerText = formatHours(data.allTimeRunning);
        document.getElementById('dd-afk').innerText = formatHours(data.allTimeAfk);
        document.getElementById('dd-macros').innerText = data.totalMacros;

        // Existing Chart
        if (drillChart) drillChart.destroy();
        drillChart = new Chart(document.getElementById('drilldownChart'), {
            type: 'bar',
            data: {
                labels: data.history.map(h => h.date),
                datasets: [{ label: 'Focus Time', data: data.history.map(h => h.focusedMinutes), backgroundColor: '#38bdf8', borderRadius: 4, hoverBackgroundColor: '#60a5fa' }]
            },
            options: {
                responsive: true, maintainAspectRatio: false,
                plugins: { legend: { display: false }, tooltip: { backgroundColor: 'rgba(15,23,42,0.9)', titleColor: '#38bdf8', padding: 12, cornerRadius: 8, callbacks: { label: function (ctx) { return formatTime(ctx.raw); } } } },
                scales: { y: { ticks: { font: { weight: 'bold' }, callback: function (val) { return formatTime(val); } }, grid: { color: 'rgba(255,255,255,0.05)' } }, x: { grid: { display: false }, ticks: { font: { weight: 'bold' } } } }
            }
        });
    } catch (err) {
        console.error("Network or fetch error:", err);
        alert("Failed to load application details.");
    }
}

function closeDrilldown() {
    document.getElementById('drilldown-panel').classList.add('translate-x-full');
}

// NEW: Open Folder Network Request
async function openAppFolder(filePath) {
    if (!filePath || filePath === "Path not recorded in database." || filePath === "Path not recorded") {
        alert("We don't have the file path for this application yet. Ensure it is actively tracked!");
        return;
    }
    try {
        const res = await fetch('/api/open-folder', { method: 'POST', body: filePath });
        if (!res.ok) {
            const errorData = await res.json();
            alert("Could not open folder: " + (errorData.error || "Unknown error."));
        }
    } catch (err) {
        console.error("Folder open error:", err);
    }
}