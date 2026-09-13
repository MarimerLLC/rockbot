// Remembers which identity provider you last signed in with, and nothing else.
//
// Deliberately does NOT auto-redirect on a remembered provider. Bouncing straight into the last
// provider makes the page impossible to get past when you need to switch accounts, and turns a
// denied account into a redirect loop you cannot escape from the UI. Remember the choice; don't
// make it.
//
// The key is namespaced because localStorage is shared with everything else on this origin.
(function () {
    'use strict';

    var KEY = 'rockbot.auth.lastProvider';
    var container = document.querySelector('[data-login-providers]');
    if (!container) {
        return;
    }

    var buttons = Array.prototype.slice.call(container.querySelectorAll('[data-provider]'));

    function readLast() {
        // Private-browsing modes and blocked site data make this throw rather than return null.
        try {
            return window.localStorage.getItem(KEY);
        } catch (e) {
            return null;
        }
    }

    var last = readLast();
    if (last) {
        for (var i = 0; i < buttons.length; i++) {
            if (buttons[i].getAttribute('data-provider') === last) {
                var badge = document.createElement('span');
                badge.className = 'login-badge';
                badge.textContent = 'Last used';
                buttons[i].appendChild(badge);
                container.insertBefore(buttons[i], container.firstChild);
                break;
            }
        }
    }

    buttons.forEach(function (button) {
        button.addEventListener('click', function () {
            try {
                window.localStorage.setItem(KEY, button.getAttribute('data-provider'));
            } catch (e) {
                // A remembered provider is a convenience; failing to store it must not block sign-in.
            }
        });
    });
})();
