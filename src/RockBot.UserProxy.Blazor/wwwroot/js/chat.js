window.chatHelpers = {
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
