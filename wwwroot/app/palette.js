/* ---------------------------------------------------------------------------
   The palette's client half.

   It owns no data. Everything it shows arrives from the WPF host over the
   WebView2 message bridge, and every action it takes goes back the same way --
   launching an app, capturing a hotkey and closing the window are native
   operations that have no business going through HTTP.

   The host is the only source of truth; this file renders it and reports
   intent. Written so that with no host attached (opened in a browser while
   working on it) it still renders, empty, rather than throwing.
   --------------------------------------------------------------------------- */

const bridge = window.chrome && window.chrome.webview ? window.chrome.webview : null;

function send(type, payload) {
    if (!bridge) return;
    bridge.postMessage(JSON.stringify({ type, ...(payload || {}) }));
}

let state = { apps: [], commands: [], focusToday: '—', tracking: true };
let query = '';
let active = 0;

const els = {
    q: document.getElementById('q'),
    results: document.getElementById('results'),
    focus: document.getElementById('focus-today'),
    statusDot: document.getElementById('status-dot'),
    statusText: document.getElementById('status-text'),
    count: document.getElementById('result-count'),
    counts: document.getElementById('counts'),
    enterVerb: document.getElementById('enter-verb')
};

/* Colours come from the same category palette the dashboard uses; the host
   sends the category and the tint is resolved here so the two surfaces cannot
   drift apart on it. */
const CATEGORY_TINT = {
    Gaming: 'rgba(139, 124, 255, 0.18)',
    Browsing: 'rgba(52, 211, 196, 0.18)',
    Communication: 'rgba(29, 118, 109, 0.28)',
    Development: 'rgba(139, 124, 255, 0.18)',
    Other: 'rgba(255, 255, 255, 0.035)'
};

function tint(category) {
    return CATEGORY_TINT[category] || CATEGORY_TINT.Other;
}

/* A plain subsequence match, so "vlr" finds Valorant. Ranked so that a prefix
   beats a word start, which beats a loose match anywhere. */
function score(name, q) {
    if (!q) return 0;
    const n = name.toLowerCase();
    if (n.startsWith(q)) return 3;
    if (n.split(/[\s\-_.]/).some(w => w.startsWith(q))) return 2;

    let i = 0;
    for (const ch of n) {
        if (ch === q[i]) i++;
        if (i === q.length) return 1;
    }
    return -1;
}

function visible() {
    const q = query.trim().toLowerCase();

    const apps = state.apps
        .map(a => ({ item: a, kind: 'app', s: score(a.name, q) }))
        .filter(r => r.s >= 0)
        .sort((a, b) => b.s - a.s);

    const commands = state.commands
        .map(c => ({ item: c, kind: 'command', s: score(c.title, q) }))
        .filter(r => r.s >= 0)
        .sort((a, b) => b.s - a.s);

    return { apps, commands, all: [...apps, ...commands] };
}

function render() {
    const { apps, commands, all } = visible();
    if (active >= all.length) active = Math.max(0, all.length - 1);

    els.results.textContent = '';

    if (all.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = query ? `Nothing matches "${query}"` : 'Nothing to show yet';
        els.results.appendChild(empty);
    } else {
        let index = 0;
        index = appendGroup(query ? 'APPS' : 'RECENT', apps, index);
        appendGroup('COMMANDS', commands, index);
    }

    els.count.textContent = query ? `${all.length} result${all.length === 1 ? '' : 's'}` : '';
    els.counts.textContent =
        `${state.apps.length} APPS · ${state.apps.filter(a => a.hotkey).length} HOTKEYS`;

    const current = all[active];
    els.enterVerb.textContent =
        !current ? 'LAUNCH'
        : current.kind === 'command' ? 'RUN'
        : current.item.running ? 'FOCUS' : 'LAUNCH';
}

function appendGroup(label, rows, startIndex) {
    if (rows.length === 0) return startIndex;

    const group = document.createElement('div');
    group.className = 'group';

    const heading = document.createElement('span');
    heading.className = 'label';
    heading.textContent = label;
    group.appendChild(heading);

    const list = document.createElement('div');
    list.className = 'group-rows';

    let index = startIndex;
    for (const row of rows) {
        list.appendChild(buildRow(row, index));
        index++;
    }

    group.appendChild(list);
    els.results.appendChild(group);
    return index;
}

function buildRow(row, index) {
    const el = document.createElement('div');
    el.className = 'row' + (index === active ? ' active' : '');
    el.addEventListener('mousemove', () => { if (active !== index) { active = index; render(); } });
    el.addEventListener('click', () => { active = index; activate(); });

    const avatar = document.createElement('span');
    avatar.className = 'avatar';

    const text = document.createElement('span');
    text.className = 'row-text';

    const name = document.createElement('span');
    name.className = 'row-name';

    if (row.kind === 'app') {
        const app = row.item;
        avatar.style.background = tint(app.category);
        avatar.textContent = (app.name[0] || '?').toUpperCase();
        name.textContent = app.name;

        const sub = document.createElement('span');
        sub.className = 'row-sub';
        sub.textContent = app.category || 'Other';
        text.append(name, sub);
        el.append(avatar, text);

        const spacer = document.createElement('span');
        spacer.className = 'row-spacer';
        el.appendChild(spacer);

        if (app.hotkey) {
            const chip = document.createElement('span');
            chip.className = 'row-chip';
            chip.textContent = app.hotkey;
            el.appendChild(chip);
        } else if (app.autoStart) {
            const chip = document.createElement('span');
            chip.className = 'row-chip';
            chip.textContent = 'Auto-start';
            el.appendChild(chip);
        }

        const figure = document.createElement('span');
        figure.className = 'row-figure';
        figure.textContent = app.today || '';
        el.appendChild(figure);
    } else {
        const command = row.item;
        avatar.style.background = CATEGORY_TINT.Other;
        avatar.textContent = '›';
        avatar.style.color = 'var(--text-dim)';
        name.textContent = command.title;
        text.appendChild(name);
        el.append(avatar, text);

        const spacer = document.createElement('span');
        spacer.className = 'row-spacer';
        el.appendChild(spacer);

        if (command.hint) {
            const hint = document.createElement('span');
            hint.className = 'row-sub';
            hint.textContent = command.hint;
            el.appendChild(hint);
        }
    }

    return el;
}

function activate() {
    const current = visible().all[active];
    if (!current) return;

    if (current.kind === 'app') send('activate-app', { id: current.item.id });
    else send('run-command', { id: current.item.id });
}

function move(delta) {
    const total = visible().all.length;
    if (total === 0) return;
    active = (active + delta + total) % total;
    render();
    const el = els.results.querySelector('.row.active');
    if (el) el.scrollIntoView({ block: 'nearest' });
}

els.q.addEventListener('input', () => { query = els.q.value; active = 0; render(); });

document.addEventListener('keydown', e => {
    switch (e.key) {
        case 'ArrowDown': e.preventDefault(); move(1); break;
        case 'ArrowUp': e.preventDefault(); move(-1); break;
        case 'Enter': e.preventDefault(); activate(); break;
        case 'Escape': e.preventDefault(); send('close'); break;
        case 'Tab': {
            e.preventDefault();
            const current = visible().all[active];
            if (current && current.kind === 'app') send('edit-app', { id: current.item.id });
            break;
        }
    }
});

/* The host pushes a whole new state rather than deltas: the palette is small
   enough that reconciling partial updates would cost more than it saves. */
if (bridge) {
    bridge.addEventListener('message', event => {
        let message;
        try {
            message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
        } catch {
            return;
        }
        if (!message || message.type !== 'state') return;

        state = { ...state, ...message.state };
        els.focus.textContent = state.focusToday || '—';
        els.statusText.textContent = state.tracking ? 'TRACKING' : 'PAUSED';
        els.statusDot.style.background = state.tracking ? 'var(--teal)' : 'var(--text-faint)';
        render();
    });
}

/* Reset on every open: a palette that reopens holding the last query is a
   palette you have to clear before you can use it. */
window.addEventListener('focus', () => {
    query = '';
    els.q.value = '';
    active = 0;
    els.q.focus();
    render();
    send('ready');
});

render();
send('ready');
