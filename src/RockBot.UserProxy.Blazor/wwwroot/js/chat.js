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
    }
};
