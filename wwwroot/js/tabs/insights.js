/* ==========================================
   TAB: Behavioral Insights
   ========================================== */

let rhythmChartInstance = null;
let fatigueChartInstance = null;

async function loadInsights() {
    try {
        const res = await fetch(`/api/insights?date=${getSelectedDate()}`);
        const data = await res.json();

        // 1. Top Statistics
        document.getElementById('insight-streak').innerText = formatTime(data.longestBlock);
        document.getElementById('insight-avg').innerText = formatTime(data.averageSpan);

        // 2. Heatmap
        document.getElementById('heatmap-grid').style.gridTemplateColumns = 'repeat(24, minmax(0, 1fr))';
        let maxMins = Math.max(...data.heatmap.map(h => h.totalMinutes), 1);
        let gridHtml = '';
        for (let day = 0; day < 7; day++) {
            for (let hour = 0; hour < 24; hour++) {
                const cellData = data.heatmap.find(h => h.dayIndex === day && h.hour === hour);
                const mins = cellData ? cellData.totalMinutes : 0;
                const intensity = mins / maxMins;
                let bgColor = 'bg-slate-800/40 border border-slate-700/30';
                if (intensity > 0.75) bgColor = 'bg-orange-500 shadow-[0_0_8px_rgba(249,115,22,0.5)] border-none';
                else if (intensity > 0.5) bgColor = 'bg-orange-500/70 border-none';
                else if (intensity > 0.25) bgColor = 'bg-orange-500/40 border-none';
                else if (intensity > 0) bgColor = 'bg-orange-500/20 border-none';
                gridHtml += `<div class="${bgColor} heatmap-cell w-full h-full rounded shadow-sm" title="${['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][day]} at ${hour}:00 - ${formatTime(mins)} focused"></div>`;
            }
        }
        document.getElementById('heatmap-grid').innerHTML = gridHtml;

        // 3. Productivity Rhythm Chart (Overlapping Line)
        if (rhythmChartInstance) rhythmChartInstance.destroy();
        rhythmChartInstance = new Chart(document.getElementById('rhythmChart'), {
            type: 'line',
            data: {
                labels: data.rhythm.map(r => r.hour + ':00'),
                datasets: [
                    {
                        label: 'Work & Study',
                        data: data.rhythm.map(r => r.work),
                        borderColor: '#3b82f6', // Blue
                        backgroundColor: 'rgba(59, 130, 246, 0.15)',
                        borderWidth: 3,
                        tension: 0.4,
                        fill: true,
                        pointRadius: 0,
                        pointHoverRadius: 6
                    },
                    {
                        label: 'Entertainment',
                        data: data.rhythm.map(r => r.play),
                        borderColor: '#f43f5e', // Rose
                        backgroundColor: 'rgba(244, 63, 94, 0.15)',
                        borderWidth: 3,
                        tension: 0.4,
                        fill: true,
                        pointRadius: 0,
                        pointHoverRadius: 6
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { position: 'top', labels: { color: '#cbd5e1', font: { weight: 'bold' } } },
                    tooltip: {
                        backgroundColor: 'rgba(15,23,42,0.9)',
                        titleColor: '#fff',
                        callbacks: { label: c => ` ${c.dataset.label}: ${formatTime(c.raw)}` }
                    }
                },
                scales: {
                    y: { grid: { color: 'rgba(255,255,255,0.05)' }, ticks: { callback: v => formatTime(v) } },
                    x: { grid: { display: false } }
                }
            }
        });

        // 4. Focus Fatigue Curve Chart (Bar Chart)
        if (fatigueChartInstance) fatigueChartInstance.destroy();
        fatigueChartInstance = new Chart(document.getElementById('fatigueChart'), {
            type: 'bar',
            data: {
                labels: data.fatigue.map(f => f.day),
                datasets: [{
                    label: 'Avg Session Length',
                    data: data.fatigue.map(f => f.avgMinutes),
                    // Colors Weekends green, Weekdays blue
                    backgroundColor: data.fatigue.map((f, i) => i >= 5 ? 'rgba(16, 185, 129, 0.8)' : 'rgba(56, 189, 248, 0.8)'),
                    borderRadius: 4,
                    hoverBackgroundColor: '#818cf8'
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: 'rgba(15,23,42,0.9)',
                        titleColor: '#38bdf8',
                        callbacks: { label: c => ` Avg: ${formatTime(c.raw)}` }
                    }
                },
                scales: {
                    y: { grid: { color: 'rgba(255,255,255,0.05)' }, ticks: { callback: v => formatTime(v) } },
                    x: { grid: { display: false }, ticks: { font: { weight: 'bold' } } }
                }
            }
        });

    } catch (err) { console.error(err); }
}

Dashboard.tabs.insights = { onEnter: loadInsights };