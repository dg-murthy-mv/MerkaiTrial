// =====================================================================
// FILE: wwwroot/js/ai-assistant.js
//
// NEW (122). The chat used by both the "Ask Merkai" panel (in the
// workspace layout) and the /Assistant page. Wire one with
//     MerkaiAssistant.attach(rootElement)
// where rootElement holds: .ai-log (the conversation), form.ai-form with a
// textarea[name=question], .ai-status, and an anti-forgery token input.
//
// • Everything shown is put in as TEXT (textContent), never as HTML: an
//   answer — or a question — containing markup is shown, not run.
// • 122-update1: once wired, the root gets data-ai-ready="1" (the tests
//   wait for it before typing).
// • 123: answers are FORMATTED — paragraphs, **bold**, "-" bullet lists,
//   "1." numbered lists — by building elements one by one (createElement +
//   textContent), never by inserting HTML. A link becomes a link ONLY when
//   it points at a record page in Merkai itself (/Leads/Detail/<id>,
//   /Pipeline/Detail/<id>, /Companies/Detail/<id>, /Contacts|Quotes|
//   Invoices/Detail?id=<id>, /Tasks); any other link (another site,
//   javascript:, …) is shown as its words only. Under an answer that used
//   records, a small line says what the assistant looked up.
// • 124: MerkaiAssistant.attachHelper(card) wires the record helpers on a
//   lead's or deal's page (_AiRecordHelper): each button posts kind, type,
//   id and the optional note to /Assistant?handler=Help and shows the
//   answer, formatted the same way; Copy copies it as plain text.
// • 125: PREPARED CHANGES. An answer can carry proposals (a task, a logged
//   activity, a note). Each shows as a card under the answer — Merkai's
//   own sentence, set as text — with Confirm and Cancel. Nothing happens
//   until Confirm (POST /Assistant?handler=Confirm). The helpers card gets
//   "Save as note" (POST ?handler=SaveNote) for the answer shown.
// • The conversation lives in this tab only (memory), and the last six
//   messages are sent with each question so follow-ups make sense.
// • Enter sends; Shift+Enter is a new line.
// =====================================================================
(function () {
    'use strict';
    if (window.MerkaiAssistant) return;

    var ASK_URL = '/Assistant?handler=Ask';
    var STATUS_URL = '/Assistant?handler=Status';
    var MAX = 1000;

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }

    // ── 123: safe formatting ────────────────────────────────────────
    var SAFE_LINK = /^\/(?:(?:Leads|Pipeline|Companies)\/Detail\/[0-9a-fA-F-]{36}|(?:Contacts|Quotes|Invoices)\/Detail\?id=[0-9a-fA-F-]{36}|Tasks)$/;
    var INLINE = /\*\*([^*\n]+)\*\*|\[([^\]\n]+)\]\(([^)\s]+)\)|`([^`\n]+)`/g;
    var LOOKED = {
        find_records: 'searched records', get_record: 'record details', my_tasks: 'your tasks',
        pipeline_summary: 'pipeline', lead_summary: 'leads summary', unpaid_invoices: 'unpaid invoices'
    };

    function inline(parent, text) {
        var last = 0, m;
        INLINE.lastIndex = 0;
        while ((m = INLINE.exec(text)) !== null) {
            if (m.index > last) parent.appendChild(document.createTextNode(text.slice(last, m.index)));
            if (m[1] != null) {
                parent.appendChild(el('strong', null, m[1]));
            } else if (m[2] != null) {
                if (SAFE_LINK.test(m[3])) {
                    var a = el('a', 'ai-link', m[2]);
                    a.setAttribute('href', m[3]);
                    parent.appendChild(a);
                } else {
                    parent.appendChild(document.createTextNode(m[2]));   // the words only
                }
            } else if (m[4] != null) {
                parent.appendChild(document.createTextNode(m[4]));
            }
            last = INLINE.lastIndex;
        }
        if (last < text.length) parent.appendChild(document.createTextNode(text.slice(last)));
    }

    function renderRich(bubble, text) {
        var lines = String(text || '').replace(/\r\n?/g, '\n').split('\n');
        var para = null, list = null, listKind = null;

        function endPara() { para = null; }
        function endList() { list = null; listKind = null; }

        lines.forEach(function (raw) {
            var line = raw.replace(/\s+$/, '');
            var bullet = /^\s*[-*\u2022]\s+(.*)$/.exec(line);
            var number = /^\s*(\d{1,3})[.)]\s+(.*)$/.exec(line);
            var heading = /^\s*#{1,6}\s+(.*)$/.exec(line);

            if (!line.trim()) { endPara(); endList(); return; }

            if (bullet || number) {
                endPara();
                var kind = bullet ? 'ul' : 'ol';
                if (!list || listKind !== kind) {
                    list = el(kind);
                    listKind = kind;
                    if (number && number[1] !== '1') list.setAttribute('start', number[1]);
                    bubble.appendChild(list);
                }
                var li = el('li');
                inline(li, bullet ? bullet[1] : number[2]);
                list.appendChild(li);
                return;
            }

            endList();
            if (heading) {
                endPara();
                var h = el('p', 'ai-heading');
                h.appendChild(el('strong', null, heading[1].replace(/\*\*/g, '')));
                bubble.appendChild(h);
                return;
            }
            if (!para) { para = el('p'); bubble.appendChild(para); }
            else para.appendChild(el('br'));
            inline(para, line);
        });
    }

    function attach(root) {
        if (!root || root.__ai) return;
        root.__ai = true;

        var log = root.querySelector('.ai-log');
        var form = root.querySelector('form.ai-form');
        var box = form.querySelector('textarea[name=question]');
        var send = form.querySelector('button[type=submit]');
        var status = root.querySelector('.ai-status');
        var counter = root.querySelector('.ai-count');
        var history = [];
        var busy = false;

        function add(role, text, extra, rich) {
            var row = el('div', 'ai-msg ai-' + role + (extra ? ' ' + extra : ''));
            row.setAttribute('data-role', role);
            var bubble;
            if (rich) { bubble = el('div', 'ai-bubble ai-rich'); renderRich(bubble, text); }
            else bubble = el('div', 'ai-bubble', text);
            row.appendChild(bubble);
            log.appendChild(row);
            log.scrollTop = log.scrollHeight;
            return row;
        }

        function say(text, kind) {
            if (!status) return;
            status.textContent = text || '';
            status.className = 'ai-status small ' + (kind === 'error' ? 'text-danger' : 'text-muted');
        }

        function refreshStatus() {
            fetch(STATUS_URL, { credentials: 'same-origin' })
                .then(function (r) { return r.json(); })
                .then(function (s) {
                    if (s.error) { say(s.error, 'error'); return; }
                    if (!s.canAsk) { say(s.reason || 'The assistant is not available.', 'error'); send.disabled = true; box.disabled = true; return; }
                    say(s.remaining + ' question' + (s.remaining === 1 ? '' : 's') + ' left this month.');
                })
                .catch(function () { say('The assistant could not be reached just now.', 'error'); });
        }

        box.addEventListener('input', function () {
            if (counter) counter.textContent = box.value.length + ' / ' + MAX;
        });
        box.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); form.requestSubmit ? form.requestSubmit() : form.submit(); }
        });

        form.addEventListener('submit', function (e) {
            e.preventDefault();
            if (busy) return;
            var q = (box.value || '').trim();
            if (!q) { say('Type a question first.', 'error'); return; }
            if (q.length > MAX) { say('Questions can be up to ' + MAX + ' characters. Please shorten it.', 'error'); return; }

            var token = root.querySelector('input[name="__RequestVerificationToken"]');
            var body = new URLSearchParams();
            body.set('question', q);
            body.set('history', JSON.stringify(history.slice(-6)));

            add('user', q);
            box.value = '';
            if (counter) counter.textContent = '0 / ' + MAX;
            var thinking = add('assistant', 'Thinking…', 'ai-thinking');
            busy = true; send.disabled = true; say('');

            fetch(ASK_URL, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'RequestVerificationToken': token ? token.value : '' },
                body: body
            }).then(function (r) {
                return r.json().catch(function () { return { error: 'The assistant couldn\'t answer just now.' }; });
            }).then(function (res) {
                thinking.remove();
                if (res && res.answer) {
                    var row = add('assistant', res.answer, null, true);
                    if (res.looked && res.looked.length) {
                        var note = el('div', 'ai-looked small text-muted',
                            'Looked up: ' + res.looked.map(function (n) { return LOOKED[n] || n; }).join(', '));
                        row.querySelector('.ai-bubble').appendChild(note);
                    }
                    if (res.proposals && res.proposals.length) {
                        row.classList.add('ai-has-proposals');
                        res.proposals.forEach(function (p) { row.appendChild(proposalCard(p, root)); });
                    }
                    history.push({ role: 'user', text: q });
                    history.push({ role: 'assistant', text: res.answer });
                    say(res.remaining + ' question' + (res.remaining === 1 ? '' : 's') + ' left this month.');
                } else {
                    add('assistant', (res && res.error) || 'The assistant couldn\'t answer just now.', 'ai-error');
                }
            }).catch(function () {
                thinking.remove();
                add('assistant', 'The assistant couldn\'t be reached. Please check your connection and try again.', 'ai-error');
            }).finally(function () {
                busy = false; send.disabled = false; box.focus();
            });
        });

        root.setAttribute('data-ai-ready', '1');
        refreshStatus();
    }

    // ── 125: prepared changes ───────────────────────────────────────
    var KIND_LABEL = { task: 'Task', log: 'Log activity', note: 'Note' };

    function postForm(root, handler, fields) {
        var token = root.querySelector('input[name="__RequestVerificationToken"]');
        var body = new URLSearchParams();
        Object.keys(fields).forEach(function (k) { body.set(k, fields[k]); });
        return fetch('/Assistant?handler=' + handler, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'RequestVerificationToken': token ? token.value : '' },
            body: body
        }).then(function (r) {
            return r.json().catch(function () { return { error: 'That couldn\'t be done just now.' }; });
        }, function () {
            return { error: 'Merkai couldn\'t be reached. Please check your connection and try again.' };
        });
    }

    function proposalCard(p, root) {
        var card = el('div', 'ai-proposal');
        card.setAttribute('data-id', p.id);
        card.setAttribute('data-kind', p.kind);
        var head = el('div', 'ai-proposal-head small fw-semibold');
        head.appendChild(el('i', 'bi bi-hand-index-thumb me-1'));
        head.appendChild(document.createTextNode((KIND_LABEL[p.kind] || 'Change') + ' — needs your OK'));
        card.appendChild(head);
        card.appendChild(el('div', 'ai-proposal-text', p.summary));   // Merkai's sentence, as text

        var bar = el('div', 'ai-proposal-bar');
        var ok = el('button', 'btn btn-sm btn-primary ai-confirm', 'Confirm');
        ok.type = 'button';
        var no = el('button', 'btn btn-sm btn-outline-secondary ai-cancel', 'Cancel');
        no.type = 'button';
        bar.appendChild(ok);
        bar.appendChild(no);
        if (SAFE_LINK.test(p.link || '')) {
            var a = el('a', 'small ms-auto', 'Open record');
            a.setAttribute('href', p.link);
            bar.appendChild(a);
        }
        card.appendChild(bar);
        var result = el('div', 'ai-proposal-result small');
        card.appendChild(result);

        function decide(handler) {
            ok.disabled = no.disabled = true;
            result.className = 'ai-proposal-result small text-muted';
            result.textContent = handler === 'Confirm' ? 'Saving…' : 'Cancelling…';
            postForm(root, handler, { id: p.id }).then(function (res) {
                if (res && res.done) {
                    card.classList.add(handler === 'Confirm' ? 'is-done' : 'is-cancelled');
                    bar.removeChild(ok); bar.removeChild(no);
                    result.className = 'ai-proposal-result small ' + (handler === 'Confirm' ? 'text-success' : 'text-muted');
                    result.textContent = (handler === 'Confirm' ? '\u2713 ' : '') + res.message;
                } else {
                    result.className = 'ai-proposal-result small text-danger ai-error';
                    result.textContent = (res && (res.error || res.message)) || 'That couldn\'t be done just now.';
                    ok.disabled = no.disabled = false;
                    if (res && res.message && !res.error) { bar.removeChild(ok); bar.removeChild(no); }
                }
            });
        }
        ok.addEventListener('click', function () { decide('Confirm'); });
        no.addEventListener('click', function () { decide('Cancel'); });
        return card;
    }

    // ── 124: the record helpers ─────────────────────────────────────
    var HELP_URL = '/Assistant?handler=Help';
    var HELP_LABEL = { summary: 'Summarising…', next_step: 'Thinking about the next step…', draft_email: 'Drafting an email…', draft_message: 'Drafting a message…' };

    function attachHelper(root) {
        if (!root || root.__aiHelp) return;
        root.__aiHelp = true;

        var buttons = Array.prototype.slice.call(root.querySelectorAll('button[data-kind]'));
        var note = root.querySelector('#aiHelperNote');
        var result = root.querySelector('.ai-helper-result');
        var out = root.querySelector('#aiHelperOut');
        var status = root.querySelector('.ai-help-status');
        var copy = root.querySelector('#aiHelperCopy');
        var save = root.querySelector('#aiHelperSave');   // 125
        var plain = '';

        function busy(on) { buttons.forEach(function (b) { b.disabled = on; }); }

        buttons.forEach(function (b) {
            b.addEventListener('click', function () {
                var kind = b.getAttribute('data-kind');
                var token = root.querySelector('input[name="__RequestVerificationToken"]');
                var body = new URLSearchParams();
                body.set('kind', kind);
                body.set('type', root.getAttribute('data-type'));
                body.set('id', root.getAttribute('data-id'));
                if (kind.indexOf('draft_') === 0 && note && note.value.trim()) body.set('note', note.value.trim().slice(0, 300));

                result.classList.remove('d-none');
                out.className = 'ai-bubble ai-help-out ai-thinking-text';
                out.textContent = HELP_LABEL[kind] || 'Thinking…';
                status.textContent = '';
                copy.disabled = true;
                if (save) save.disabled = true;
                plain = '';
                busy(true);

                fetch(HELP_URL, {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'RequestVerificationToken': token ? token.value : '' },
                    body: body
                }).then(function (r) {
                    return r.json().catch(function () { return { error: 'The assistant couldn\'t answer just now.' }; });
                }).then(function (res) {
                    out.textContent = '';
                    if (res && res.answer) {
                        out.className = 'ai-bubble ai-help-out ai-rich';
                        out.setAttribute('data-kind', kind);
                        renderRich(out, res.answer);
                        plain = res.answer.replace(/\*\*([^*\n]+)\*\*/g, '$1').replace(/\[([^\]\n]+)\]\([^)\s]+\)/g, '$1');
                        copy.disabled = false;
                        if (save) save.disabled = false;
                        status.textContent = res.remaining + ' question' + (res.remaining === 1 ? '' : 's') + ' left this month.';
                    } else {
                        out.className = 'ai-bubble ai-help-out ai-error';
                        out.textContent = (res && res.error) || 'The assistant couldn\'t answer just now.';
                    }
                }).catch(function () {
                    out.className = 'ai-bubble ai-help-out ai-error';
                    out.textContent = 'The assistant couldn\'t be reached. Please check your connection and try again.';
                }).finally(function () { busy(false); });
            });
        });

        if (save) save.addEventListener('click', function () {
            if (!plain) return;
            save.disabled = true;
            status.textContent = 'Saving…';
            postForm(root, 'SaveNote', { type: root.getAttribute('data-type'), id: root.getAttribute('data-id'), text: plain })
                .then(function (res) {
                    if (res && res.done) { status.textContent = '\u2713 ' + res.message + ' Reload the page to see it in Notes.'; }
                    else { status.textContent = (res && res.error) || 'That couldn\'t be saved just now.'; save.disabled = false; }
                });
        });

        copy.addEventListener('click', function () {
            if (!plain) return;
            var done = function () { status.textContent = 'Copied.'; };
            if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(plain).then(done, function () { status.textContent = 'Select the text and copy it.'; });
            else status.textContent = 'Select the text and copy it.';
        });

        root.setAttribute('data-ai-ready', '1');
    }

    window.MerkaiAssistant = { attach: attach, attachHelper: attachHelper, render: renderRich };
})();
