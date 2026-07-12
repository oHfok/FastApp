/* ==========================================
   TAB: All Applications
   ========================================== */

let allAppsData = [];
let allAppsSearchQuery = '';
let currentSortKey = 'appName';
let sortAscending = true;

async function loadAllApps() {
    try {
        const res = await fetch('/api/all-apps');
        allAppsData = await res.json();
        renderAllApps();
    } catch (err) { console.error(err); }
}

function handleAllAppsSearch() {
    allAppsSearchQuery = document.getElementById('allapps-search').value.toLowerCase();
    renderAllApps();
}

// Handles the backend post for modifying category directly from the All Apps tab
async function updateAllAppsCategory(appName, newCategory) {
    await fetch('/api/update-category', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ appName: appName, category: newCategory }) });
    let row = allAppsData.find(a => getAppProp(a, 'appName') === appName);
    if (row) {
        if (row.Category !== undefined) row.Category = newCategory;
        if (row.category !== undefined) row.category = newCategory;
    }
}

// Safely gets property regardless of JSON casing (camelCase vs PascalCase)
function getAppProp(obj, key) {
    const pascalKey = key.charAt(0).toUpperCase() + key.slice(1);
    return obj[key] !== undefined ? obj[key] : obj[pascalKey];
}

function sortAllApps(key) {
    if (currentSortKey === key) {
        sortAscending = !sortAscending;
    } else {
        currentSortKey = key;
        sortAscending = key === 'appName' || key === 'category';
    }

    allAppsData.sort((a, b) => {
        let valA = getAppProp(a, key), valB = getAppProp(b, key);
        if (typeof valA === 'string') {
            return sortAscending ? valA.localeCompare(valB) : valB.localeCompare(valA);
        } else {
            return sortAscending ? valA - valB : valB - valA;
        }
    });

    renderAllApps();
}

function renderAllApps() {
    const container = document.getElementById('allapps-body');
    let renderData = allAppsData.filter(a => getAppProp(a, 'appName').toLowerCase().includes(allAppsSearchQuery));

    if (renderData.length === 0) {
        container.innerHTML = '<tr><td colspan="6" class="text-center py-12 text-slate-500 font-medium">No applications found in lifetime history.</td></tr>';
        return;
    }

    container.innerHTML = renderData.map(app => {
        const name = getAppProp(app, 'appName');
        const cat = getAppProp(app, 'category') || 'Other';
        const focus = getAppProp(app, 'totalFocus');
        const runtime = getAppProp(app, 'totalRuntime');
        const afk = getAppProp(app, 'totalAfk');
        const longest = getAppProp(app, 'longestSession');

        // Grab the first letter for the avatar icon
        const firstLetter = name.charAt(0).toUpperCase();

        // Build the dropdown options dynamically
        let categoryOptions = allCategories.map(c => `<option value="${c}" ${cat === c ? 'selected' : ''}>${c}</option>`).join('');
        if (!allCategories.includes(cat)) categoryOptions += `<option value="${cat}" selected>${cat}</option>`;

        return `
            <tr class="group hover:bg-slate-800/40 transition-colors cursor-pointer" onclick="openDrilldown('${name}')">
                
                <!-- App Name & Icon -->
                <td class="py-3 px-6">
                    <div class="flex items-center gap-3">
                        <div class="w-8 h-8 rounded-lg bg-slate-800 border border-slate-600/50 flex items-center justify-center text-slate-300 font-bold text-sm shadow-inner group-hover:border-blue-500/50 transition-colors">
                            ${firstLetter}
                        </div>
                        <span class="font-semibold text-slate-200 group-hover:text-blue-400 transition-colors tracking-wide">${name}</span>
                    </div>
                </td>
                
                <!-- Sleek Badge Dropdown (FIXED: Added custom-select) -->
                <td class="py-3 px-6">
                    <select onchange="updateAllAppsCategory('${name}', this.value)" onclick="event.stopPropagation()" 
                            class="custom-select bg-slate-800/50 text-slate-300 hover:text-white text-xs font-semibold tracking-wide px-3 py-1.5 rounded-lg border border-transparent hover:border-slate-600 outline-none cursor-pointer transition-all shadow-sm">
                        ${categoryOptions}
                    </select>
                </td>
                
                <!-- Focus Time (Highlighted) -->
                <td class="py-3 px-6 text-right">
                    <span class="font-semibold text-blue-400/90">${formatTime(focus)}</span>
                </td>
                
                <!-- Muted Stats -->
                <td class="py-3 px-6 text-right">
                    <span class="text-slate-400 font-medium">${formatTime(runtime)}</span>
                </td>
                <td class="py-3 px-6 text-right">
                    <span class="text-slate-500 font-medium">${formatTime(afk)}</span>
                </td>
                <td class="py-3 px-6 text-right">
                    <span class="text-slate-400 font-medium">${formatTime(longest)}</span>
                </td>
            </tr>
        `;
    }).join('');
}

Dashboard.tabs.allapps = { onEnter: loadAllApps };