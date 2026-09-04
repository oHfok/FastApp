/* ==========================================================
   ANALYTICS

   A report, not a dashboard. The engine on the desktop side does every
   calculation and hands this page finished observations with their evidence
   attached; nothing here works anything out. That split is the point: an
   insight assembled from three numbers in JavaScript has nothing to show when
   somebody asks why it thinks so.

   So this file only renders. If a sentence here is wrong, the fault is in
   Services/Analytics, which is where it can be tested.
   ========================================================== */

(function () {
    /* "Focus" became "Continuity" because the program cannot see focus. It can
       see that one window held the foreground for two hours without a break,
       which is equally true of somebody deep in a manuscript and somebody
       asleep in front of a film. The label was the last place this page still
       named a measurement after a conclusion it had not earned.

       "Anomaly" is gone. No detector has ever produced one -- it was the slot
       the median-absolute-deviation detector would have filled, and that was
       measured, found to detect days off, and deliberately not written. A
       label with nothing behind it is a promise the page cannot keep. */
    const KIND = {
        change:     { label: 'Something changed', mark: '↗' },
        continuity: { label: 'Continuity',        mark: '◎' },
        routine:    { label: 'Your routine',      mark: '⟳' },
        pattern:    { label: 'A pattern',         mark: '◈' },
        discovery:  { label: 'New',               mark: '✦' }
    };

    let loaded = false;

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function insightCard(insight) {
        const kind = KIND[insight.kind] || { label: insight.kind, mark: '·' };
        const card = el('article', 'an-insight');

        const head = el('div', 'an-insight-head');
        head.appendChild(el('span', 'an-kind-mark', kind.mark));
        head.appendChild(el('span', 'an-kind', kind.label));
        if (insight.trend) {
            head.appendChild(el('span', 'an-trend an-trend-' + insight.trend,
                insight.trend === 'up' ? 'higher'
                : insight.trend === 'down' ? 'lower'
                : insight.trend));
        }
        card.appendChild(head);

        card.appendChild(el('h3', 'an-insight-title', insight.title));
        if (insight.explanation) card.appendChild(el('p', 'an-insight-body', insight.explanation));

        // The evidence is shown rather than tucked away. Being told something
        // about yourself is worth very little without the measurements that
        // led to it.
        if (insight.evidence && insight.evidence.length) {
            const list = el('ul', 'an-evidence');
            for (const line of insight.evidence) {
                if (line) list.appendChild(el('li', null, line));
            }
            card.appendChild(list);
        }

        if (insight.recommendation) {
            const rec = el('p', 'an-rec');
            rec.appendChild(el('span', 'an-rec-label', 'Worth trying'));
            rec.appendChild(document.createTextNode(insight.recommendation));
            card.appendChild(rec);
        }

        const foot = el('div', 'an-insight-foot');
        foot.appendChild(el('span', null, insight.period || ''));
        foot.appendChild(el('span', 'an-confidence',
            `confidence ${Math.round((insight.confidence || 0) * 100)}%`));
        card.appendChild(foot);
        return card;
    }

    function hourShape(hours) {
        const wrap = el('div', 'an-hours');
        const peak = Math.max(1, ...hours);
        hours.forEach((minutes, hour) => {
            const col = el('div', 'an-hour');
            const bar = el('div', 'an-hour-bar');
            // A floor of 2% so an hour with a little activity is still visible
            // as a mark rather than reading as an hour with none.
            bar.style.height = minutes > 0
                ? Math.max(2, (minutes / peak) * 100) + '%'
                : '0';
            bar.title = `${String(hour).padStart(2, '0')}:00 — ${Math.round(minutes)} min`;
            col.appendChild(bar);
            if (hour % 6 === 0) col.appendChild(el('span', 'an-hour-label', String(hour).padStart(2, '0')));
            wrap.appendChild(col);
        });
        return wrap;
    }

    /* Applications and categories are the same row: a name, a share, a
       duration and a change. Written once because two copies of a four-column
       grid diverge the first time either is touched. */
    function barRows(items, modifier) {
        const list = el('div', 'an-apps' + (modifier ? ' ' + modifier : ''));
        for (const item of items) {
            const row = el('div', 'an-app');
            row.appendChild(el('span', 'an-app-name', item.name));

            const track = el('span', 'an-app-track');
            const fill = el('span', 'an-app-fill');
            fill.style.width = Math.max(1, item.share) + '%';
            track.appendChild(fill);
            row.appendChild(track);

            row.appendChild(el('span', 'an-app-time', item.time));
            if (item.changePercent) {
                row.appendChild(el('span',
                    'an-app-change ' + (item.changePercent > 0 ? 'up' : 'down'),
                    `${item.changePercent > 0 ? '+' : ''}${item.changePercent}%`));
            } else {
                row.appendChild(el('span', 'an-app-change', ''));
            }
            list.appendChild(row);
        }
        return list;
    }

    /* What the engine can and cannot see, listed by the engine itself.

       A reader who does not know what is looked for will hear "nothing stood
       out" as "your week was unremarkable", when all it can honestly mean is
       "none of these specific measurements crossed a threshold". Saying what
       is measured is what makes the absence of a finding readable -- and it
       stops a long unbroken stretch being taken as evidence of concentration,
       which is the one inference this page most invites and least supports. */
    function coverage(report) {
        const wrap = el('section', 'an-block an-coverage');
        wrap.appendChild(el('h2', 'an-block-title', 'What this page can and cannot see'));

        const cols = el('div', 'an-coverage-cols');

        const yes = el('div', 'an-coverage-col');
        yes.appendChild(el('h3', 'an-coverage-head', 'Measured'));
        const yesList = el('ul', 'an-coverage-list an-coverage-yes');
        (report.understands || []).forEach(line => yesList.appendChild(el('li', null, line)));
        yes.appendChild(yesList);

        const no = el('div', 'an-coverage-col');
        no.appendChild(el('h3', 'an-coverage-head', 'Not known'));
        const noList = el('ul', 'an-coverage-list an-coverage-no');
        (report.doesNotUnderstand || []).forEach(line => noList.appendChild(el('li', null, line)));
        no.appendChild(noList);

        cols.appendChild(yes);
        cols.appendChild(no);
        wrap.appendChild(cols);
        return wrap;
    }

    function render(report) {
        const root = document.getElementById('an-body');
        root.textContent = '';

        /* Not the same as an empty history, and it used to be rendered as one.
           A database that is locked, missing or corrupt produced "Nothing has
           been recorded yet. This page fills in as you use your computer." --
           a confident falsehood, and the worst possible one here, since it
           tells somebody whose history may be in trouble that there is nothing
           to worry about. */
        if (report.couldNotRead && !report.hasEnoughHistory) {
            const band = el('section', 'an-broken');
            band.appendChild(el('h2', 'an-broken-title', 'Analytics unavailable'));
            band.appendChild(el('p', 'an-broken-body',
                'Your activity history could not be read, so nothing on this page could be worked out. '
                + 'This is not the same as there being nothing recorded — your history may well be intact.'));
            if (report.problem) {
                band.appendChild(el('p', 'an-broken-detail', report.problem));
            }
            root.appendChild(band);
            root.appendChild(coverage(report));
            return;
        }

        if (!report.hasEnoughHistory) {
            root.appendChild(el('p', 'an-empty', report.notYet || 'Nothing recorded yet.'));
            return;
        }

        /* Read, but not all of it. Everything below is real and none of it is
           necessarily complete, so the page says so before the reader draws a
           conclusion from a fragment. */
        if (report.couldNotRead) {
            const band = el('p', 'an-partial');
            band.appendChild(el('span', 'an-partial-label', 'Incomplete'));
            band.appendChild(document.createTextNode(
                'Part of your history could not be read, so everything below covers less than it should. '
                + (report.problem || '')));
            root.appendChild(band);
        }

        // --- the week in a number -------------------------------------
        const summary = el('section', 'an-summary');
        const figure = el('div', 'an-figure');
        figure.appendChild(el('div', 'an-figure-value', report.activeTotal));
        figure.appendChild(el('div', 'an-figure-label', 'active over ' + report.period));
        if (report.hasComparison && report.activeChangePercent) {
            const up = report.activeChangePercent > 0;
            figure.appendChild(el('div', 'an-delta ' + (up ? 'up' : 'down'),
                `${up ? '↑' : '↓'} ${Math.abs(report.activeChangePercent)}% a day against your usual`));
        }
        summary.appendChild(figure);
        summary.appendChild(el('p', 'an-headline', report.headline));
        root.appendChild(summary);

        // Said plainly rather than hidden: a page that compares you against
        // yourself is worth nothing until it knows what you are usually like.
        if (report.notYet) root.appendChild(el('p', 'an-learning', report.notYet));

        // --- when the days happen -------------------------------------
        if (report.hourShape && report.hourShape.some(h => h > 0)) {
            const section = el('section', 'an-block');
            section.appendChild(el('h2', 'an-block-title', 'When your days happen'));
            section.appendChild(hourShape(report.hourShape));
            root.appendChild(section);
        }

        // --- what was found -------------------------------------------
        if (report.insights && report.insights.length) {
            const section = el('section', 'an-block');
            section.appendChild(el('h2', 'an-block-title', 'What stood out'));
            const grid = el('div', 'an-insights');
            report.insights.forEach(i => grid.appendChild(insightCard(i)));
            section.appendChild(grid);

            /* Findings that were made and then not printed, because something
               stronger had already said the same thing. Counted rather than
               dropped in silence: a reader who expected a card about an
               application deserves to know it was collapsed, not missed. */
            if (report.alsoFound > 0) {
                section.appendChild(el('p', 'an-also',
                    `${report.alsoFound} further observation${report.alsoFound === 1 ? '' : 's'} `
                    + 'said much the same thing as the cards above, and were left out.'));
            }
            root.appendChild(section);
        } else {
            /* Was: "Nothing stood out this week — your use of the computer
               looked much like it usually does." That is a claim about the
               week. All the engine knows is that none of its own thresholds
               were crossed, which is a claim about the engine. */
            root.appendChild(el('p', 'an-empty',
                'Nothing unusual was detected. None of the patterns this page tracks differed '
                + 'enough from your usual activity to report — see what is measured below.'));
        }

        // --- what the time was made of --------------------------------
        /* Kinds of time before applications. "Communication, 14 hours" is a
           statement about a week; "Discord, 14 hours" is a statement about a
           process, and the reader has to do the translating. The page leads
           with the one that needs no translating. */
        if (report.hasCategories && report.categories && report.categories.length) {
            const section = el('section', 'an-block');
            section.appendChild(el('h2', 'an-block-title', 'What kind of time it was'));
            // Wider name column: category names run to "Media Production"
            // where application names are mostly one short word, and the
            // shared 12ch column clipped "Communication" to "Communicat...".
            section.appendChild(barRows(report.categories, 'an-cats'));
            section.appendChild(el('p', 'an-source',
                `From the categories you have set in the dashboard, covering ${report.categoryCoverage}% `
                + 'of your recorded time. Anything uncategorised is left out rather than guessed at.'));
            root.appendChild(section);
        }

        if (report.topApps && report.topApps.length) {
            const section = el('section', 'an-block');
            section.appendChild(el('h2', 'an-block-title', 'Which applications'));
            section.appendChild(barRows(report.topApps));
            root.appendChild(section);
        }

        root.appendChild(askBox());
        root.appendChild(coverage(report));

        root.appendChild(el('p', 'an-privacy',
            'Everything on this page, questions included, was worked out on this computer '
            + 'from data that never left it.'));
    }

    /* ---- asking about your own activity ----------------------------------
       Answered by the same engine that built the page, on this machine. The
       question box is deliberately not a search box with a cursor blinking in
       it: nobody knows what to ask a program about themselves, so the examples
       are the interface and typing is the shortcut. */
    function askBox() {
        const wrap = el('section', 'an-block an-ask');
        wrap.appendChild(el('h2', 'an-block-title', 'Ask about your activity'));

        const row = el('div', 'an-ask-row');
        const input = el('input', 'an-ask-input');
        input.type = 'text';
        input.placeholder = 'When am I most focused?';
        input.setAttribute('aria-label', 'Ask about your activity');
        const button = el('button', 'an-ask-go', 'Ask');
        button.type = 'button';
        row.appendChild(input);
        row.appendChild(button);
        wrap.appendChild(row);

        const chips = el('div', 'an-ask-chips');
        wrap.appendChild(chips);
        const out = el('div', 'an-ask-answer');
        wrap.appendChild(out);

        async function ask(question) {
            if (!question) return;
            input.value = question;
            out.textContent = '';
            out.appendChild(el('p', 'an-ask-text', 'Looking…'));
            try {
                const res = await fetch('/api/analytics/ask?q=' + encodeURIComponent(question));
                const data = await res.json();
                out.textContent = '';

                out.appendChild(el('p', 'an-ask-text', data.text));

                if (data.evidence && data.evidence.length) {
                    const list = el('ul', 'an-evidence');
                    data.evidence.forEach(line => line && list.appendChild(el('li', null, line)));
                    out.appendChild(list);
                }
                if (data.basedOn) {
                    out.appendChild(el('p', 'an-ask-basis', 'Based on ' + data.basedOn));
                }
                // Only when it did not follow: offering the same six every time
                // would make an answer look like a failure.
                showChips(data.understood ? [] : (data.suggestions || []));
            } catch (err) {
                out.textContent = '';
                out.appendChild(el('p', 'an-ask-text', 'Could not answer that: ' + err.message));
            }
        }

        function showChips(list) {
            chips.textContent = '';
            for (const example of list) {
                const chip = el('button', 'an-chip', example);
                chip.type = 'button';
                chip.addEventListener('click', () => ask(example));
                chips.appendChild(chip);
            }
        }

        button.addEventListener('click', () => ask(input.value.trim()));
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter') { e.preventDefault(); ask(input.value.trim()); }
        });

        showChips([
            'When am I most focused?',
            'What changed this week?',
            'What interrupts me the most?',
            'What is my usual routine?',
            'What do I spend the most time in?',
            'What kind of time is it?',
            'How much am I on my computer?'
        ]);
        return wrap;
    }

    async function load() {
        const root = document.getElementById('an-body');
        root.textContent = '';
        root.appendChild(el('p', 'an-empty', 'Reading your history…'));
        try {
            const res = await fetch('/api/analytics');
            if (!res.ok) throw new Error('The analytics engine returned ' + res.status);
            render(await res.json());
            loaded = true;
        } catch (err) {
            root.textContent = '';
            root.appendChild(el('p', 'an-empty', 'Could not build the report: ' + err.message));
        }
    }

    // Dashboard, not window.Dashboard. utils.js declares it as a top-level
    // const, which in a classic script lives in the global lexical scope and
    // never becomes a property of window -- so `window.Dashboard = window.Dashboard || {}`
    // did not find it and quietly built a second object beside it. This file
    // registered on that one; switchView reads the real one; the tab opened
    // empty forever and nothing anywhere threw. Every other tab does it this
    // way, and the defensive version is what broke it: it turned a missing
    // global into a silent parallel universe instead of a loud error.
    Dashboard.tabs.analytics = {
        // Built once a visit. It reads several weeks of sessions, and nothing in
        // it changes minute to minute.
        onEnter() { if (!loaded) load(); },
        reload: load
    };
})();
