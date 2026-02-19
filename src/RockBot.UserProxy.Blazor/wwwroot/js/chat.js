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
        // When the OS-level focus switches to this window, move focus to the
        // input element so the user can start typing immediately.  This only
        // fires when the window was previously out of focus (e.g. the user was
        // in another application), so it does not interfere with in-app clicks
        // where the user may be trying to select text.
        //
        // The setTimeout defers the focus call until after the browser's own
        // click-focus logic completes.  Without it, clicking a focusable element
        // (e.g. the Send button) to activate the window would cause a flicker:
        // our handler gives focus to the textarea, then the browser immediately
        // moves focus to the clicked element.  By deferring and only focusing
        // when nothing else has claimed focus (activeElement is body/null), we
        // avoid stealing focus from elements the user intentionally clicked.
        window.addEventListener('focus', function () {
            setTimeout(function () {
                if (!document.activeElement || document.activeElement === document.body) {
                    element.focus();
                }
            }, 0);
        });
    }
};
