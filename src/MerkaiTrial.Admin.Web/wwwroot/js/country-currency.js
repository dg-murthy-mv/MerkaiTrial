// =====================================================================
// country-currency.js
// Location: MerkaiTrial.Admin.Web/wwwroot/js/country-currency.js
//
// Shared module. Loaded ONCE in _Layout.cshtml.
// Exposes window.CountryMeta — used by any page needing country/currency sync.
//
// USAGE in any page script:
//   CountryMeta.onReady(({ bindCountrySelect }) => {
//       bindCountrySelect('Input_CountryId', 'Input_CurrencyId', 'currency-symbol');
//   });
// =====================================================================

(function () {
    'use strict';

    // Internal state
    let _countries = [];        // raw array from API
    let _byId      = {};        // { guid: countryObj }
    let _byCode    = {};        // { "IN": countryObj }
    let _readyCallbacks = [];
    let _ready = false;

    // ── Bootstrap: called by _Layout.cshtml after page load ──────────
    async function init(apiBaseUrl) {
        try {
            const res = await fetch(`${apiBaseUrl}api/meta/countries`);
            if (!res.ok) throw new Error(`Meta API returned ${res.status}`);

            _countries = await res.json();

            _countries.forEach(c => {
                _byId[c.id]     = c;
                _byCode[c.code] = c;
            });

            _ready = true;
            _readyCallbacks.forEach(fn => fn(publicApi));
            _readyCallbacks = [];

        } catch (err) {
            console.error('[CountryMeta] Failed to load country data:', err);
        }
    }

    // ── Public API ────────────────────────────────────────────────────

    function onReady(callback) {
        if (_ready) {
            callback(publicApi);
        } else {
            _readyCallbacks.push(callback);
        }
    }

    function getByCode(code)    { return _byCode[code?.toUpperCase()] ?? null; }
    function getById(id)        { return _byId[id]  ?? null; }
    function getAll()           { return _countries; }

    function getCurrencyByCountryId(countryId) {
        const c = _byId[countryId];
        return c ? { code: c.currencyCode, symbol: c.currencySymbol } : null;
    }

    // ── Core bind function: wires country→currency sync on any page ───
    //
    // countrySelectId : element id of the country <select>
    // currencySelectId: element id of the currency <select> (value = currencyCode string)
    // symbolElId      : element id of the <span> showing the symbol prefix
    //
    function bindCountrySelect(countrySelectId, currencySelectId, symbolElId) {
        const countryEl  = document.getElementById(countrySelectId);
        const currencyEl = document.getElementById(currencySelectId);
        const symbolEl   = document.getElementById(symbolElId);

        if (!countryEl || !currencyEl) {
            console.warn(`[CountryMeta] bindCountrySelect: elements not found`,
                countrySelectId, currencySelectId);
            return;
        }

        // Country change → auto-suggest currency
        countryEl.addEventListener('change', function () {
            const country = getById(this.value);
            if (!country) return;

            // Try to select matching currency option
            const opt = [...currencyEl.options]
                .find(o => o.value === country.currencyCode);

            if (opt) {
                currencyEl.value = country.currencyCode;
                updateSymbol(country.currencySymbol);
            }
        });

        // Currency change → update symbol only (user can override)
        currencyEl.addEventListener('change', function () {
            // Find symbol from any country using this currency
            const match = _countries.find(c => c.currencyCode === this.value);
            if (match) updateSymbol(match.currencySymbol);
        });

        // Init symbol from current selection on page load
        const initialCurrency = currencyEl.value;
        if (initialCurrency) {
            const match = _countries.find(c => c.currencyCode === initialCurrency);
            if (match) updateSymbol(match.currencySymbol);
        }

        function updateSymbol(symbol) {
            if (symbolEl) symbolEl.textContent = symbol ?? '¤';
        }
    }

    // ── Build a <datalist> or populate a <select> from country data ───
    function populateCountrySelect(selectEl, selectedId) {
        if (!selectEl) return;
        const current = selectEl.value || selectedId;
        selectEl.innerHTML = '<option value="">-- Select Country --</option>';
        _countries.forEach(c => {
            const opt = document.createElement('option');
            opt.value       = c.id;
            opt.textContent = c.name;
            if (c.id === current) opt.selected = true;
            selectEl.appendChild(opt);
        });
    }

    const publicApi = { onReady, getByCode, getById, getAll,
                        getCurrencyByCountryId, bindCountrySelect,
                        populateCountrySelect };

    // Expose globally
    window.CountryMeta = { init, ...publicApi };

})();
