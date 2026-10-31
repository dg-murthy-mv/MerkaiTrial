/* =====================================================================
   wwwroot/js/record-search.js                              (Round 108)

   A search box for a long drop-down — contacts, companies, deals.

   The <select> stays exactly as it was (same id, same name, same posted
   value, still keyboard- and screen-reader-friendly). The server fills it
   with a short starting list — the newest 20, plus whatever is already
   chosen. The box above it asks the page for matches as you type, from the
   WHOLE workspace, and puts them in the drop-down.

   Markup:
     <input type="search" data-record-search="pcContact"
            data-url="/Pipeline/Create?handler=FindContacts"
            data-placeholder-text="Choose a contact…" ...>
     <div data-record-search-status="pcContact"></div>
     <select id="pcContact" ...>…</select>

   The page handler answers JSON: [{ id, text, data: { "company-id": "…" } }]
   Each "data" entry becomes a data-* attribute on the option, so code that
   reads the chosen option (the contact → company auto-fill) keeps working.

   Typing:
     • fewer than 2 characters  → the starting list comes back
     • 2 or more                → matches replace it, after a short pause
     • nothing chosen yet       → the first match is chosen for you (and the
                                  select's "change" fires, as if picked)
     • something chosen already → it stays chosen and stays in the list
   Text is always set with textContent — names are the workspace's own
   text and may contain anything.
   ===================================================================== */
(function () {
    'use strict';

    var MIN = 2;
    var PAUSE_MS = 250;

    function setup(box) {
        var select = document.getElementById(box.getAttribute('data-record-search'));
        if (!select || box.__recordSearch) return;
        box.__recordSearch = true;

        var url = box.getAttribute('data-url');
        var status = document.querySelector('[data-record-search-status="' + select.id + '"]');
        var initial = Array.prototype.map.call(select.options, function (o) { return o.cloneNode(true); });
        var timer = null;
        var latest = 0;

        function say(text) { if (status) status.textContent = text || ''; }

        function placeholder() {
            var first = initial.length && initial[0].value === '' ? initial[0] : null;
            return first ? first.cloneNode(true) : null;
        }

        function replaceOptions(options) {
            var keep = select.value;
            var keptOption = keep ? select.options[select.selectedIndex].cloneNode(true) : null;

            while (select.options.length) select.remove(0);

            var ph = placeholder();
            if (ph) select.appendChild(ph);

            var hasKept = false;
            options.forEach(function (o) {
                if (keep && o.value === keep) hasKept = true;
                select.appendChild(o);
            });

            // What was chosen stays chosen, even when the search does not match it.
            if (keep && !hasKept && keptOption) select.insertBefore(keptOption, select.options[ph ? 1 : 0] || null);
            if (keep) select.value = keep;
        }

        function restore() {
            replaceOptions(initial.filter(function (o) { return o.value !== ''; })
                                  .map(function (o) { return o.cloneNode(true); }));
            say('');
        }

        function toOption(item) {
            var o = document.createElement('option');
            o.value = item.id;
            o.textContent = item.text;
            if (item.data) {
                Object.keys(item.data).forEach(function (k) {
                    o.setAttribute('data-' + k, item.data[k] == null ? '' : String(item.data[k]));
                });
            }
            return o;
        }

        function search(q) {
            var mine = ++latest;
            say('Searching…');
            fetch(url + (url.indexOf('?') >= 0 ? '&' : '?') + 'q=' + encodeURIComponent(q), {
                credentials: 'same-origin',
                headers: { 'Accept': 'application/json' }
            })
                .then(function (r) {
                    if (!r.ok) throw new Error('HTTP ' + r.status);
                    return r.json();
                })
                .then(function (items) {
                    if (mine !== latest) return;               // a newer search has started
                    items = Array.isArray(items) ? items : [];
                    var hadValue = !!select.value;
                    replaceOptions(items.map(toOption));

                    if (items.length === 0) say('No matches for "' + q + '".');
                    else if (items.length >= 20) say('Showing the first ' + items.length + ' matches — type more to narrow them.');
                    else say(items.length === 1 ? '1 match.' : items.length + ' matches.');

                    if (!hadValue && items.length > 0) {
                        select.value = items[0].id;
                        select.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                })
                .catch(function () {
                    if (mine !== latest) return;
                    say('The search did not work just now. Please try again.');
                });
        }

        box.addEventListener('input', function () {
            clearTimeout(timer);
            var q = box.value.trim();
            if (q.length < MIN) { latest++; restore(); return; }
            timer = setTimeout(function () { search(q); }, PAUSE_MS);
        });

        // Enter in the box searches now instead of submitting the form.
        box.addEventListener('keydown', function (e) {
            if (e.key !== 'Enter') return;
            e.preventDefault();
            clearTimeout(timer);
            var q = box.value.trim();
            if (q.length >= MIN) search(q);
        });
    }

    function init() {
        document.querySelectorAll('[data-record-search]').forEach(setup);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
