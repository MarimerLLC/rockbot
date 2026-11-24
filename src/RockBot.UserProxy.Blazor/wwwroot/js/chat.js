// The theme is already on the body: an inline script at the top of App.razor's <body> applies
// the saved choice (rockbot.theme) or the system's colour scheme before the page is painted.

// ── Transcript snapshot ───────────────────────────────────────────────────
// What the chat looked like when this browser last showed it, so a reload (a phone waking the
// tab, a dropped circuit, a new pod) opens on that transcript, where the reader left it, instead
// of on a page that starts at the top and then scrolls. The live transcript replaces it once the
// page has caught up with the server (chatHelpers.endRestore, from Chat.razor).
//
// The snapshot is shown in .chat-snapshot, an element Blazor renders with no children of its own,
// so filling it never disturbs Blazor's diffing. The live .chat-messages is hidden meanwhile.
// It is kept in localStorage, so it outlives the tab; it is cleared on sign-out and on any page
// that is not the chat (the sign-in page after an expired session, access denied).
window.rbSnapshot = (function () {
    const KEY = 'rockbot.chatSnapshot';
    const VERSION = 1;
    // localStorage holds about 5 MB of UTF-16 per origin; leave the rest of it alone. A longer
    // transcript keeps its most recent messages.
    const MAX_CHARS = 1500000;
    // A backup save while the page is open; the one that matters is on leaving the tab.
    const SAVE_DELAY_MS = 10000;
    // Within this of the bottom, the reader counts as following the conversation.
    const PINNED_PX = 48;
    const RESTORING = 'rb-snapshot';
    const INTERACTIONS = ['wheel', 'touchmove', 'keydown', 'pointerdown'];
    // Markup that can run or load something; never put back from storage.
    const UNSAFE = 'script, iframe, object, embed, link, meta, style, base, frame, frameset';
    const idle = window.requestIdleCallback || function (f) { return setTimeout(f, 0); };

    let snapshotHtml = null; // shown while restoring
    let snapshotCount = 0;   // messages in the snapshot
    let restoring = false;   // the snapshot is on screen, the live transcript hidden
    let ended = false;       // endRestore has run: the live transcript is on screen
    let interacted = false;  // the reader has touched the page, so a scroll may be theirs
    let signedOut = false;
    // Until the live transcript has this many messages, don't save over the snapshot (the
    // history didn't load, so the live one is behind it).
    let holdSavesUntil = 0;
    // Where the transcript on screen should sit: { fromBottom: px } or { lastMessage: true }.
    let desired = null;
    let placedShell = null;  // the elements already positioned; only a new one is placed again
    let placedLive = null;
    let watched = null;      // the live .chat-messages being saved from
    let liveObserver = null;
    let saveTimer = null;

    function live() { return document.querySelector('.chat-messages:not(.chat-snapshot)'); }
    function shell() { return document.querySelector('.chat-snapshot'); }
    function onScreen() { return restoring ? shell() : live(); }

    // The transcript's messages, without the "Thinking" line (it comes and goes on its own).
    function messages(el) {
        return Array.prototype.filter.call(el.querySelectorAll('.message-wrapper'),
            function (m) { return !m.querySelector('.thinking-bubble'); });
    }

    function read() {
        try {
            const raw = window.localStorage.getItem(KEY);
            if (!raw) return null;
            const s = JSON.parse(raw);
            return s && s.v === VERSION && typeof s.html === 'string' && s.html.trim() ? s : null;
        } catch (e) {
            return null;
        }
    }

    function clear() {
        try { window.localStorage.removeItem(KEY); } catch (e) { /* nothing to clear */ }
    }

    // Puts the stored transcript into the snapshot element. Storage is not trusted: anything that
    // could run is dropped (a template parses without running or loading anything). The copy's
    // controls have no handlers behind them, so they are disabled, and a form in it (an MCP
    // question) must not submit natively and reload the page.
    function fill(s) {
        const t = document.createElement('template');
        t.innerHTML = snapshotHtml;
        t.content.querySelectorAll(UNSAFE).forEach(function (n) { n.remove(); });
        t.content.querySelectorAll('*').forEach(function (n) {
            for (const a of Array.from(n.attributes)) {
                const name = a.name.toLowerCase();
                const url = /^(href|src|action|formaction|xlink:href|srcdoc)$/.test(name);
                if (name.startsWith('on') || name === 'srcdoc'
                    || (url && /^\s*(javascript|data|vbscript):/i.test(a.value) && !/^\s*data:image\//i.test(a.value)))
                    n.removeAttribute(a.name);
            }
        });
        t.content.querySelectorAll('input, textarea, select, button').forEach(function (c) { c.disabled = true; });
        s.replaceChildren(t.content);
        s.addEventListener('submit', function (e) { e.preventDefault(); }, true);
    }

    // Sets a scroll position without the smooth animation .chat-messages has in CSS.
    function jumpTo(el, top) {
        const previous = el.style.scrollBehavior;
        el.style.scrollBehavior = 'auto';
        el.scrollTop = top;
        el.style.scrollBehavior = previous;
    }

    function fromBottom(el) {
        return Math.max(0, el.scrollHeight - el.clientHeight - el.scrollTop);
    }

    // The top of a message: where Chat.razor scrolls a new message to.
    function messageTop(el, m) {
        return m.getBoundingClientRect().top - el.getBoundingClientRect().top + el.scrollTop;
    }

    function place(el) {
        if (!el || !desired) return;
        if (desired.lastMessage) {
            const msgs = el.querySelectorAll('.message-wrapper');
            jumpTo(el, msgs.length ? messageTop(el, msgs[msgs.length - 1]) : el.scrollHeight);
        } else {
            jumpTo(el, el.scrollHeight - el.clientHeight - desired.fromBottom);
        }
    }

    function noteInteraction() { interacted = true; }

    // Follows the transcript on screen. Before the restore ends, only once the reader has touched
    // the page: until then the only scrolling is ours. Scroll events don't bubble; capture does.
    function onScroll(e) {
        if ((ended || interacted) && e.target === onScreen())
            desired = { fromBottom: fromBottom(e.target) };
    }

    // Blazor's first interactive render replaces the prerendered page with new elements (and a
    // resumed circuit can do it again later): an empty .chat-snapshot and a .chat-messages
    // scrolled to the top. Mutation callbacks run before the next paint, so putting both back
    // here means neither is ever seen. Only a new element is placed: re-placing on every change
    // would fight the reader's own scrolling.
    function onPageMutation() {
        if (restoring) {
            const s = shell();
            if (s && s !== placedShell) {
                fill(s);
                place(s);
                placedShell = s;
            }
        } else {
            const el = live();
            if (!el || el === placedLive) return;
            place(el);
            placedLive = el;
            if (ended) watchLive(el);
        }
    }

    function capture(el) {
        let html = el.innerHTML;
        if (html.length > MAX_CHARS) {
            html = '';
            for (let node = el.lastElementChild; node; node = node.previousElementSibling) {
                const part = node.outerHTML;
                if (html.length + part.length > MAX_CHARS) break;
                html = part + html;
            }
        }
        return { v: VERSION, html: html, fromBottom: fromBottom(el), savedAt: Date.now() };
    }

    function save() {
        clearTimeout(saveTimer);
        saveTimer = null;
        // Before the restore ends the live transcript may not have caught up yet: keep the old one.
        if (signedOut || !ended) return;
        const el = live();
        if (!el) return;
        if (holdSavesUntil > 0) {
            if (messages(el).length < holdSavesUntil) return;
            holdSavesUntil = 0;
        }
        try {
            window.localStorage.setItem(KEY, JSON.stringify(capture(el)));
        } catch (e) {
            // Full or blocked: drop the old one rather than restore something stale next time.
            clear();
        }
    }

    function saveSoon() {
        if (saveTimer === null) saveTimer = setTimeout(function () { idle(save); }, SAVE_DELAY_MS);
    }

    // After the restore: keep the snapshot current with the live transcript and its position.
    function watchLive(el) {
        if (liveObserver) liveObserver.disconnect();
        watched = el;
        liveObserver = new MutationObserver(saveSoon);
        liveObserver.observe(el, { childList: true, subtree: true, characterData: true, attributes: true });
        el.addEventListener('scroll', saveSoon, { passive: true });
    }

    function start() {
        if (!live()) {
            // Not the chat page: signed out, an expired session's sign-in page, access denied.
            clear();
            return;
        }

        const snapshot = read();
        restoring = !!snapshot && !!shell();
        if (restoring) {
            snapshotHtml = snapshot.html;
            const s = shell();
            fill(s);
            snapshotCount = messages(s).length;
            document.body.classList.add(RESTORING);
            desired = { fromBottom: snapshot.fromBottom || 0 };
            place(s);
            placedShell = s;
        } else {
            desired = { lastMessage: true };
            placedLive = live();
            place(placedLive);
        }

        for (const type of INTERACTIONS)
            document.addEventListener(type, noteInteraction, { passive: true, capture: true });
        document.addEventListener('scroll', onScroll, { passive: true, capture: true });
        new MutationObserver(onPageMutation).observe(document.body, { childList: true, subtree: true });

        // Leaving the tab (switching apps on a phone included) is the moment worth keeping.
        document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'hidden') save();
        });
        window.addEventListener('pagehide', save);

        // Signing out takes the transcript off this device.
        document.addEventListener('submit', function (e) {
            const action = e.target && e.target.getAttribute && e.target.getAttribute('action');
            if (action === '/auth/logout') {
                signedOut = true;
                clear();
            }
        }, true);
    }

    // The live transcript is ready: show it, where the reader was in the snapshot (or at the last
    // message), and keep the snapshot current from now on. caughtUp is false when the history
    // didn't load: the live transcript may then be behind the snapshot, which is kept until the
    // live one has as many messages. With onlyIfComplete, a live transcript shorter than the
    // snapshot doesn't end the restore yet. Returns whether the restore is over.
    function endRestore(caughtUp, onlyIfComplete) {
        const el = live();
        if (ended || !el) return true;
        if (onlyIfComplete && restoring && messages(el).length < snapshotCount) return false;
        ended = true;
        for (const type of INTERACTIONS)
            document.removeEventListener(type, noteInteraction, { capture: true });

        let top = null;
        if (restoring) {
            // Where the reader had the snapshot, read while it is still laid out.
            const s = shell();
            const wasPinned = !s || fromBottom(s) <= PINNED_PX;
            const shellTop = s ? s.scrollTop : 0;
            restoring = false;
            document.body.classList.remove(RESTORING);
            if (!caughtUp) holdSavesUntil = snapshotCount;

            // What came in while the page was away goes below what the snapshot had. A reader who
            // was following along starts at the first new message, read top to bottom, as
            // Chat.razor does for one that arrives live; anyone else stays where they were.
            const msgs = messages(el);
            if (msgs.length > snapshotCount)
                top = wasPinned ? messageTop(el, msgs[snapshotCount]) : shellTop;
            if (s) s.replaceChildren();
        }
        snapshotHtml = null;
        if (top !== null) jumpTo(el, top);
        else place(el);
        placedLive = el;
        watchLive(el);
        save();
        return true;
    }

    return { start: start, endRestore: endRestore };
})();

window.rbSnapshot.start();

window.chatHelpers = {
    setPageTitle: function (title) {
        document.title = title;
    },

    endRestore: function (caughtUp, onlyIfComplete) {
        return window.rbSnapshot.endRestore(!!caughtUp, !!onlyIfComplete);
    },

    isDarkMode: function () {
        return document.body.classList.contains('dark-mode');
    },

    // The light / dark choice, remembered per browser (read back by App.razor's inline script).
    setDarkMode: function (dark) {
        document.body.classList.toggle('dark-mode', !!dark);
        try {
            window.localStorage.setItem('rockbot.theme', dark ? 'dark' : 'light');
        } catch (e) {
            // Not remembered; the page still switches.
        }
    },

    // The Simple / Detailed view, remembered per browser. Storage can be unavailable (private
    // windows, blocked site data): reading then gives Simple, and the choice lasts the page only.
    getDetailLevel: function () {
        try {
            return window.localStorage.getItem('rockbot.detailLevel') === 'detailed' ? 'detailed' : 'simple';
        } catch (e) {
            return 'simple';
        }
    },

    setDetailLevel: function (level) {
        try {
            window.localStorage.setItem('rockbot.detailLevel', level === 'detailed' ? 'detailed' : 'simple');
        } catch (e) {
            // Not remembered; the page still switches.
        }
    },

    // Moves focus to the first element matching a selector, when there is one.
    focusSelector: function (selector) {
        const el = document.querySelector(selector);
        if (el) el.focus();
    },

    getBrowserTimezone: function () {
        try {
            return Intl.DateTimeFormat().resolvedOptions().timeZone;
        } catch (e) {
            return 'UTC';
        }
    },


    preventEnterNewline: function (element) {
        element.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey) {
                // Plain Enter — prevent default so the form doesn't add a newline;
                // Blazor's HandleKeyDown will call SendMessage.
                e.preventDefault();
            }
            // Shift+Enter and Ctrl+Enter: allow default so the browser inserts a newline.
        });
    },

    initInputHistory: function (element) {
        element._inputHistory = [];
        element._historyIndex = -1;
        element._draft = '';

        element.addEventListener('keydown', function (e) {
            if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;

            const history = element._inputHistory;
            if (history.length === 0 && e.key === 'ArrowUp') return;

            const value = element.value;
            const selStart = element.selectionStart;
            const selEnd = element.selectionEnd;

            if (e.key === 'ArrowUp') {
                // Only navigate if cursor is on the first line (no newline before it)
                if (value.substring(0, selStart).includes('\n')) return;

                e.preventDefault();
                if (element._historyIndex === -1) {
                    element._draft = value;
                    element._historyIndex = history.length - 1;
                } else if (element._historyIndex > 0) {
                    element._historyIndex--;
                }
                element.value = history[element._historyIndex];

            } else { // ArrowDown
                if (element._historyIndex === -1) return;
                // Only navigate if cursor is on the last line (no newline after it)
                if (value.substring(selEnd).includes('\n')) return;

                e.preventDefault();
                if (element._historyIndex < history.length - 1) {
                    element._historyIndex++;
                    element.value = history[element._historyIndex];
                } else {
                    // Past the end — restore the draft
                    element._historyIndex = -1;
                    element.value = element._draft;
                }
            }

            element.selectionStart = element.selectionEnd = element.value.length;
            // Notify Blazor's @bind so currentMessage stays in sync
            element.dispatchEvent(new Event('input', { bubbles: true }));
        });
    },

    addToHistory: function (element, text) {
        if (!text || !text.trim()) return;
        const trimmed = text.trim();
        const history = element._inputHistory || (element._inputHistory = []);
        // Skip consecutive duplicates
        if (history.length === 0 || history[history.length - 1] !== trimmed) {
            history.push(trimmed);
        }
        element._historyIndex = -1;
        element._draft = '';
    },

    focusInputOnWindowFocus: function (element) {
        const focusable = 'input, textarea, button, select, a[href], [tabindex]:not([tabindex="-1"])';
        let activatedByInteractiveClick = false;
        let reclaimActive = false;
        let reclaimTimer = null;

        // When the window loses focus, cancel any pending reclaim guard.
        window.addEventListener('blur', function () {
            activatedByInteractiveClick = false;
            reclaimActive = false;
            clearTimeout(reclaimTimer);
        });

        // pointerdown fires before window.focus, so we know whether the click
        // that is about to activate the window targeted an interactive element.
        document.addEventListener('pointerdown', function (e) {
            activatedByInteractiveClick = !!e.target.closest(focusable);
        });

        // When the window gains focus, auto-focus the textarea unless:
        //   • the user clicked an interactive element (let the browser handle focus), or
        //   • the textarea is disabled (message is being processed).
        window.addEventListener('focus', function () {
            if (!activatedByInteractiveClick && !element.disabled) {
                // Arm the reclaim guard: for the next 500 ms, if focus leaves
                // the textarea to body/null we'll immediately pull it back.
                // This handles both the initial browser focus-reset and any
                // subsequent Blazor render-cycle focus disruptions.
                reclaimActive = true;
                clearTimeout(reclaimTimer);
                reclaimTimer = setTimeout(function () { reclaimActive = false; }, 500);

                // Also do an immediate deferred focus so the textarea is set
                // right after the browser finishes processing the activating click.
                setTimeout(function () {
                    const active = document.activeElement;
                    const onBody = !active || active === document.body || active === document.documentElement;
                    if (onBody && !element.disabled) {
                        element.focus();
                    }
                }, 0);
            }
            activatedByInteractiveClick = false; // reset for next activation
        });

        // Reclaim guard: while reclaimActive, if focus drifts from the textarea
        // to body/null (browser reset or Blazor render), pull it back immediately.
        // We allow focus to leave for real interactive targets (buttons, links, etc.).
        element.addEventListener('focusout', function (e) {
            if (!reclaimActive || element.disabled || !document.hasFocus()) return;
            const to = e.relatedTarget;
            // Genuine interactive target — let it go.
            if (to && to !== document.body && to !== document.documentElement) return;
            // Focus went to body/null — reclaim on next animation frame so the
            // browser has finished its own focus bookkeeping.
            requestAnimationFrame(function () {
                if (reclaimActive && !element.disabled && document.hasFocus() &&
                    (!document.activeElement ||
                     document.activeElement === document.body ||
                     document.activeElement === document.documentElement)) {
                    element.focus();
                }
            });
        });
    }
};
