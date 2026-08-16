/* ==========================================================
   SETTINGS
   1. Dashboard theme (instant, localStorage-only, no server call)
   2. Hidden apps list + unhide
   3. Database retention (default: Keep Forever) + save
   ========================================================== */

const DASHBOARD_THEME_KEY = 'fastapp-theme';

function loadDashboardTheme() {
    const current = localStorage.getItem(DASHBOARD_THEME_KEY) || 'instrument';
    updateThemePickerActiveState(current);
}

function setDashboardTheme(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem(DASHBOARD_THEME_KEY, theme);
    updateThemePickerActiveState(theme);
    // Chart.js canvases and the JS-computed heatmap fills only re-theme when
    // re-rendered, not from the CSS change alone — refresh whatever's behind
    // this drawer so it's already correct once Settings closes.
    refreshActiveTab();
}

function updateThemePickerActiveState(theme) {
    document.querySelectorAll('#theme-picker .theme-swatch').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.themeValue === theme);
    });
}

// TIMELINE_COLOR_MODE_KEY / getTimelineColorMode() live in utils.js since
// timelineSegmentsHtml() needs to read the mode at render time — this just
// owns writing it.
function loadTimelineColorMode() {
    const current = getTimelineColorMode();
    document.querySelectorAll('#timeline-color-toggle button').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.mode === current);
    });
}

function setTimelineColorMode(mode, btnEl) {
    localStorage.setItem(TIMELINE_COLOR_MODE_KEY, mode);
    document.querySelectorAll('#timeline-color-toggle button').forEach(btn => {
        btn.classList.toggle('active', btn === btnEl);
    });
    refreshActiveTab();
}

async function loadHiddenApps() {
    try {
        const res = await fetch('/api/hidden-apps');
        const apps = await res.json();
        const list = document.getElementById('hidden-apps-list');
        if (!apps || apps.length === 0) {
            list.innerHTML = `<div class="empty-state">No apps hidden.</div>`;
            return;
        }
        list.innerHTML = apps.map(app => `
            <div class="settings-list-item">
                <span>${app}</span>
                <button class="btn btn-ghost" onclick="unhideAppSetting('${app}')">Unhide</button>
            </div>`).join('');
    } catch (err) { console.error(err); }
}

async function unhideAppSetting(appName) {
    await fetch('/api/unhide', { method: 'POST', body: appName });
    loadHiddenApps();
    refreshActiveTab();
}

async function loadRetentionSetting() {
    try {
        const res = await fetch('/api/settings');
        const data = await res.json();
        const val = data.retentionDays ?? data.RetentionDays ?? 99999;
        const select = document.getElementById('retention-select');
        // If the stored value isn't one of the presets, fall back to Keep Forever
        const hasOption = Array.from(select.options).some(o => o.value == val);
        select.value = hasOption ? val : 99999;

        const captureWindowTitles = data.captureWindowTitles ?? data.CaptureWindowTitles ?? false;
        document.getElementById('window-titles-toggle').checked = captureWindowTitles;
    } catch (err) { console.error(err); }
}

async function saveRetentionSetting() {
    const val = document.getElementById('retention-select').value;
    await fetch('/api/settings/retention', { method: 'POST', body: val });
    const status = document.getElementById('retention-status');
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

async function saveWindowTitlesSetting() {
    const enabled = document.getElementById('window-titles-toggle').checked;
    await fetch('/api/settings/window-titles', { method: 'POST', body: String(enabled) });
    const status = document.getElementById('window-titles-status');
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

async function loadPinSetting() {
    try {
        const res = await fetch('/api/settings/pin');
        const data = await res.json();
        const hasPin = data.hasPin ?? data.HasPin ?? false;
        document.getElementById('pin-status-line').style.display = hasPin ? 'block' : 'none';
        document.getElementById('pin-form').style.display = hasPin ? 'none' : 'flex';
    } catch (err) { console.error(err); }
}

function showPinForm() {
    document.getElementById('pin-form').style.display = 'flex';
    document.getElementById('pin-status-line').style.display = 'none';
}

function flashPinStatus(text, isError) {
    const status = document.getElementById('pin-status');
    status.textContent = text;
    status.style.color = isError ? 'var(--rose)' : 'var(--teal)';
    status.style.display = 'block';
    setTimeout(() => { status.style.display = 'none'; }, 2500);
}

async function savePinSetting() {
    const pin = document.getElementById('pin-input').value;
    if (!pin || pin.length < 4) {
        flashPinStatus('PIN must be at least 4 characters.', true);
        return;
    }

    try {
        const res = await fetch('/api/settings/pin', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pin })
        });
        if (!res.ok) throw new Error('save failed');
        document.getElementById('pin-input').value = '';
        flashPinStatus('Saved.', false);
        loadPinSetting();
    } catch (err) {
        flashPinStatus('Failed to save PIN.', true);
    }
}

Dashboard.tabs.settings = {
    onEnter: () => {
        loadDashboardTheme();
        loadTimelineColorMode();
        loadHiddenApps();
        loadRetentionSetting();
        loadPinSetting();
    }
};
