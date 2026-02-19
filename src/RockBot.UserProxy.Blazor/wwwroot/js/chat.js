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

        // When the window loses focus, reset so that Alt+Tab back always focuses the textarea.
        window.addEventListener('blur', function () {
            activatedByInteractiveClick = false;
        });

        // pointerdown fires before window.focus, so we know whether the click
        // that is about to activate the window targeted an interactive element.
        document.addEventListener('pointerdown', function (e) {
            activatedByInteractiveClick = !!e.target.closest(focusable);
        });

        // When the window gains focus, auto-focus the textarea unless:
        //   • the user clicked an interactive element (let the browser handle focus), or
        //   • the textarea is disabled (message is being processed).
        //
        // We defer via setTimeout(0) so this runs AFTER the browser finishes
        // processing the activating click. Without the defer, the browser's own
        // click-focus logic (which may reset focus to document.body for clicks on
        // non-focusable elements) fires after our handler and steals focus back.
        window.addEventListener('focus', function () {
            if (!activatedByInteractiveClick && !element.disabled) {
                setTimeout(function () {
                    // Only claim focus when nothing interactive has it already.
                    const active = document.activeElement;
                    const focusOnBody = !active || active === document.body || active === document.documentElement;
                    if (focusOnBody && !element.disabled) {
                        element.focus();
                    }
                }, 0);
            }
            activatedByInteractiveClick = false; // reset for next activation
        });
    }
};
