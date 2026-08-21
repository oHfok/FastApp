/* ==========================================================
   WRAPPED — Spotify-Wrapped-style recap of the current week/
   month/year. Always live (never a one-time reveal): an
   in-progress period is compared to the same elapsed portion
   of the previous one, not its full total. Deliberately no
   browsing past periods here — that's what Periods is for;
   this is a small, curated "here's what's ready" moment with
   exactly three cards (current week/month/year).
   ========================================================== */

const WRAPPED_SEEN_KEY = 'fastapp-wrapped-seen';
const WRAPPED_TYPE_NOUN = { week: 'Week', month: 'Month', year: 'Year' };

let wrappedAvailableData = [];
let wrappedSlides = [];
let wrappedSlideIndex = 0;

function getWrappedSeen() {
    try { return JSON.parse(localStorage.getItem(WRAPPED_SEEN_KEY) || '{}'); }
    catch { return {}; }
}

function markWrappedSeen(type, label) {
    const seen = getWrappedSeen();
    seen[type] = label;
    localStorage.setItem(WRAPPED_SEEN_KEY, JSON.stringify(seen));
    updateWrappedReadyDot();
}

function updateWrappedReadyDot() {
    const seen = getWrappedSeen();
    const hasUnseen = wrappedAvailableData.some(w => seen[w.type] !== (w.label));
    const dot = document.getElementById('tb-wrapped-ready-dot');
    if (dot) dot.style.display = hasUnseen ? '' : 'none';
}

async function loadWrappedAvailable() {
    const cardsEl = document.getElementById('wrapped-panel-cards');
    try {
        const res = await fetch('/api/wrapped/available');
        const data = await res.json();
        wrappedAvailableData = data || [];

        if (wrappedAvailableData.length === 0) {
            cardsEl.innerHTML = `<div class="empty-state" style="padding:16px;">Nothing to wrap yet — check back once you've used your PC a bit this week.</div>`;
            document.getElementById('tb-wrapped-ready-dot').style.display = 'none';
            return;
        }

        cardsEl.innerHTML = wrappedAvailableData.map(w => {
            const type = w.type;
            const label = w.label;
            const teaser = w.teaser;
            return `
                <div class="wrapped-card" data-type="${type}" onclick="openWrappedStory(this.dataset.type)">
                    <div>
                        <div class="wrapped-card-eyebrow">${escapeHtml(label)}</div>
                        <div class="wrapped-card-teaser">${escapeHtml(teaser)}</div>
                    </div>
                    <div class="wrapped-card-chev">&rarr;</div>
                </div>`;
        }).join('');

        updateWrappedReadyDot();
    } catch (err) {
        console.error('Failed to load Wrapped availability', err);
        if (cardsEl) cardsEl.innerHTML = `<div class="empty-state" style="padding:16px;">Couldn't load Wrapped.</div>`;
    }
}

function toggleWrappedPanel() {
    const panel = document.getElementById('wrapped-panel');
    const isOpen = panel.style.display !== 'none';
    panel.style.display = isOpen ? 'none' : '';
    if (!isOpen) loadWrappedAvailable();
}

function initWrappedPanelOutsideClick() {
    document.addEventListener('click', (e) => {
        const panel = document.getElementById('wrapped-panel');
        const btn = document.getElementById('tb-wrapped-btn');
        if (!panel || panel.style.display === 'none') return;
        if (panel.contains(e.target) || btn.contains(e.target)) return;
        panel.style.display = 'none';
    });
}

// --- Story ------------------------------------------------------------------

async function openWrappedStory(type) {
    document.getElementById('wrapped-panel').style.display = 'none';
    const overlay = document.getElementById('wrapped-overlay');
    overlay.style.display = 'flex';
    document.getElementById('wrapped-slide-body').innerHTML = `<div class="empty-state" style="border:none;background:none;">Loading…</div>`;

    try {
        const res = await fetch(`/api/wrapped?type=${type}`);
        const data = await res.json();
        if (data.error) throw new Error(data.error);

        wrappedSlides = buildWrappedSlides(type, data);
        wrappedSlideIndex = 0;
        renderWrappedSlide();

        markWrappedSeen(type, data.label);
    } catch (err) {
        console.error('Failed to load Wrapped story', err);
        document.getElementById('wrapped-slide-body').innerHTML = `<div class="empty-state" style="border:none;background:none;">Couldn't load this recap.</div>`;
    }
}

function closeWrappedStory() {
    document.getElementById('wrapped-overlay').style.display = 'none';
    wrappedSlides = [];
}

function handleWrappedOverlayClick(event) {
    // Click on the dimmed backdrop itself (not the slide or the nav zones,
    // which stopPropagation) closes the story.
    if (event.target.id === 'wrapped-overlay') closeWrappedStory();
}

// Keyboard navigation for the story. The prev/next zones are invisible
// full-height strips sized for the mouse — making them tab stops would put two
// unlabelled buttons in the tab order for no benefit, so the keyboard gets
// arrow keys instead (Escape closes, handled centrally in app.js).
function initWrappedKeys() {
    document.addEventListener('keydown', (e) => {
        const overlay = document.getElementById('wrapped-overlay');
        if (!overlay || overlay.style.display === 'none') return;

        if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { e.preventDefault(); wrappedNextSlide(); }
        else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { e.preventDefault(); wrappedPrevSlide(); }
    });
}

function wrappedNextSlide() {
    if (wrappedSlideIndex < wrappedSlides.length - 1) {
        wrappedSlideIndex++;
        renderWrappedSlide();
    } else {
        closeWrappedStory();
    }
}

function wrappedPrevSlide() {
    if (wrappedSlideIndex > 0) {
        wrappedSlideIndex--;
        renderWrappedSlide();
    }
}

function renderWrappedSlide() {
    const slide = wrappedSlides[wrappedSlideIndex];
    if (!slide) return;

    document.getElementById('wrapped-progress-row').innerHTML = wrappedSlides.map((s, i) => {
        const cls = i < wrappedSlideIndex ? 'done' : i === wrappedSlideIndex ? 'active' : '';
        return `<div class="progress-seg ${cls}"></div>`;
    }).join('');

    // innerHTML, not textContent: slide.eyebrow deliberately contains the
    // "&middot;" entity (textContent would print it as literal, undecoded
    // text). Safe here since it's built entirely from backend-computed
    // date/label strings, never from an app name or window title.
    document.getElementById('wrapped-slide-eyebrow').innerHTML = slide.eyebrow;
    document.getElementById('wrapped-slide-counter').textContent = `${wrappedSlideIndex + 1} / ${wrappedSlides.length}`;
    document.getElementById('wrapped-slide-body').innerHTML = slide.body;
}

// Each builder returns { eyebrow, body } for one slide. Unlike the old
// version, the slide SET itself now differs by type -- Week is tactical
// (day-by-day, a light "vibe"), Month is about trajectory (week-by-week,
// milestones crossed), Year is the retrospective (month-by-month, every
// milestone crossed all year, top 3 apps, and the full "Your Type" payoff).
// Shared pieces (category breakdown, the rhythm bar chart, the archetype
// card) are built by the render* helpers below and reused across periods
// with different data, not different markup.
function buildWrappedSlides(type, data) {
    const noun = WRAPPED_TYPE_NOUN[type] || 'Period';
    // CSS handles the uppercase styling (.wrapped-slide .eyebrow) -- doing it
    // here in JS as well would also uppercase the "&middot;" entity into
    // "&MIDDOT;", which browsers don't recognize and print literally.
    const eyebrow = `${data.label} &middot; ${(data.dateRange) || ''}`;
    const elapsedSuffix = (data.isInProgress) ? ' so far' : '';

    const totalFocusedHours = data.totalFocusedHours ?? 0;
    const topApp = data.topApp;
    const topApps = data.topApps;
    const archetype = data.archetype;
    const categoryBreakdown = data.categoryBreakdown;
    const milestones = data.milestones;

    const slides = [];

    // 1. Cover -- one clean rounded number, not "Xh Ym" crammed into one
    // 96px line (that's what wrapped into two cramped stacked lines before).
    // Exact minutes aren't lost, just moved down to caption-sized text.
    // totalFocusedHours IS already the truly-focused figure (not PC-on time),
    // so there's no second "% of that was truly focused" number to tack on --
    // that phrasing implied a subset-of-a-subset that doesn't exist and was
    // just confusing. The focus-quality read (high/steady/low) still shows up
    // in the Archetype slide's description, framed as a description instead
    // of a contradictory qualifier on this number.
    slides.push({
        eyebrow,
        body: `
            <div class="headline">Your <em>${noun}</em>, Wrapped.</div>
            <div class="hero">
                <div class="hero-number">${Math.round(totalFocusedHours)}<span class="unit">h</span></div>
                <div class="hero-caption">hours focused${elapsedSuffix}</div>
            </div>`
    });

    // 2. Rhythm -- day-by-day for Week, week-by-week for Month, month-by-month
    // for Year. Same component, different grain, because that's the level of
    // detail that's actually meaningful at each timescale.
    slides.push({ eyebrow, body: renderRhythmBody(type, noun, data) });

    // 3. Where your time went -- category breakdown, real data Wrapped never
    // used to touch at all.
    slides.push({ eyebrow, body: renderCategoryBreakdownBody(noun, categoryBreakdown) });

    if (type === 'week') {
        slides.push({ eyebrow, body: renderTopAppBody(noun, topApp) });
        slides.push({ eyebrow, body: renderArchetypeBody(noun, archetype) });
    } else if (type === 'month') {
        slides.push({ eyebrow, body: renderMilestonesBody(noun, milestones) });
        slides.push({ eyebrow, body: renderTopAppBody(noun, topApp) });
        slides.push({ eyebrow, body: renderArchetypeBody(noun, archetype) });
    } else {
        slides.push({ eyebrow, body: renderMilestonesBody(noun, milestones) });
        slides.push({ eyebrow, body: renderTopAppsBody(topApps) });
        slides.push({ eyebrow, body: renderArchetypeBody(noun, archetype) });
    }

    return slides;
}

// --- Rhythm: a small bar chart standing in for the giant hero-number the
// other slides use -- this one's about a shape across days/weeks/months, not
// a single figure. The headline names the strongest bucket explicitly so the
// story reads even before you look at the bars.
function renderRhythmBody(type, noun, data) {
    const buckets = (data.rhythmBuckets ?? []).map(b => ({
        label: b.label ?? '',
        hours: b.hours ?? 0,
        isFuture: b.isFuture ?? false
    }));
    const rhythmLabel = data.rhythmLabel ?? '';
    const unitWord = type === 'week' ? 'day' : type === 'month' ? 'week' : 'month';

    const real = buckets.filter(b => !b.isFuture && b.hours > 0);
    const peak = real.length > 0 ? real.reduce((a, b) => (b.hours > a.hours ? b : a)) : null;
    const headline = peak
        ? `<em>${escapeHtml(peak.label)}</em> was your strongest ${unitWord}.`
        : `Here's your ${noun.toLowerCase()} so far.`;

    const maxHours = Math.max(0.01, ...real.map(b => b.hours));
    const barsHtml = buckets.map(b => {
        const pct = b.isFuture ? 4 : Math.max(4, Math.min(100, (b.hours / maxHours) * 100));
        const isPeak = peak && b.label === peak.label && b.hours === peak.hours;
        return `
            <div class="bar-col">
                <div class="bar-track"><div class="bar-fill ${isPeak ? 'is-peak' : ''} ${b.isFuture ? 'is-future' : ''}" style="height:${pct}%"></div></div>
                <div class="bar-label">${escapeHtml(b.label)}</div>
            </div>`;
    }).join('');

    return `
        <div class="headline">${headline}</div>
        <div class="rhythm-caption">${escapeHtml(rhythmLabel)}</div>
        <div class="bar-row">${barsHtml}</div>`;
}

// --- Where your time went: dominant category as the hero stat, the rest of
// the split listed underneath so the whole picture is visible at a glance.
function renderCategoryBreakdownBody(noun, categoryBreakdown) {
    if (!categoryBreakdown) {
        return `<div class="headline">Not enough data yet to see where your time went.</div>`;
    }
    const top = categoryBreakdown.top;
    const all = categoryBreakdown.all ?? [];
    const topCat = top.category;
    const topPct = top.pct ?? 0;

    const listHtml = all.map(c => {
        const cat = c.category;
        const pct = c.pct ?? 0;
        return `
            <div class="cat-row">
                <div class="cat-dot" style="background:${catColor(cat)}"></div>
                <div class="cat-name">${escapeHtml(cat)}</div>
                <div class="cat-pct">${pct}%</div>
            </div>`;
    }).join('');

    return `
        <div class="headline">Most of your time went to <em>${escapeHtml(topCat)}</em>.</div>
        <div class="hero">
            <div class="hero-number">${topPct}<sup>%</sup></div>
            <div class="hero-caption">of your tracked time this ${noun.toLowerCase()}</div>
        </div>
        <div class="cat-list">${listHtml}</div>`;
}

// --- Top app (Week/Month): unchanged from the original design -- this one
// already did real comparison-to-last-period work and reads fine.
function renderTopAppBody(noun, topApp) {
    if (!topApp) return `<div class="headline">No standout app this ${noun.toLowerCase()} yet.</div>`;

    const appName = topApp.appName;
    const minutes = topApp.minutes ?? 0;
    const mover = topApp.mover;
    let moverHtml = '';
    if (mover) {
        const moverName = mover.appName;
        const direction = mover.direction;
        const moverDeltaPct = mover.deltaPct;
        const moverText = moverDeltaPct != null
            ? `${direction === 'up' ? '&#9650;' : '&#9660;'} ${moverDeltaPct}% vs last ${noun.toLowerCase()}`
            : (direction === 'up' ? 'New this ' + noun.toLowerCase() : 'Quiet this ' + noun.toLowerCase());
        moverHtml = `
            <div class="foot" style="grid-template-columns:1fr;">
                <div class="foot-card">
                    <div class="foot-label">Biggest Mover</div>
                    <div class="foot-value ${direction === 'up' ? 'teal' : ''}">${escapeHtml(moverName)} &middot; ${moverText}</div>
                </div>
            </div>`;
    }
    return `
        <div class="headline"><em>${escapeHtml(appName)}</em> was your top app.</div>
        <div class="hero">
            <div class="hero-number" style="font-size:56px;">${escapeHtml(appName.charAt(0).toUpperCase())}</div>
            <div class="hero-caption">${formatTime(minutes)} focused</div>
        </div>
        ${moverHtml}`;
}

// --- Top apps, plural (Year only): a whole year earns more than a single
// hero app -- a small ranked list instead.
function renderTopAppsBody(topApps) {
    const list = topApps ?? [];
    if (list.length === 0) return `<div class="headline">No standout apps yet this year.</div>`;

    const rows = list.map((a, i) => {
        const appName = a.appName;
        const minutes = a.minutes ?? 0;
        const deltaPct = a.deltaPct;
        const deltaText = deltaPct == null ? '' : ` &middot; ${deltaPct >= 0 ? '&#9650;' : '&#9660;'} ${Math.abs(deltaPct)}% vs last year`;
        return `
            <div class="rank-row">
                <div class="rank-num">#${i + 1}</div>
                <div>
                    <div class="rank-name">${escapeHtml(appName)}</div>
                    <div class="rank-sub">${formatTime(minutes)}${deltaText}</div>
                </div>
            </div>`;
    }).join('');

    return `
        <div class="headline">Your top apps this year.</div>
        <div class="rank-list">${rows}</div>`;
}

// --- Milestones this period (Month/Year): pulls from the same tier ladder
// shown in the App Detail drawer -- any app that crossed Bronze/Silver/Gold/
// Platinum within this window shows up here. Real cross-feature payoff, not
// filler: this is data the app already tracks that a period recap should
// obviously surface.
function renderMilestonesBody(noun, milestones) {
    const list = milestones ?? [];
    if (list.length === 0) {
        return `
            <div class="headline">No milestones crossed this ${noun.toLowerCase()} yet.</div>
            <div class="hero"><div class="hero-caption">Tiers are Bronze (10h), Silver (50h), Gold (150h), Platinum (500h) per app -- keep going.</div></div>`;
    }
    const rows = list.map(m => {
        const appName = m.appName;
        const tierName = m.tierName;
        const date = m.date;
        return `
            <div class="ms-row">
                <div class="ms-dot" style="background:${milestoneTierColor(tierName)}"></div>
                <div>
                    <div class="ms-app">${escapeHtml(appName)}</div>
                    <div class="ms-tier">${escapeHtml(tierName)} &middot; ${escapeHtml(date)}</div>
                </div>
            </div>`;
    }).join('');

    return `
        <div class="headline">${list.length} milestone${list.length === 1 ? '' : 's'} reached this ${noun.toLowerCase()}.</div>
        <div class="ms-list">${rows}</div>`;
}

// --- Archetype ("Your Type" / "This week's vibe"): the actual payoff slide.
// Week gets the light-weight "vibe" framing (smaller, no permanent label --
// one week isn't enough data to crown an identity); Month/Year get the full
// treatment as the story's closing beat.
function renderArchetypeBody(noun, archetype) {
    if (!archetype) return `<div class="headline">Not enough data yet for a read on your ${noun.toLowerCase()}.</div>`;

    const label = archetype.label;
    const description = archetype.description;
    const weight = archetype.weight ?? 'full';
    const titleText = weight === 'light' ? `This ${noun.toLowerCase()}'s vibe` : 'Your Type';

    return `
        <div class="headline">${titleText}</div>
        <div class="hero archetype-hero ${weight === 'light' ? 'is-light' : ''}">
            <div class="archetype-label">${escapeHtml(label)}</div>
            <div class="hero-caption">${escapeHtml(description)}</div>
        </div>`;
}
