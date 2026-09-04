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

let state = { apps: [], trackable: [], commands: [], focusToday: '—', tracking: true, trackingText: 'Tracking' };
let query = '';
let active = 0;

const els = {
    q: document.getElementById('q'),
    results: document.getElementById('results'),
    commandBar: document.getElementById('command-bar'),
    statusText: document.getElementById('status-text'),
    statusDot: document.getElementById('status-dot'),
    facets: document.getElementById('facets'),
    attention: document.getElementById('attention'),
    reorderHint: document.getElementById('hint-reorder'),
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
const ICON_WARN =
    '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" '
    + 'stroke-width="2" stroke-linecap="round">'
    + '<path d="M12 9v4M12 17h.01"></path>'
    + '<path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"></path>'
    + '</svg>';

const ICON_UPDATE =
    '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" '
    + 'stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
    + '<path d="M12 19V5"></path><path d="M5 12l7-7 7 7"></path>'
    + '</svg>';

/* Category colours come from js/categories.js, shared with the dashboard. The
   list here used to hold five of the eleven categories, so Music, Media
   Production, Productivity, Fun, Education and Utilities all rendered in the
   grey that means "uncategorised". */

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

/* Which facets this machine actually has anything in. A setup that only uses
   hotkeys should never be offered a STARTUP filter that returns nothing. */
const FACETS = [
    { id: 'all', label: 'ALL', match: () => true },
    { id: 'hotkeys', label: 'HOTKEYS', match: a => !!a.hotkey },
    { id: 'startup', label: 'STARTUP', match: a => a.autoStart },
    { id: 'limited', label: 'LIMITED', match: a => a.limitMinutes > 0 },
    { id: 'actions', label: 'ACTIONS', match: a => a.isAction }
];

let facet = 'all';

function populatedFacets() {
    return FACETS.filter(f => f.id === 'all' || state.apps.some(f.match));
}

function visible() {
    const q = query.trim().toLowerCase();
    const chosen = FACETS.find(f => f.id === facet) || FACETS[0];

    const apps = state.apps
        .filter(a => q ? true : chosen.match(a))
        .map(a => ({ item: a, kind: 'app', s: score(a.name, q) }))
        .filter(r => r.s >= 0)
        .sort((a, b) => b.s - a.s);

    // Unfiltered, the list stays in the order you arranged, because that order
    // is not decoration: it is the sequence auto-launch follows, and it is what
    // Alt+Arrow rearranges. Sorting by most-recently-used (2.0.4) made sense
    // while the heading said RECENT and the list was a launcher; it hid the one
    // property of this list you can actually set.
    //
    // Typing still ranks by how well the name matches, which beats both.

    // Commands are full rows only once you are looking for one. Idle, the five
    // of them took more of the screen than the apps did, which is most of why
    // opening FastApp told you nothing -- they became a chip bar instead.
    // Applications FastApp has watched you use but was never told about. Only
    // ever while searching: idle, this screen is what you manage, and eleven
    // suggestions under it would be exactly the noise the redesign removed.
    // Capped, because a loose subsequence match over a few hundred names can
    // return most of them.
    const managed = new Set(state.apps.map(a => a.name.toLowerCase()));
    const trackable = !q ? [] : (state.trackable || [])
        .filter(t => !managed.has(t.name.toLowerCase()))
        .map(t => ({ item: t, kind: 'trackable', s: score(t.name, q) }))
        .filter(r => r.s >= 0)
        .sort((a, b) => b.s - a.s || b.item.minutes - a.item.minutes)
        .slice(0, 6);

    const commands = !q ? [] : state.commands
        .map(c => ({ item: c, kind: 'command', s: score(c.title, q) }))
        .filter(r => r.s >= 0)
        .sort((a, b) => b.s - a.s);

    return { apps, trackable, commands, all: [...apps, ...trackable, ...commands] };
}

function render() {
    const { apps, trackable, commands, all } = visible();
    if (active >= all.length) active = Math.max(0, all.length - 1);

    renderStatus();
    renderAttention();
    renderFacets();

    els.results.textContent = '';

    if (state.apps.length === 0 && !query) {
        renderNothingAdded();
    } else if (all.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = query ? `Nothing matches "${query}"` : 'Nothing in this filter';
        els.results.appendChild(empty);
    } else {
        // Actions get their own group: they have no window to focus, no startup
        // position and no daily limit, so listing them among the apps put three
        // permanently empty columns beside every one of them.
        const programs = apps.filter(r => !r.item.isAction);
        const actions = apps.filter(r => r.item.isAction);

        // Each group is measured on its own contents, so the actions group does
        // not carry a STARTUP or LIMIT heading over four permanently empty
        // cells just because some app elsewhere in the list has one.
        let index = 0;
        index = appendGroup(query ? 'APPS' : 'YOUR APPS', programs, index, activeColumns(programs));
        index = appendGroup('ACTIONS', actions, index, activeColumns(actions));
        index = appendGroup('YOU USE THESE, BUT HAVE NOT ADDED THEM', trackable, index, null);
        appendGroup('COMMANDS', commands, index, null);
    }

    renderCommandBar();
    fitWindow();

    els.count.textContent = query ? `${all.length} result${all.length === 1 ? '' : 's'}` : '';
    // Actions are not apps, and counting them as such made "6 APPS" out of four
    // programs and two macros.
    const programCount = state.apps.filter(a => !a.isAction).length;
    const actionCount = state.apps.length - programCount;
    const hotkeyCount = state.apps.filter(a => a.hotkey).length;
    els.counts.textContent = [
        `${programCount} APPS`,
        actionCount ? `${actionCount} ACTIONS` : null,
        `${hotkeyCount} HOTKEYS`
    ].filter(Boolean).join(' · ');

    // Reordering only means anything on the unfiltered, untyped list, where the
    // position is the thing being edited.
    els.reorderHint.hidden = !!query || facet !== 'all';

    // What the combobox is currently pointing at. Cleared rather than left
    // stale when there is nothing to point at, since a dangling reference
    // announces the wrong row.
    const activeRow = els.results.querySelector('.row.active');
    if (activeRow) els.q.setAttribute('aria-activedescendant', activeRow.id);
    else els.q.removeAttribute('aria-activedescendant');
    els.q.setAttribute('aria-expanded', all.length > 0 ? 'true' : 'false');

    const current = all[active];
    els.enterVerb.textContent =
        !current ? 'LAUNCH'
        : current.kind === 'command' ? 'RUN'
        : current.kind === 'trackable' ? 'ADD'
        : current.item.running ? 'FOCUS' : 'LAUNCH';
}

/// The commands, as one row of chips under the list. Still reachable by typing;
/// this is so they remain reachable without knowing that.
/// Grow the window to the configuration rather than picking one height for
/// everybody: three managed apps should not open a window sized for twenty, and
/// twenty should not be shown five at a time.
///
/// Only on the idle screen. Resizing while someone types would move the window
/// under them on every keystroke as the result count changed.
let sentHeight = 0;

function fitWindow() {
    if (view !== 'palette' || query) return;

    // The nothing-added panel grows to fill whatever height it is given, so
    // measuring it just returns the height it already has. Nothing to fit.
    if (state.apps.length === 0) {
        if (sentHeight === MIN_HEIGHT) return;
        sentHeight = MIN_HEIGHT;
        VIEWS.palette.h = MIN_HEIGHT;
        send('resize', { width: VIEWS.palette.w, height: MIN_HEIGHT });
        return;
    }

    const palette = VIEWS.palette;
    const shell = els.results.parentElement;
    const shellStyle = getComputedStyle(shell);
    const shellGap = parseFloat(shellStyle.rowGap) || 0;

    // Measured from the children, NOT from results.scrollHeight. scrollHeight
    // is never less than the element's own height, so once the window had grown
    // it reported the grown size as the content size and the window could only
    // ever ratchet upwards -- which is where the empty space below the list came
    // from. The children know how tall they actually are.
    //
    // Everything but the list is naturally sized; the list is the one that
    // stretches, so only it needs measuring from its own contents. Written this
    // way so a new sibling (the command bar, once it left the list) is counted
    // without anyone having to remember to add it.
    const listGap = parseFloat(getComputedStyle(els.results).rowGap) || 0;
    const rows = [...els.results.children];
    const listContent = rows.reduce((total, el) => total + el.getBoundingClientRect().height, 0)
                        + Math.max(0, rows.length - 1) * listGap;

    // Only rendered siblings: a flex gap does not appear beside a child that is
    // display:none, and the attention strip collapses itself when there is
    // nothing wrong. Counting it added a phantom gap on every healthy launch.
    const siblings = [...shell.children]
        .filter(el => el !== els.results && el.getBoundingClientRect().height > 0);
    const around = siblings.reduce((total, el) => total + el.getBoundingClientRect().height, 0);

    // The list plus its rendered siblings sit in a row with a gap between each
    // pair, so the number of gaps is the number of siblings.
    const gaps = siblings.length * shellGap;

    const padding = parseFloat(shellStyle.paddingTop) + 22;
    const wanted = Math.round(padding + around + gaps + listContent);
    const height = Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, wanted));

    if (height === sentHeight) return;
    sentHeight = height;
    palette.h = height;
    send('resize', { width: palette.w, height });
}

const MIN_HEIGHT = 420;
// Past this the window stops being a palette and the list scrolls instead.
const MAX_HEIGHT = 760;

/// A line that says what just happened and then goes away. Adding an app from
/// a search result gives no other feedback -- the row it came from disappears
/// as the list re-renders, which on its own reads like the click missed.
let toastTimer = null;

/// Put the caret in the search box, and check that it landed.
///
/// The host asks for this the instant it has taken the foreground, which can
/// be a beat before the WebView2 is ready to accept focus -- the call then does
/// nothing and the window sits there ignoring the keyboard. One retry on the
/// next frame covers that without a timer that could fight the user if they
/// have already clicked somewhere else.
function focusSearch() {
    if (view !== 'palette') return;

    els.q.focus();
    if (document.activeElement === els.q) return;

    requestAnimationFrame(() => {
        if (view === 'palette' && document.activeElement !== els.q) els.q.focus();
    });
}

function showToast(text) {
    if (!text) return;

    let toast = document.getElementById('toast');
    if (!toast) {
        toast = document.createElement('div');
        toast.id = 'toast';
        toast.className = 'toast';
        // Announced, because it is the only confirmation the window gives that
        // an app was added, and it was silent to a screen reader.
        toast.setAttribute('role', 'status');
        toast.setAttribute('aria-live', 'polite');
        document.body.appendChild(toast);
    }

    toast.textContent = text;
    toast.classList.add('on');

    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toast.classList.remove('on'), 2600);
}

/// A glyph per command. Six words in a row is a list; six words with marks
/// beside them is something you can find your way around without reading all of
/// it, which is what a strip you pass every time you open the window needs.
const COMMAND_ICONS = {
    manage: `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                  stroke-width="2" stroke-linecap="round">
               <path d="M4 7h8.5"></path><path d="M17.5 7h2.5"></path><circle cx="15" cy="7" r="2.2"></circle>
               <path d="M4 17h2.5"></path><path d="M11.5 17h8.5"></path><circle cx="9" cy="17" r="2.2"></circle>
             </svg>`,
    extend: `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                  stroke-width="2" stroke-linecap="round">
               <circle cx="11" cy="13" r="7.5"></circle><path d="M11 9v4l3 1.8"></path>
               <path d="M19 2.5v4.5"></path><path d="M16.75 4.75h4.5"></path>
             </svg>`,
    pause:  `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                  stroke-width="2.4" stroke-linecap="round">
               <path d="M9.5 6v12"></path><path d="M14.5 6v12"></path>
             </svg>`,
    resume: `<svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor"
                  stroke="currentColor" stroke-width="2" stroke-linejoin="round">
               <path d="M8 5.5v13l11-6.5z"></path>
             </svg>`,
    scan:   `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                  stroke-width="2" stroke-linecap="round">
               <circle cx="11" cy="11" r="7"></circle><path d="M20 20l-3.6-3.6"></path>
               <path d="M11 8v6"></path><path d="M8 11h6"></path>
             </svg>`,
    settings: `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                    stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                 <circle cx="12" cy="12" r="3"></circle>
                 <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"></path>
               </svg>`,
    dashboard: `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                     stroke-width="2" stroke-linecap="round">
                  <path d="M4 20h16"></path><path d="M7 20v-5"></path>
                  <path d="M12 20V7"></path><path d="M17 20v-9"></path>
                </svg>`
};

/// The two that are setup you do once, and the one that hands you over to the
/// browser. Everything not named here is something you came here to do, and
/// keeps its words.
const UTILITY_COMMANDS = new Set(['scan', 'settings', 'dashboard']);

/// Six commands, all the same size, in the same dim grey, wrapping onto a
/// second row: managing what is tracked and granting an app more time carried
/// exactly the weight of Settings, so the whole strip read as something to skim
/// past rather than the controls of the application.
///
/// They are ranked now. The three you would open this window to do keep their
/// names and gain an icon; setup and the dashboard shrink to icon buttons at
/// the far end. Nothing is taken away -- all six still answer to typing, which
/// is what the tooltips and the accessible names are for -- but the row no
/// longer pretends they are equally likely.
function renderCommandBar() {
    const bar = els.commandBar;
    bar.textContent = '';
    bar.hidden = !!query || state.apps.length === 0;
    if (bar.hidden) return;

    const named = document.createElement('div');
    named.className = 'command-group';

    const utility = document.createElement('div');
    utility.className = 'command-group';

    for (const command of state.commands) {
        const minor = UTILITY_COMMANDS.has(command.id);

        const chip = document.createElement('button');
        chip.type = 'button';
        chip.className = minor ? 'command-icon' : 'command-chip';

        // The hint is the only place the cost of a command is written down --
        // that extending wants the PIN, that pausing means half an hour -- and
        // it used to appear only once you had already searched for the thing.
        chip.title = command.hint ? command.title + ' \u2014 ' + command.hint : command.title;
        // Named or not, the name is what a screen reader should read: an icon
        // button with no words is silent otherwise.
        chip.setAttribute('aria-label', command.title);
        chip.innerHTML = COMMAND_ICONS[command.id] || '';

        if (!minor) {
            const words = document.createElement('span');
            words.textContent = command.title;
            chip.appendChild(words);
        }

        // Deciding what this app watches is the one job that exists only in
        // this window; the dashboard does everything else better. So it is the
        // one command lifted onto a surface of its own.
        if (command.id === 'manage') chip.classList.add('lead');

        // Tracking being off is not a preference, it is a gap in the record.
        // The status dot says so at the top of the window; saying it again on
        // the control that undoes it costs nothing and is where you will look.
        if (command.id === 'resume') chip.classList.add('paused');

        chip.addEventListener('click', () => send('run-command', { id: command.id }));
        (minor ? utility : named).appendChild(chip);
    }

    bar.appendChild(named);

    if (utility.children.length) {
        const spacer = document.createElement('span');
        spacer.className = 'chrome-spacer';
        bar.appendChild(spacer);
        bar.appendChild(utility);
    }
}

/// The status pill. It read TRACKING unconditionally, because the host sent a
/// literal true; it is the one place a person looks to know whether their time
/// is being recorded, so it now says which.
function renderStatus() {
    const paused = state.tracking === false;
    els.statusText.textContent = (state.trackingText || (paused ? 'Paused' : 'Tracking')).toUpperCase();
    els.statusDot.classList.toggle('paused', paused);
}

function renderFacets() {
    const available = populatedFacets();

    // ALL plus one other facet is not a choice; it is the same list twice.
    els.facets.hidden = available.length < 3 || !!query;
    els.facets.textContent = '';
    if (els.facets.hidden) return;

    for (const entry of available) {
        const chip = document.createElement('button');
        chip.type = 'button';
        chip.className = 'facet' + (entry.id === facet ? ' on' : '');
        const count = entry.id === 'all' ? state.apps.length : state.apps.filter(entry.match).length;
        chip.textContent = entry.label + ' ' + count;
        // Which filter is on is state, not decoration, so it is announced.
        chip.setAttribute('aria-pressed', entry.id === facet ? 'true' : 'false');
        chip.addEventListener('click', () => { facet = entry.id; active = 0; render(); });
        els.facets.appendChild(chip);
    }
}

function renderAttention() {
    els.attention.textContent = '';
    const attention = state.attention || {};

    const strip = (kind, icon, text, action, onClick) => {
        const el = document.createElement('div');
        el.className = 'attention ' + kind;
        el.innerHTML = icon;

        const words = document.createElement('span');
        words.textContent = text;
        el.appendChild(words);

        const spacer = document.createElement('span');
        spacer.className = 'chrome-spacer';
        el.appendChild(spacer);

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'attention-action';
        button.textContent = action;
        button.addEventListener('click', onClick);
        el.appendChild(button);

        els.attention.appendChild(el);
    };

    if (attention.startupConflict) {
        strip('warn', ICON_WARN,
            attention.startupConflictText || 'Startup is registered to a different copy of FastApp.',
            'FIX', () => send('settings-command', { id: 'fix-startup' }));
    }
    if (attention.updateReady) {
        strip('info', ICON_UPDATE, 'A new version of FastApp is ready to install.',
            'RESTART', () => send('settings-command', { id: 'apply-update' }));
    }
}

function renderNothingAdded() {
    const blank = document.createElement('div');
    blank.className = 'blank';

    const title = document.createElement('span');
    title.className = 'blank-title';
    title.textContent = 'Nothing added yet';

    const body = document.createElement('span');
    body.className = 'blank-body';
    body.textContent =
        'Add the applications you want a hotkey for, or want launched when you log in.';

    const actions = document.createElement('span');
    actions.className = 'blank-actions';
    const button = (text, primary, onClick) => {
        const el = document.createElement('button');
        el.type = 'button';
        el.className = 'btn' + (primary ? ' btn-primary' : '');
        el.textContent = text;
        el.addEventListener('click', onClick);
        actions.appendChild(el);
    };
    button('Scan this PC', true, () => send('run-command', { id: 'scan' }));
    button('Browse files', false, () => send('browse-files'));

    blank.append(title, body, actions);
    els.results.appendChild(blank);
}

/// Which columns this list is worth drawing. A column no app uses is not
/// rendered at all, rather than rendered empty.
function formatMinutes(total) {
    if (total < 60) return `${total}m`;
    const hours = Math.floor(total / 60);
    const minutes = total % 60;
    return minutes ? `${hours}h ${minutes}m` : `${hours}h`;
}

function ordinal(n) {
    // 1st, 2nd, 3rd, 4th -- and 11th/12th/13th, which the naive rule gets wrong.
    const rest = n % 100;
    if (rest >= 11 && rest <= 13) return `${n}th`;
    return n + (['th', 'st', 'nd', 'rd'][n % 10] || 'th');
}

function activeColumns(rows) {
    const items = rows.map(r => r.item);
    return {
        hotkey: items.some(a => a.hotkey),
        startup: items.some(a => a.autoStart),
        limit: items.some(a => a.limitMinutes > 0),
        // An action has no window and accumulates no focus time, so the column
        // would be blank down its whole length.
        today: items.some(a => !a.isAction)
    };
}

function columnHeader(label, columns) {
    const head = document.createElement('div');
    head.className = 'head-row';
    // Column captions, read once as the group's label rather than again per row.
    head.setAttribute('role', 'presentation');

    const name = document.createElement('span');
    name.className = 'label';
    name.textContent = label;
    head.appendChild(name);

    const spacer = document.createElement('span');
    spacer.className = 'head-spacer';
    head.appendChild(spacer);

    const column = (cls, text) => {
        const cell = document.createElement('span');
        cell.className = cls;
        const inner = document.createElement('span');
        inner.className = 'label';
        inner.textContent = text;
        cell.appendChild(inner);
        head.appendChild(cell);
    };

    if (columns.hotkey) column('col-hotkey', 'HOTKEY');
    if (columns.startup) column('col-startup', 'STARTUP');
    if (columns.limit) column('col-limit', 'LIMIT');
    if (columns.today) column('row-figure', 'TODAY');

    const end = document.createElement('span');
    end.className = 'head-end';
    head.appendChild(end);

    return head;
}

function appendGroup(label, rows, startIndex, columns) {
    if (rows.length === 0) return startIndex;

    const group = document.createElement('div');
    group.className = 'group';
    // The heading is already on screen; as a group label it also reaches
    // anyone who cannot see it.
    group.setAttribute('role', 'group');
    group.setAttribute('aria-label', label);
    group.appendChild(columns ? columnHeader(label, columns) : plainHeading(label));

    const list = document.createElement('div');
    list.className = 'group-rows';
    // A wrapper for layout, not a thing in its own right: without this it
    // announces as an empty group between the real one and its options.
    list.setAttribute('role', 'presentation');

    let index = startIndex;
    for (const row of rows) {
        list.appendChild(buildRow(row, index, columns));
        index++;
    }

    group.appendChild(list);
    els.results.appendChild(group);
    return index;
}

function plainHeading(label) {
    const heading = document.createElement('span');
    heading.className = 'label';
    heading.textContent = label;
    return heading;
}

function buildRow(row, index, columns) {
    const el = document.createElement('div');
    el.className = 'row' + (index === active ? ' active' : '');

    // The highlight was a CSS class and nothing else, so a screen reader had no
    // way to know anything was selected -- arrow keys moved something it could
    // not see, and the search box appeared to do nothing at all. The rows are
    // options in a listbox now, and the box below points at the current one.
    el.id = 'opt-' + index;
    el.setAttribute('role', 'option');
    el.setAttribute('aria-selected', index === active ? 'true' : 'false');
    el.addEventListener('mousemove', () => { if (active !== index) { active = index; render(); } });

    // Clicking the row opens it rather than running it. An app row is two
    // things at once -- something to launch and something to configure -- and
    // the row is much the larger target, so the reading with no undo is the one
    // that gets the small explicit button. A command row has nothing to open,
    // so there running stays the click.
    el.addEventListener('click', () => { active = index; primary(row); });

    const avatar = document.createElement('span');
    avatar.className = 'avatar';

    const text = document.createElement('span');
    text.className = 'row-text';

    const name = document.createElement('span');
    name.className = 'row-name';

    if (row.kind === 'trackable') {
        const candidate = row.item;
        avatar.style.background = catTint('Other');
        avatar.textContent = (candidate.name[0] || '?').toUpperCase();
        name.textContent = candidate.name;

        const sub = document.createElement('span');
        sub.className = 'row-sub';
        sub.textContent = `${formatMinutes(candidate.minutes)} tracked`;
        text.append(name, sub);
        el.append(avatar, text);

        const spacer = document.createElement('span');
        spacer.className = 'row-spacer';
        el.appendChild(spacer);

        const add = document.createElement('button');
        add.type = 'button';
        // Same reasoning as the run button: Enter on the highlighted candidate
        // adds it.
        add.tabIndex = -1;
        add.className = 'row-add';
        add.textContent = 'ADD';
        add.setAttribute('aria-label', `Add ${candidate.name} to FastApp`);
        add.addEventListener('click', event => {
            event.stopPropagation();
            send('add-tracked', { text: candidate.name });
        });
        el.appendChild(add);

        return el;
    }

    if (row.kind === 'app') {
        const app = row.item;
        avatar.style.background = catTint(app.category);
        if (app.running) avatar.classList.add('live');
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

        // The row used to show one chip: the hotkey, or "Auto-start" if there
        // was no hotkey. Which meant an app with both simply never mentioned
        // the second one. Each property now has its own column and they all
        // show at once.
        const cols = columns || { hotkey: false, startup: false, limit: false };

        if (cols.hotkey) {
            const cell = document.createElement('span');
            cell.className = 'col-hotkey';
            if (app.hotkey) {
                const chip = document.createElement('span');
                chip.className = 'row-chip';
                chip.textContent = app.hotkey;
                cell.appendChild(chip);

                const uses = document.createElement('span');
                // Under ten in the app's whole lifetime is a binding you have
                // not adopted, which is worth knowing before you blame it.
                uses.className = 'uses' + (app.hotkeyUses < 10 ? ' cold' : '');
                uses.textContent = `${app.hotkeyUses} use${app.hotkeyUses === 1 ? '' : 's'}`;
                cell.appendChild(uses);
            }
            el.appendChild(cell);
        }

        if (cols.startup) {
            const cell = document.createElement('span');
            cell.className = 'col-startup';
            if (app.autoStart && app.startupPosition) {
                const chip = document.createElement('span');
                chip.className = 'row-chip';
                chip.textContent = ordinal(app.startupPosition);
                cell.appendChild(chip);
            }
            el.appendChild(cell);
        }

        if (cols.limit) {
            const cell = document.createElement('span');
            cell.className = 'col-limit';
            if (app.limitMinutes > 0) {
                const left = document.createElement('span');
                const remaining = app.limitRemaining;
                left.className = 'limit' + (remaining <= 0 ? ' out' : remaining <= 15 ? ' low' : '');
                left.textContent = remaining > 0
                    ? `${remaining}m left`
                    : remaining === 0 ? 'none left' : `over by ${-remaining}m`;
                cell.appendChild(left);
            }
            el.appendChild(cell);
        }

        if (cols.today) {
            const figure = document.createElement('span');
            figure.className = 'row-figure';
            figure.textContent = app.today || '';
            el.appendChild(figure);
        }

        el.appendChild(buildRunButton(row));
    } else {
        const command = row.item;
        avatar.style.background = catTint('Other');
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

/// Launch an app, or focus it if it is already running. The row is passed in
/// rather than read from `active`, because the run button belongs to one
/// specific row and must not act on whatever the keyboard last highlighted.
function activate(row) {
    const current = row || visible().all[active];
    if (!current) return;

    if (current.kind === 'app') send('activate-app', { id: current.item.id });
    else if (current.kind === 'trackable') send('add-tracked', { text: current.item.name });
    else send('run-command', { id: current.item.id });
}

/// What clicking the row itself does: open an app, run a command.
function primary(row) {
    const current = row || visible().all[active];
    if (!current) return;

    // A candidate has nothing to open yet, so both the row and its button do
    // the only thing available: add it.
    if (current.kind === 'app') send('edit-app', { id: current.item.id });
    else if (current.kind === 'trackable') send('add-tracked', { text: current.item.name });
    else send('run-command', { id: current.item.id });
}

// A triangle to start something, an arrow out of a box to go to something
// already started, so the control says which of the two is about to happen.
function runIcon(running, size) {
    return running
        ? `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
             <path d="M14 4h6v6"></path><path d="M20 4l-8 8"></path>
             <path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"></path>
           </svg>`
        : `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="currentColor"
                stroke="currentColor" stroke-width="2" stroke-linejoin="round">
             <path d="M8 5.5v13l11-6.5z"></path>
           </svg>`;
}

function buildRunButton(row) {
    const app = row.item;
    const running = !!app.running;

    const button = document.createElement('button');
    button.type = 'button';
    // Out of the tab order deliberately, not by omission: Enter on the
    // highlighted row already launches it, so eight of these in the traversal
    // would be eight stops that do what the previous key did. Still a real
    // button, so it is clickable, announced, and operable once a screen reader
    // reaches it directly.
    button.tabIndex = -1;
    button.className = 'row-run' + (running ? ' running' : '');
    button.title = running ? `Focus ${app.name}` : `Launch ${app.name}`;
    button.setAttribute('aria-label', button.title);
    button.innerHTML = runIcon(running, 14);

    // Without this the row's own handler fires straight afterwards and opens
    // the panel on top of the app that was just launched.
    button.addEventListener('click', event => {
        event.stopPropagation();
        activate(row);
    });

    return button;
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

/* ---------------------------------------------------------------------------
   Views

   One document rather than separate pages: the palette is summoned constantly
   and must not pay for a navigation, and the detail view wants the same bridge
   and the same stylesheet. The host is told to resize when the view changes.

   The editing views used to suppress dismiss-on-click-away, on the theory that
   a stray click would lose half-typed changes. It could not: every field saves
   on change, and clicking away blurs the field, which is what fires it. So the
   protection guarded nothing and cost the behaviour people expect from a
   palette -- click elsewhere and it goes.
   --------------------------------------------------------------------------- */

const VIEWS = {
    palette: { el: document.getElementById('view-palette'), w: 820, h: 560 },
    detail: { el: document.getElementById('view-detail'), w: 820, h: 560 },
    manage: { el: document.getElementById('view-manage'), w: 940, h: 620 },
    settings: { el: document.getElementById('view-settings'), w: 940, h: 700 },
    scanner: { el: document.getElementById('view-scanner'), w: 880, h: 640 },
    extend: { el: document.getElementById('view-extend'), w: 620, h: 470 }
};

let view = 'palette';

function show(name) {
    view = name;
    for (const [key, v] of Object.entries(VIEWS)) v.el.hidden = key !== name;

    const target = VIEWS[name];
    // The view goes with the size: the host has no other way to know which of
    // these is on screen, and it needs to know so it can keep Settings up to
    // date while something slow is running behind it.
    send('resize', { width: target.w, height: target.h, view: name });
    // Leaving and returning re-measures: the list may have changed while away.
    if (name !== 'palette') sentHeight = 0;

    if (name === 'palette') els.q.focus();
}

document.addEventListener('keydown', e => {
    if (view === 'scanner') {
        switch (e.key) {
            case 'Escape': e.preventDefault(); show('palette'); break;
            case 'ArrowDown': e.preventDefault(); moveScan(1); break;
            case 'ArrowUp': e.preventDefault(); moveScan(-1); break;
            case ' ': e.preventDefault(); toggleScanPick(); break;
            case 'Enter': e.preventDefault(); addScanned(); break;
        }
        return;
    }

    if (view === 'settings') {
        if (e.key === 'Escape') { e.preventDefault(); show('palette'); }
        return;
    }

    if (view === 'extend') {
        if (e.key === 'Escape') { e.preventDefault(); show('palette'); }
        // Enter anywhere in the form submits it, so the PIN can be typed and
        // confirmed without reaching for the mouse.
        if (e.key === 'Enter') { e.preventDefault(); grantExtension(); }
        return;
    }

    if (view === 'manage') {
        switch (e.key) {
            case 'Escape': e.preventDefault(); show('palette'); break;
            case 'ArrowDown':
            case 'ArrowUp': {
                e.preventDefault();
                const delta = e.key === 'ArrowDown' ? 1 : -1;
                // Alt turns navigation into reordering, which is also the
                // startup order, so the two live on the same keys deliberately.
                if (e.altKey) {
                    const entry = state.apps[manageActive];
                    if (entry) {
                        send('reorder-app', { id: entry.id, delta });
                        manageActive = Math.min(Math.max(manageActive + delta, 0), state.apps.length - 1);
                    }
                } else {
                    manageActive = Math.min(Math.max(manageActive + delta, 0), state.apps.length - 1);
                    renderManage();
                }
                break;
            }
            case 'Enter': {
                e.preventDefault();
                const entry = state.apps[manageActive];
                if (entry) send('edit-app', { id: entry.id });
                break;
            }
        }
        return;
    }

    if (view === 'palette' && e.key === 'Escape'
        && document.activeElement && document.activeElement !== els.q
        && document.activeElement !== document.body) {
        // Tab must not be a one-way trip: Escape from a focused control puts
        // the caret back in the search box rather than closing the window.
        e.preventDefault();
        focusSearch();
        return;
    }

    if (view === 'detail') {
        if (e.key === 'Escape') {
            e.preventDefault();
            if (capturing) { stopCapture(); return; }
            show('palette');
        }
        return;
    }

    switch (e.key) {
        case 'ArrowDown':
        case 'ArrowUp': {
            e.preventDefault();
            const delta = e.key === 'ArrowDown' ? 1 : -1;

            // Alt turns navigation into reordering, the same as in Manage, and
            // for the same reason: this order is the auto-launch sequence, so
            // being able to change it where you can see it is the point of
            // showing it here at all. Only on the plain list -- reordering a
            // filtered or searched view would move things you cannot see.
            const entry = visible().all[active];
            if (e.altKey && !query && facet === 'all' && entry && entry.kind === 'app') {
                send('reorder-app', { id: entry.item.id, delta });
                active = Math.min(Math.max(active + delta, 0), visible().all.length - 1);
                break;
            }

            move(delta);
            break;
        }
        case 'Enter': e.preventDefault(); activate(); break;
        case 'Escape': e.preventDefault(); send('close'); break;

        // Tab used to open the highlighted app, which meant Tab never moved
        // focus and every control that was not the search box was unreachable
        // by keyboard. Drilling in moved to the right arrow, which is what a
        // list does anyway, and Tab went back to being Tab.
        case 'ArrowRight': {
            e.preventDefault();
            const current = visible().all[active];
            if (current && current.kind === 'app') send('edit-app', { id: current.item.id });
            break;
        }
    }
});

/* ---------------------------------------------------------------------------
   Extend time

   Granting extra minutes needs the PIN, so this is the one view that can fail
   on correct-looking input. Everything it says goes in one line under the PIN
   box, which holds its height whether or not it is saying anything -- a message
   that appears and pushes the button down is a message you click through.
   --------------------------------------------------------------------------- */
const x = {
    app: document.getElementById('x-app'),
    usage: document.getElementById('x-usage'),
    minutes: document.getElementById('x-minutes'),
    pin: document.getElementById('x-pin'),
    message: document.getElementById('x-message'),
    grant: document.getElementById('x-grant'),
    count: document.getElementById('x-count'),
    form: document.getElementById('x-form'),
    empty: document.getElementById('x-empty'),
    emptyText: document.getElementById('x-empty-text')
};

const MINUTE_CHOICES = [10, 15, 30, 60];
let extendState = { apps: [], hasPin: false };
let extendMinutes = 15;

function renderExtend() {
    const apps = extendState.apps || [];
    x.count.textContent = apps.length;

    // Two ways to have nothing to do here, and they need different answers:
    // no limits set, or limits set but no PIN to authorise lifting them.
    const blocked = apps.length === 0 || !extendState.hasPin;
    x.form.hidden = blocked;
    x.empty.hidden = !blocked;
    x.grant.disabled = blocked;

    if (blocked) {
        x.emptyText.textContent = apps.length === 0
            ? 'No app has a daily limit set. Give one a limit in its details first.'
            : 'A PIN has to be set before extra time can be granted. Settings has one.';
        return;
    }

    // Rebuilding the options throws the selection away, so it is put back.
    // Without this, picking a different duration silently reset the app to the
    // first in the list and granted the time to the wrong one.
    const chosen = x.app.value;
    x.app.textContent = '';
    for (const app of apps) {
        const option = document.createElement('option');
        option.value = app.id;
        option.textContent = app.name;
        x.app.appendChild(option);
    }
    if (apps.some(a => String(a.id) === String(chosen))) x.app.value = chosen;

    renderExtendMinutes();
}

// Split out because choosing a duration must not touch the app list: see above.
function renderExtendMinutes() {
    x.minutes.textContent = '';
    for (const minutes of MINUTE_CHOICES) {
        const pill = document.createElement('span');
        pill.className = 'x-minute' + (minutes === extendMinutes ? ' picked' : '');
        pill.textContent = `${minutes} min`;
        pill.addEventListener('click', () => { extendMinutes = minutes; renderExtendMinutes(); });
        x.minutes.appendChild(pill);
    }

    renderExtendUsage();
    x.grant.textContent = `Grant ${extendMinutes} minutes`;
}

function renderExtendUsage() {
    const app = (extendState.apps || []).find(a => String(a.id) === String(x.app.value));
    if (!app) { x.usage.textContent = ''; return; }

    const bonus = app.bonusToday ? ` · ${app.bonusToday}m already granted today` : '';
    x.usage.textContent = `${app.usedToday} of ${app.limitMinutes} minutes used today${bonus}`;
}

function setExtendMessage(text, kind) {
    x.message.textContent = text || '';
    x.message.className = 'field-label x-message' + (kind ? ' ' + kind : '');
}

function grantExtension() {
    if (x.grant.disabled) return;

    const id = x.app.value;
    const pin = x.pin.value;
    if (!id) return;
    if (!pin) { setExtendMessage('Enter your PIN.', 'bad'); x.pin.focus(); return; }

    setExtendMessage('Checking…');
    send('extend-grant', { id, minutes: extendMinutes, pin });
}

x.app.addEventListener('change', renderExtendUsage);
x.grant.addEventListener('click', grantExtension);
x.pin.addEventListener('input', () => setExtendMessage(''));
document.querySelector('[data-back-extend]').addEventListener('click', () => show('palette'));

/* ---------------------------------------------------------------------------
   Scanner

   The host owns the scan; this only chooses from it. Selections are held as
   paths rather than positions, so a re-scan cannot silently move the choice
   onto a different application.
   --------------------------------------------------------------------------- */

let scanApps = [];
let scanning = false;
let scanActive = 0;
let scanFilter = '';
const scanPicked = new Set();

const sc = {
    list: document.getElementById('sc-list'),
    filter: document.getElementById('sc-filter'),
    count: document.getElementById('sc-count'),
    subtitle: document.getElementById('sc-subtitle'),
    add: document.getElementById('sc-add'),
    none: document.getElementById('sc-none')
};

function scanVisible() {
    const q = scanFilter.trim().toLowerCase();
    return q ? scanApps.filter(a => score(a.name, q) >= 0) : scanApps;
}

function renderScan() {
    const rows = scanVisible();
    if (scanActive >= rows.length) scanActive = Math.max(0, rows.length - 1);

    sc.list.textContent = '';

    if (scanning) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = 'Looking through your Start menu and the Microsoft Store…';
        sc.list.appendChild(empty);
    } else if (rows.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty';
        empty.textContent = scanFilter
            ? `Nothing found matching "${scanFilter}"`
            : 'Nothing new found. Everything discovered is already managed.';
        sc.list.appendChild(empty);
    } else {
        rows.forEach((app, index) => sc.list.appendChild(buildScanRow(app, index)));
    }

    sc.count.textContent = String(scanPicked.size);
    sc.add.disabled = scanPicked.size === 0;
    sc.add.textContent = scanPicked.size === 0
        ? 'Add'
        : `Add ${scanPicked.size} application${scanPicked.size === 1 ? '' : 's'}`;

    sc.subtitle.textContent = scanning
        ? 'Scanning…'
        : `${scanApps.length} found that you are not already managing`;

    const active = sc.list.querySelector('.sc-row.active');
    if (active) active.scrollIntoView({ block: 'nearest' });
}

function buildScanRow(app, index) {
    const row = document.createElement('div');
    row.className = 'sc-row'
        + (index === scanActive ? ' active' : '')
        + (scanPicked.has(app.path) ? ' picked' : '');
    row.addEventListener('click', () => { scanActive = index; toggleScanPick(); });

    const tick = document.createElement('span');
    tick.className = 'sc-tick';
    if (scanPicked.has(app.path)) {
        const mark = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        mark.setAttribute('width', '12');
        mark.setAttribute('height', '12');
        mark.setAttribute('viewBox', '0 0 24 24');
        mark.setAttribute('fill', 'none');
        mark.setAttribute('stroke', 'currentColor');
        mark.setAttribute('stroke-width', '3');
        mark.setAttribute('stroke-linecap', 'round');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', 'M5 12l5 5L19 7');
        mark.appendChild(path);
        tick.appendChild(mark);
    }

    const avatar = document.createElement('span');
    avatar.className = 'avatar';
    avatar.style.width = '30px';
    avatar.style.height = '30px';
    avatar.style.background = 'var(--panel)';
    avatar.textContent = (app.name[0] || '?').toUpperCase();

    const text = document.createElement('span');
    text.className = 'sc-text';
    const name = document.createElement('span');
    name.className = 'sc-name';
    name.textContent = app.name;
    const path = document.createElement('span');
    path.className = 'sc-path';
    path.textContent = app.path;
    text.append(name, path);

    const spacer = document.createElement('span');
    spacer.className = 'row-spacer';

    const source = document.createElement('span');
    source.className = 'sc-source' + (app.packaged ? ' store' : '');
    source.textContent = app.packaged ? 'STORE' : 'START MENU';

    row.append(tick, avatar, text, spacer, source);
    return row;
}

function moveScan(delta) {
    const total = scanVisible().length;
    if (total === 0) return;
    scanActive = Math.min(Math.max(scanActive + delta, 0), total - 1);
    renderScan();
}

function toggleScanPick() {
    const app = scanVisible()[scanActive];
    if (!app) return;
    if (scanPicked.has(app.path)) scanPicked.delete(app.path);
    else scanPicked.add(app.path);
    renderScan();
}

function addScanned() {
    if (scanPicked.size === 0) return;
    send('add-scanned', { paths: [...scanPicked] });
    scanPicked.clear();
    renderScan();
}

sc.filter.addEventListener('input', () => { scanFilter = sc.filter.value; scanActive = 0; renderScan(); });
sc.add.addEventListener('click', addScanned);
sc.none.addEventListener('click', () => { scanPicked.clear(); renderScan(); });
document.querySelector('[data-back-scanner]').addEventListener('click', () => show('palette'));

/* ---------------------------------------------------------------------------
   Settings

   Nothing here is stored locally. Every change is sent to the host, which sets
   the matching view-model property so the persistence and side effects the WPF
   settings tab relies on all still happen, and the host then sends the whole
   settings state back. Some of it is answered asynchronously -- the startup
   toggle waits on a UAC prompt -- so the page always renders what came back
   rather than what it just sent.
   --------------------------------------------------------------------------- */

const st = {
    version: document.getElementById('s-version'),
    versionLine: document.getElementById('s-version-line'),
    conflict: document.getElementById('s-conflict'),
    conflictPath: document.getElementById('s-conflict-path'),
    fix: document.getElementById('s-fix'),
    startup: document.getElementById('s-startup'),
    osd: document.getElementById('s-osd'),
    progress: document.getElementById('s-progress'),
    notify: document.getElementById('s-notify'),
    quiet: document.getElementById('s-quiet'),
    quietTimes: document.getElementById('s-quiet-times'),
    quietFrom: document.getElementById('s-quiet-from'),
    quietTo: document.getElementById('s-quiet-to'),
    dashboardStatus: document.getElementById('s-dashboard-status'),
    openDashboard: document.getElementById('s-open-dashboard'),
    updateStatus: document.getElementById('s-update-status'),
    check: document.getElementById('s-check'),
    apply: document.getElementById('s-apply'),
    whatsNewCard: document.getElementById('s-whatsnew-card'),
    whatsNewLabel: document.getElementById('s-whatsnew-label'),
    whatsNew: document.getElementById('s-whatsnew'),
    rollbackCard: document.getElementById('s-rollback-card'),
    rollbackVersion: document.getElementById('s-rollback-version'),
    rollback: document.getElementById('s-rollback'),
    rollbackWarning: document.getElementById('s-rollback-warning'),
    rollbackStatus: document.getElementById('s-rollback-status'),
    whatsNewEmpty: document.getElementById('s-whatsnew-empty'),
    rollbackRow: document.getElementById('s-rollback-row'),
    rollbackEmpty: document.getElementById('s-rollback-empty'),
    theme: document.getElementById('s-theme'),
    themeHint: document.getElementById('s-theme-hint')
};

function renderSettings(v) {
    st.version.textContent = v.version || '';
    st.versionLine.textContent = v.version || 'FastApp';

    st.conflict.hidden = !v.hasStartupConflict;
    st.conflictPath.textContent = (v.startupConflictText || '').split('\n').pop();

    setToggle(st.startup, v.launchOnStartup);
    setToggle(st.osd, v.enableOsd);
    setToggle(st.progress, v.showAutoLaunchProgress);
    setToggle(st.notify, v.notificationsEnabled);
    setToggle(st.quiet, v.quietHoursEnabled);

    st.quietTimes.hidden = !v.quietHoursEnabled;
    if (document.activeElement !== st.quietFrom) st.quietFrom.value = v.quietHoursFrom || '';
    if (document.activeElement !== st.quietTo) st.quietTo.value = v.quietHoursTo || '';

    st.dashboardStatus.textContent = v.dashboardStatus || '';
    st.openDashboard.disabled = !v.dashboardRunning;

    st.updateStatus.textContent = v.updateStatus || '';
    // Disabled while it runs, so the button itself says something is happening
    // rather than leaving that entirely to a line of grey text beside it.
    st.check.textContent = v.checkingForUpdates ? 'Checking…' : 'Check now';
    st.check.disabled = !!v.checkingForUpdates;
    st.apply.hidden = !v.updateReady;

    // Both of the cards below stay on screen with nothing to show. A section
    // that vanishes entirely reads as a bug or a missing feature; one that
    // explains why it is empty answers the question instead.
    const devBuild = /dev/i.test(v.version || '');

    st.whatsNewLabel.textContent =
        v.hasWhatsNew && v.version ? `WHAT'S NEW IN ${v.version}` : "WHAT'S NEW";
    renderNotes(v.hasWhatsNew ? (v.whatsNew || '') : '');
    st.whatsNewEmpty.hidden = v.hasWhatsNew;
    st.whatsNewEmpty.textContent = devBuild
        ? 'Development builds carry no release notes. They appear here for installed versions.'
        : 'No notes were recorded for this version.';

    st.rollbackRow.hidden = !v.hasRollbackVersions;
    st.rollbackEmpty.hidden = v.hasRollbackVersions;
    st.rollbackEmpty.textContent = devBuild
        ? 'Not available on a development build, which Windows has no installed history for.'
        : 'No earlier version is installed yet. After your first update, the version you came from can be reinstalled here.';

    if (v.rollbackVersions && st.rollbackVersion.options.length !== v.rollbackVersions.length) {
        st.rollbackVersion.textContent = '';
        for (const version of v.rollbackVersions) {
            const option = document.createElement('option');
            option.value = version;
            option.textContent = version;
            st.rollbackVersion.appendChild(option);
        }
    }
    if (v.selectedRollback) st.rollbackVersion.value = v.selectedRollback;
    st.rollbackWarning.hidden = !v.rollbackWarning;
    st.rollbackWarning.textContent = v.rollbackWarning || '';
    st.rollbackStatus.hidden = !v.rollbackStatus;
    st.rollbackStatus.textContent = v.rollbackStatus || '';
    st.rollback.textContent = v.rollbackBusy ? 'Working…' : 'Reinstall';
    st.rollback.disabled = !!v.rollbackBusy;

    renderTheme(v.themePreference, v.systemIsLight);
}

/* The release body, rendered with the shape it was written in.
   Text nodes and elements, never innerHTML: this is a GitHub release body,
   ours to display and not ours to trust as markup. */

/// Inline spans within one line. Links keep their words and lose their target,
/// because there is nowhere in this window to follow one to -- "Every version"
/// in the card header is the way out to the dashboard.
const INLINE = /\*\*([^*]+)\*\*|`([^`]+)`|(?<![\w*])\*([^*]+)\*(?![\w*])/g;

function inline(el, text) {
    const rest = String(text).replace(/\[([^\]]+)\]\([^)]+\)/g, '$1');

    INLINE.lastIndex = 0;
    let at = 0;
    let match;
    while ((match = INLINE.exec(rest)) !== null) {
        if (match.index > at) {
            el.appendChild(document.createTextNode(rest.slice(at, match.index)));
        }
        const tag = match[1] ? 'strong' : match[2] ? 'code' : 'em';
        const span = document.createElement(tag);
        span.textContent = match[1] || match[2] || match[3];
        el.appendChild(span);
        at = INLINE.lastIndex;
    }
    if (at < rest.length) el.appendChild(document.createTextNode(rest.slice(at)));
}

/// Headings were arriving as ordinary sentences, bold as plain text, and the
/// bullet branch below could never run: the host had already replaced the
/// leading "-" with a literal dot, so nothing here started with one and the
/// brass dot was unreachable code. Four sections of notes read as one
/// undifferentiated block, and stopped mid-sentence on an ellipsis.
function renderNotes(text) {
    st.whatsNew.textContent = '';

    for (const raw of String(text || '').replace(/\r\n/g, '\n').split('\n')) {
        const line = raw.trim();
        if (!line) continue;
        if (/^(-{3,}|\*{3,}|_{3,})$/.test(line)) continue;   // horizontal rule
        if (line.startsWith('|')) continue;                  // table row

        const heading = line.match(/^#{1,6}\s+(.*)$/);
        if (heading) {
            const head = document.createElement('div');
            head.className = 's-note-head';
            inline(head, heading[1]);
            st.whatsNew.appendChild(head);
            continue;
        }

        const bullet = line.match(/^[-*+]\s+(.*)$/);

        const note = document.createElement('div');
        note.className = 's-note';

        if (bullet) {
            const dot = document.createElement('span');
            dot.className = 's-note-dot';
            note.appendChild(dot);
        }

        const body = document.createElement('span');
        // The opening sentence, before anything else, is the release in one
        // line -- the one part of this worth reading at a glance.
        body.className = 's-note-text'
            + (!bullet && st.whatsNew.children.length === 0 ? ' s-note-lead' : '');
        inline(body, bullet ? bullet[1] : line);

        note.appendChild(body);
        st.whatsNew.appendChild(note);
    }
}

/// The host decides the theme, because the OS is no longer the only thing that
/// can set it. Dark is stamped as explicitly as light: leaving the attribute
/// off would hand the page back to prefers-color-scheme, which is exactly the
/// answer someone choosing Dark on a light machine has said no to.
function applyTheme(theme) {
    const light = theme === 'light';
    document.documentElement.setAttribute('data-theme', light ? 'light' : 'dark');
    // Form controls, scrollbars and the caret are drawn by the browser from
    // this rather than from our tokens.
    document.documentElement.style.colorScheme = light ? 'light' : 'dark';
}

const THEME_HINTS = {
    system: light => `Following Windows, which is set to ${light ? 'light' : 'dark'}.`,
    dark: () => 'Always dark, whatever Windows is set to.',
    light: () => 'Always light, whatever Windows is set to.'
};

/// Remembered rather than re-derived, because a click knows which preference
/// was picked and nothing about Windows -- and "Following Windows, which is set
/// to ..." has to keep telling the truth between the click and the host's reply.
let windowsIsLight = false;

function renderTheme(preference, systemLight) {
    if (systemLight !== undefined) windowsIsLight = !!systemLight;

    const chosen = THEME_HINTS[preference] ? preference : 'system';
    for (const pill of st.theme.querySelectorAll('[data-theme-pref]')) {
        pill.setAttribute('aria-pressed', pill.dataset.themePref === chosen ? 'true' : 'false');
    }
    st.themeHint.textContent = THEME_HINTS[chosen](windowsIsLight);
}

st.theme.addEventListener('click', event => {
    const pill = event.target.closest('[data-theme-pref]');
    if (!pill) return;

    // Optimistic, like the toggles: the host answers with a settings push and
    // a theme message, and both overwrite this.
    renderTheme(pill.dataset.themePref);
    settingText('theme', pill.dataset.themePref);
});

function setting(key, value) { send('set-setting', { key, value }); }
function settingText(key, text) { send('set-setting', { key, text }); }

for (const [el, key] of [
    [st.startup, 'launchOnStartup'],
    [st.osd, 'enableOsd'],
    [st.progress, 'showAutoLaunchProgress'],
    [st.notify, 'notificationsEnabled'],
    [st.quiet, 'quietHoursEnabled']
]) {
    el.addEventListener('click', () => {
        const next = !toggleOn(el);
        setToggle(el, next);          // optimistic; the host's reply is the truth
        setting(key, next);
    });
}

st.quietFrom.addEventListener('change', () => settingText('quietHoursFrom', st.quietFrom.value));
st.quietTo.addEventListener('change', () => settingText('quietHoursTo', st.quietTo.value));
st.rollbackVersion.addEventListener('change', () => settingText('selectedRollback', st.rollbackVersion.value));

st.fix.addEventListener('click', () => send('settings-command', { id: 'fix-startup' }));
st.check.addEventListener('click', () => send('settings-command', { id: 'check-updates' }));
st.apply.addEventListener('click', () => send('settings-command', { id: 'apply-update' }));
st.rollback.addEventListener('click', () => send('settings-command', { id: 'rollback' }));
st.openDashboard.addEventListener('click', () => send('settings-command', { id: 'open-dashboard' }));
document.querySelector('[data-back-settings]').addEventListener('click', () => show('palette'));

/* ---------------------------------------------------------------------------
   Manage view
   --------------------------------------------------------------------------- */

let manageActive = 0;
const mList = document.getElementById('m-list');
const mCounts = document.getElementById('m-counts');

function renderManage() {
    mList.textContent = '';

    state.apps.forEach((app, index) => {
        const row = document.createElement('div');
        row.className = 'm-row' + (index === manageActive ? ' active' : '');
        row.addEventListener('click', () => { manageActive = index; renderManage(); });
        row.addEventListener('dblclick', () => send('edit-app', { id: app.id }));

        const avatar = document.createElement('span');
        avatar.className = 'avatar';
        avatar.style.width = '30px';
        avatar.style.height = '30px';
        avatar.style.background = catTint(app.category);
        avatar.textContent = (app.name[0] || '?').toUpperCase();

        const name = document.createElement('span');
        name.className = 'm-name';
        name.textContent = app.name;

        const hotkey = document.createElement('span');
        hotkey.className = app.hotkey ? 'row-chip' : 'm-meta';
        hotkey.textContent = app.hotkey || '—';
        if (app.hotkey) hotkey.style.justifySelf = 'start';

        const limit = document.createElement('span');
        limit.className = 'm-meta';
        limit.textContent = app.limitMinutes ? app.limitMinutes + 'm' : '—';

        const auto = document.createElement('span');
        auto.className = 'm-dot' + (app.autoStart ? '' : ' off');

        const figure = document.createElement('span');
        figure.className = 'm-figure';
        figure.textContent = app.today || '—';

        row.append(avatar, name, hotkey, limit, auto, figure);
        mList.appendChild(row);
    });

    const withHotkeys = state.apps.filter(a => a.hotkey).length;
    const withAuto = state.apps.filter(a => a.autoStart).length;
    mCounts.textContent =
        `${state.apps.length} ENTRIES · ${withHotkeys} HOTKEYS · ${withAuto} AUTO-START`;

    const active = mList.querySelector('.m-row.active');
    if (active) active.scrollIntoView({ block: 'nearest' });
}

document.getElementById('m-new-action').addEventListener('click', () => send('new-action'));
document.getElementById('m-browse').addEventListener('click', () => send('browse-files'));
document.getElementById('m-scan').addEventListener('click', () => send('open-scanner'));
document.querySelector('[data-back-manage]').addEventListener('click', () => show('palette'));

/* ---------------------------------------------------------------------------
   Detail view
   --------------------------------------------------------------------------- */

const d = {
    avatar: document.getElementById('d-avatar'),
    name: document.getElementById('d-name'),
    category: document.getElementById('d-category'),
    today: document.getElementById('d-today'),
    triggers: document.getElementById('d-triggers'),
    customName: document.getElementById('d-custom-name'),
    path: document.getElementById('d-path'),
    hotkey: document.getElementById('d-hotkey'),
    hotkeyClear: document.getElementById('d-hotkey-clear'),
    suppress: document.getElementById('d-suppress'),
    autostart: document.getElementById('d-autostart'),
    args: document.getElementById('d-args'),
    delay: document.getElementById('d-delay'),
    limit: document.getElementById('d-limit'),
    force: document.getElementById('d-force'),
    limitCard: document.getElementById('d-limit-card'),
    limitOn: document.getElementById('d-limit-on'),
    limitBody: document.getElementById('d-limit-body'),
    limitSummary: document.getElementById('d-limit-summary'),
    limitSaveRow: document.getElementById('d-limit-save-row'),
    limitPin: document.getElementById('d-limit-pin'),
    limitSave: document.getElementById('d-limit-save'),
    limitMessage: document.getElementById('d-limit-message'),
    locked: document.getElementById('d-locked'),
    limitNote: document.getElementById('d-limit-note'),
    limitLink: document.getElementById('d-limit-link'),
    remove: document.getElementById('d-delete'),
    actionCard: document.getElementById('d-action-card'),
    actionHint: document.getElementById('d-action-hint'),
    payloadCard: document.getElementById('d-payload-card'),
    payload: document.getElementById('d-payload'),
    startupCard: document.getElementById('d-startup-card'),
    execRow: document.getElementById('d-exec-row'),
    run: document.getElementById('d-run'),
    dashboard: document.getElementById('d-dashboard'),
    runIcon: document.getElementById('d-run-icon'),
    runLabel: document.getElementById('d-run-label')
};

d.run.addEventListener('click', () => {
    if (editing) send('activate-app', { id: editing.id });
});

// This panel holds what you can change about an app; the dashboard holds what
// it has done. Sending the tracked name rather than the display name, because
// that is the key the history is filed under.
d.dashboard.addEventListener('click', () => {
    if (editing) send('open-dashboard-app', { text: editing.name });
});

// Same destination, one tab further in: the limits editor for this app, which
// is the only place a PIN-locked limit can actually be changed.
d.limitLink.addEventListener('click', () => {
    if (editing) send('open-dashboard-app', { text: editing.name, id: 'limits' });
});

document.getElementById('s-all-notes').addEventListener('click', () =>
    send('settings-command', { id: 'open-release-notes' }));

const ACTION_HINTS = {
    1: 'Mutes or unmutes the system volume. Nothing else to configure.',
    2: 'Centres the window you are currently in, on the screen it is on.',
    3: 'Pastes the text below, then puts your clipboard back as it was.'
};

let editing = null;
let capturing = false;

function setToggle(el, on) { el.setAttribute('aria-checked', on ? 'true' : 'false'); }
function toggleOn(el) { return el.getAttribute('aria-checked') === 'true'; }

function renderDetail(app) {
    editing = app;
    limitDirty = false;
    d.limitMessage.textContent = '';
    d.limitMessage.classList.remove('good', 'bad');
    d.limitPin.value = '';
    d.limitSave.disabled = false;

    d.avatar.textContent = (app.displayName[0] || '?').toUpperCase();
    d.avatar.style.background = catTint(app.category);
    d.name.textContent = app.displayName;
    d.category.textContent = app.category || 'Other';
    d.today.textContent = app.today || '0m';
    d.triggers.textContent = app.triggerCount;

    // An action has no window to go back to, so it only ever runs.
    const running = !!app.running;
    d.run.className = 'run-btn' + (running ? ' running' : '');
    d.runIcon.innerHTML = runIcon(running, 15);
    d.runLabel.textContent = app.isAction ? 'RUN' : running ? 'FOCUS' : 'LAUNCH';
    d.run.title = `${d.runLabel.textContent[0]}${d.runLabel.textContent.slice(1).toLowerCase()} ${app.displayName}`;
    d.run.setAttribute('aria-label', d.run.title);

    // Nothing tracks an action's time, so there is no history page for one.
    d.dashboard.hidden = !!app.isAction;

    d.customName.value = app.customName || '';
    d.path.textContent = app.packaged
        ? 'Microsoft Store app'
        : (app.executablePath || 'No executable');

    // An action has no executable, nothing to auto-start and no time to limit;
    // showing those fields greyed would only invite the question of why.
    d.actionCard.hidden = !app.isAction;
    d.payloadCard.hidden = !(app.isAction && app.actionType === 3);

    // The mirror of the above: an action has no executable to show, nothing to
    // open at login and no running time to limit, so those cards go entirely
    // rather than sitting there greyed out inviting the question of why.
    d.startupCard.hidden = app.isAction;
    d.limitCard.hidden = app.isAction;
    d.execRow.hidden = app.isAction;
    d.payload.value = app.actionPayload || '';
    for (const pill of d.actionCard.querySelectorAll('.pill')) {
        pill.setAttribute('aria-pressed',
            Number(pill.dataset.action) === app.actionType ? 'true' : 'false');
    }
    d.actionHint.textContent = ACTION_HINTS[app.actionType] || '';

    d.hotkey.textContent = app.hotkeySequence ? app.hotkeyDisplay : 'None';
    setToggle(d.suppress, app.suppressHotkeyPassthrough);
    setToggle(d.autostart, app.launchOnStartup);
    d.args.value = app.launchArguments || '';
    d.delay.value = app.launchDelaySeconds ? String(app.launchDelaySeconds) : '';
    const limited = app.dailyLimitMinutes > 0;
    setToggle(d.limitOn, limited);
    d.limit.value = app.dailyLimitMinutes ? String(app.dailyLimitMinutes) : '';
    setToggle(d.force, app.strictFocusMode);
    renderLimit();

    // The PIN gates limits wherever they are edited from; this surface must not
    // become a way around it.
    d.locked.hidden = !app.limitsLocked;
    d.limitNote.hidden = !app.limitsLocked;
    d.limitLink.hidden = !app.limitsLocked;

    show('detail');
}

/* ---------------------------------------------------------------------------
   The daily limit

   This used to be two controls the PIN turned to stone, plus a link to the
   dashboard, where the same change could be made against the same database in
   the same process -- reached by opening a browser. Which is a poor answer when
   the app being limited is the browser.

   So the change is made here now. The fields are never disabled; the PIN is
   asked for at the point of saving, which is also where the dashboard asks. The
   host verifies it and refuses on its own account, so nothing here is load
   bearing for the lock.
   --------------------------------------------------------------------------- */

/// Nothing is written until Save when a PIN is set, so the panel has to
/// remember whether what is on screen still matches what is stored.
let limitDirty = false;

function limitOnNow() { return toggleOn(d.limitOn); }
function limitMinutesNow() { return limitOnNow() ? (parseInt(d.limit.value, 10) || 0) : 0; }

function renderLimit() {
    const on = limitOnNow();
    d.limitBody.hidden = !on;

    d.limitSummary.textContent = !on
        ? 'No limit. This app can run all day.'
        : limitMinutesNow() > 0
            ? `${limitMinutesNow()} minutes a day, then ${toggleOn(d.force) ? 'it closes' : 'you are warned'}.`
            : 'Set how many minutes a day.';

    // The save row is the PIN path only. Without a PIN every edit here saves
    // itself, the same as every other field in this panel.
    const locked = !!(editing && editing.limitsLocked);
    const wasHidden = d.limitSaveRow.hidden;
    d.limitSaveRow.hidden = !(locked && limitDirty);
    if (d.limitSaveRow.hidden) d.limitPin.value = '';

    // The panel scrolls, and turning the switch on adds two rows above this
    // one. Only on the way in, or every keystroke in the minutes field would
    // drag the card around.
    else if (wasHidden) d.limitSaveRow.scrollIntoView({ block: 'nearest' });
}

/// Called by every control in the card. With no PIN this is the ordinary
/// save-as-you-go the rest of the panel does; with one it only arms the button.
function limitChanged() {
    d.limitMessage.textContent = '';
    d.limitMessage.classList.remove('good', 'bad');

    if (editing && editing.limitsLocked) limitDirty = true;
    renderLimit();

    if (!(editing && editing.limitsLocked)) saveDetail();
}

d.limitOn.addEventListener('click', () => {
    const next = !limitOnNow();
    setToggle(d.limitOn, next);

    // Turning it on with nothing set would save a limit of zero, which reads as
    // "no limit" everywhere else and would switch itself straight back off.
    if (next && !(parseInt(d.limit.value, 10) > 0)) d.limit.value = '60';

    limitChanged();
    if (next) d.limit.focus();
});

d.force.addEventListener('click', () => {
    setToggle(d.force, !toggleOn(d.force));
    limitChanged();
});

d.limit.addEventListener('input', () => {
    if (editing && editing.limitsLocked) { limitDirty = true; renderLimit(); return; }
    renderLimit();
});
d.limit.addEventListener('change', limitChanged);

d.limitSave.addEventListener('click', () => {
    if (!editing) return;

    if (limitOnNow() && limitMinutesNow() <= 0) {
        limitResult(false, 'Enter how many minutes a day.');
        d.limit.focus();
        return;
    }
    if (!d.limitPin.value) {
        limitResult(false, 'The PIN is needed to save this.');
        d.limitPin.focus();
        return;
    }

    d.limitSave.disabled = true;
    d.limitMessage.classList.remove('good', 'bad');
    d.limitMessage.textContent = 'Saving…';
    send('save-limit', {
        id: editing.id,
        minutes: limitMinutesNow(),
        value: toggleOn(d.force),
        pin: d.limitPin.value
    });
});

d.limitPin.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); d.limitSave.click(); }
});

function limitResult(ok, text) {
    d.limitSave.disabled = false;
    d.limitMessage.textContent = text || '';
    // The same two classes the Extend view uses for the same two outcomes.
    d.limitMessage.classList.toggle('good', !!ok);
    d.limitMessage.classList.toggle('bad', !ok);
    if (!ok) return;

    limitDirty = false;
    d.limitPin.value = '';
    renderLimit();
}

function saveDetail() {
    if (!editing) return;
    send('save-app', {
        app: {
            id: editing.id,
            customName: d.customName.value,
            hotkeySequence: editing.hotkeySequence || '',
            hotkeyDisplay: editing.hotkeyDisplay || '',
            suppressHotkeyPassthrough: toggleOn(d.suppress),
            launchOnStartup: toggleOn(d.autostart),
            launchArguments: d.args.value,
            launchDelaySeconds: parseInt(d.delay.value, 10) || 0,
            // limitMinutesNow, not the raw field: the switch is what says
            // whether there is a limit, and turning it off deliberately leaves
            // the number where it was so turning it back on remembers it. Read
            // straight from the box, this saved that leftover number and the
            // limit you had just switched off stayed exactly where it was.
            dailyLimitMinutes: limitMinutesNow(),
            strictFocusMode: toggleOn(d.force),
            category: editing.category,
            actionType: editing.actionType,
            actionPayload: d.payload.value
        }
    });
}

for (const pill of d.actionCard.querySelectorAll('.pill')) {
    pill.addEventListener('click', () => {
        if (!editing) return;
        editing.actionType = Number(pill.dataset.action);
        for (const other of d.actionCard.querySelectorAll('.pill')) {
            other.setAttribute('aria-pressed', other === pill ? 'true' : 'false');
        }
        d.payloadCard.hidden = editing.actionType !== 3;
        d.actionHint.textContent = ACTION_HINTS[editing.actionType] || '';
        saveDetail();
    });
}
d.payload.addEventListener('change', saveDetail);

// Not d.limit or d.force: the limit card owns both, because they have to
// decide between saving straight away and arming the PIN. A second listener on
// the switch flipped it twice and left it exactly where it started; a second
// one on the field just saved the same thing twice.
for (const el of [d.customName, d.args, d.delay]) {
    el.addEventListener('change', saveDetail);
}
for (const el of [d.suppress, d.autostart]) {
    el.addEventListener('click', () => {
        if (el.disabled) return;
        setToggle(el, !toggleOn(el));
        saveDetail();
    });
}

function startCapture() {
    capturing = true;
    d.hotkey.classList.add('capturing');
    d.hotkey.textContent = 'Press a combination\u2026';
    send('capture-hotkey');
}

// Recording ends when the keys come up, so the field is showing the
// combination as it is built. Without this it sat on "Press a combination"
// throughout and only changed once everything was released, which felt like
// nothing was happening.


function stopCapture() {
    capturing = false;
    d.hotkey.classList.remove('capturing');
    d.hotkey.textContent = editing && editing.hotkeySequence ? editing.hotkeyDisplay : 'None';
    send('cancel-capture');
}

d.hotkey.addEventListener('click', () => (capturing ? stopCapture() : startCapture()));

d.hotkeyClear.addEventListener('click', () => {
    if (!editing) return;
    editing.hotkeySequence = '';
    editing.hotkeyDisplay = 'None';
    d.hotkey.textContent = 'None';
    saveDetail();
});

d.remove.addEventListener('click', () => {
    if (!editing) return;
    send('delete-app', { id: editing.id });
    show('palette');
});

document.querySelector('[data-back]').addEventListener('click', () => show('palette'));

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
        if (!message) return;

        if (message.type === 'limit-result') {
            limitResult(message.value, message.text);
            return;
        }

        if (message.type === 'theme') { applyTheme(message.theme); return; }

        if (message.type === 'app') { renderDetail(message.app); return; }

        if (message.type === 'hotkey-progress') {
            // Only while the field is listening: a stale progress message
            // arriving after a cancel must not overwrite the saved binding.
            if (capturing) d.hotkey.textContent = message.text;
            return;
        }

        if (message.type === 'hotkey-captured') {
            if (!capturing || !editing) return;
            capturing = false;
            d.hotkey.classList.remove('capturing');
            editing.hotkeySequence = message.sequence;
            editing.hotkeyDisplay = message.display;
            d.hotkey.textContent = message.display;
            saveDetail();
            return;
        }

        if (message.type === 'reset') {
            // A summon always starts clean. The window focus handler does this
            // too, but it does not fire when the palette is re-summoned while
            // already focused, which left the old query and selection in place.
            query = '';
            els.q.value = '';
            active = 0;
            facet = 'all';

            // Forget what was last asked for. The host ignores a resize that
            // arrives while the window is hidden, so anything measured while
            // the palette was down was never applied -- and without this,
            // fitWindow would see its own remembered value and decline to ask
            // again, leaving the window at whatever height it went down at.
            sentHeight = 0;

            show('palette');
            render();
            focusSearch();
            return;
        }

        if (message.type === 'show-manage') { manageActive = 0; renderManage(); show('manage'); return; }

        if (message.type === 'show-settings') { show('settings'); return; }

        if (message.type === 'toast') { showToast(message.text); return; }

        if (message.type === 'focus-input') { focusSearch(); return; }

        if (message.type === 'extend') {
            extendState = message.extend;
            x.pin.value = '';
            setExtendMessage('');
            renderExtend();
            return;
        }

        if (message.type === 'show-extend') {
            show('extend');
            // Straight to the PIN: the app and the amount both have sensible
            // defaults, and the PIN is the only field with no answer already.
            if (!x.grant.disabled) x.pin.focus();
            return;
        }

        if (message.type === 'extend-result') {
            setExtendMessage(message.text, message.value ? 'good' : 'bad');
            if (message.value) x.pin.value = '';
            else x.pin.select();
            return;
        }

        if (message.type === 'show-scanner') {
            scanPicked.clear();
            scanFilter = '';
            sc.filter.value = '';
            scanActive = 0;
            show('scanner');
            return;
        }

        if (message.type === 'scan') {
            scanning = message.scan.scanning;
            scanApps = message.scan.apps || [];
            renderScan();
            return;
        }

        if (message.type === 'settings') { renderSettings(message.settings); return; }

        if (message.type !== 'state') return;

        state = { ...state, ...message.state };
        if (view === 'manage') renderManage();
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
    render();
    focusSearch();
    send('ready');
});

render();
send('ready');
