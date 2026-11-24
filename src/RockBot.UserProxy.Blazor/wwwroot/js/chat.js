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
    const SAVE_DELAY_MS = 1500;
    const RESTORING = 'rb-snapshot';
    const INTERACTIONS = ['wheel', 'touchmove', 'keydown', 'pointerdown'];

    let snapshotHtml = null; // shown while restoring
    let restoring = false;   // the snapshot is on screen, the live transcript hidden
    let ended = false;       // endRestore has run: the live transcript is on screen
    let interacted = false;  // the reader has touched the page, so a scroll may be theirs
    let signedOut = false;
    // Where the transcript on screen should sit: { fromBottom: px } or { lastMessage: true }.
    let desired = null;
    let watched = null;      // the live .chat-messages being saved from
    let liveObserver = null;
    let saveTimer = null;

    function live() { return document.querySelector('.chat-messages:not(.chat-snapshot)'); }
    function shell() { return document.querySelector('.chat-snapshot'); }
    function onScreen() { return restoring ? shell() : live(); }

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

    // The top of the last message: where Chat.razor scrolls a new message to.
    function lastMessageTop(el) {
        const msgs = el.querySelectorAll('.message-wrapper');
        if (!msgs.length) return el.scrollHeight;
        const last = msgs[msgs.length - 1];
        return last.getBoundingClientRect().top - el.getBoundingClientRect().top + el.scrollTop;
    }

    function place(el) {
        if (!el || !desired) return;
        if (desired.lastMessage) jumpTo(el, lastMessageTop(el));
        else jumpTo(el, el.scrollHeight - el.clientHeight - desired.fromBottom);
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
    // here means neither is ever seen.
    function onPageMutation() {
        if (restoring) {
            const s = shell();
            if (s && !s.firstChild) s.innerHTML = snapshotHtml;
            place(s);
        } else if (!ended) {
            place(live());
        } else {
            const el = live();
            if (el && el !== watched) {
                place(el);
                watchLive(el);
            }
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
        try {
            window.localStorage.setItem(KEY, JSON.stringify(capture(el)));
        } catch (e) {
            // Full or blocked: drop the old one rather than restore something stale next time.
            clear();
        }
    }

    function saveSoon() {
        if (saveTimer === null) saveTimer = setTimeout(save, SAVE_DELAY_MS);
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
            shell().innerHTML = snapshotHtml;
            document.body.classList.add(RESTORING);
            desired = { fromBottom: snapshot.fromBottom || 0 };
        } else {
            desired = { lastMessage: true };
        }
        place(onScreen());

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

    // The live transcript has caught up with the server: show it, where the reader was in the
    // snapshot (or at the last message), and keep the snapshot current from now on.
    function endRestore() {
        const el = live();
        if (ended || !el) return;
        ended = true;
        for (const type of INTERACTIONS)
            document.removeEventListener(type, noteInteraction, { capture: true });
        if (restoring) {
            restoring = false;
            document.body.classList.remove(RESTORING);
            const s = shell();
            if (s) s.replaceChildren();
        }
        snapshotHtml = null;
        place(el);
        watchLive(el);
        save();
    }

    return { start: start, endRestore: endRestore };
})();

window.rbSnapshot.start();

window.chatHelpers = {
    setPageTitle: function (title) {
        document.title = title;
    },

    endRestore: function () {
        window.rbSnapshot.endRestore();
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
