window.chatHelpers = {
    preventEnterNewline: function (element) {
        element.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
            }
        });
    }
};
