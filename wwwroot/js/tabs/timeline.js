/* ==========================================
   TAB: Timeline (Gantt Chart)
   ========================================== */

async function loadTimeline() {
    try {
        const res = await fetch(`/api/timeline?date=${getSelectedDate()}`);
        const sessions = await res.json();

        const container = document.getElementById('timeline-rows');
        if (!sessions || sessions.length === 0) {
            container.innerHTML = '<div class="text-center py-12 text-slate-500 font-bold bg-slate-800/20 rounded-xl mx-6 mt-4">No sessions recorded for this day.</div>';
            return;
        }

        // Group sessions by AppName
        const appGroups = {};
        sessions.forEach(s => {
            const name = s.appName || s.AppName; // Handle JSON casing safely
            const cat = s.category || s.Category;
            const startStr = s.start || s.Start;
            const endStr = s.end || s.End;
            const dur = s.durationMinutes || s.DurationMinutes;
            const startMins = s.startMinutes || s.StartMinutes;

            if (!appGroups[name]) appGroups[name] = { category: cat, blocks: [], totalDur: 0 };
            appGroups[name].blocks.push({ startStr, endStr, dur, startMins, cat });
            appGroups[name].totalDur += dur;
        });

        // Sort apps by Total Duration (Most used apps at the top)
        const sortedApps = Object.entries(appGroups).sort((a, b) => b[1].totalDur - a[1].totalDur);

        let html = '';
        for (const [appName, data] of sortedApps) {
            let blocksHtml = data.blocks.map(b => {
                const leftPct = (b.startMins / 1440) * 100;
                let widthPct = (b.dur / 1440) * 100;
                if (widthPct < 0.3) widthPct = 0.3; // Make ultra-fast sessions visible

                const colorClass = categoryColors[b.cat] || 'bg-slate-500';
                const tooltip = `${appName}\n${b.startStr} - ${b.endStr}\nDuration: ${formatTime(b.dur)}`;

                // Thicker blocks (top-1 bottom-1) and slight glow
                return `<div class="absolute top-1 bottom-1 rounded shadow-[0_0_8px_rgba(0,0,0,0.5)] ${colorClass} hover:brightness-125 transition-all cursor-pointer border border-white/20" 
                             style="left: ${leftPct}%; width: ${widthPct}%" title="${tooltip}" onclick="openDrilldown('${appName}')"></div>`;
            }).join('');

            html += `
                <div class="flex items-center h-12 bg-slate-800/30 border border-slate-700/30 rounded-lg mx-4 hover:bg-slate-700/40 transition-colors group">
                    <div class="w-32 truncate px-4 font-bold text-xs text-slate-300 group-hover:text-blue-400 cursor-pointer text-right" onclick="openDrilldown('${appName}')" title="${appName}">
                        ${appName}
                    </div>
                    <div class="flex-1 relative h-full border-l border-slate-700/50">
                        ${blocksHtml}
                    </div>
                </div>`;
        }

        container.innerHTML = html;
    } catch (err) { console.error(err); }
}

Dashboard.tabs.timeline = { onEnter: loadTimeline };