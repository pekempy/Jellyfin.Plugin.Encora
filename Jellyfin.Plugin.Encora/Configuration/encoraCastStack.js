/**
 * Encora Cast Stack — injects a small cast-avatar stack under the episode
 * description, but ONLY for items whose path lives under the Theatre library.
 * Loaded as a same-origin plugin resource so Jellyfin's CSP allows it.
 */
(function () {
    'use strict';

    var THEATRE_PATH   = '/plex-drive/Plex/Theatre/';
    var MAX_SHOW       = 8;
    var STACK_ID       = 'encora-cast-stack';
    var STYLE_ID       = 'encora-cast-style';

    /* ── CSS ─────────────────────────────────────────────────────────────── */
    function injectStyles() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.textContent = [
            '#' + STACK_ID + '{',
            '  display:flex;align-items:center;',
            '  margin:8px 0 16px;padding:0;',
            '}',
            '.ecs-av{',
            '  width:40px;height:40px;border-radius:50%;overflow:hidden;',
            '  border:2px solid rgba(0,0,0,.55);',
            '  margin-left:-10px;background:#3a3a4a;',
            '  display:flex;align-items:center;justify-content:center;',
            '  font-size:13px;font-weight:700;color:#fff;',
            '  cursor:default;flex-shrink:0;',
            '  transition:transform .15s,box-shadow .15s;',
            '}',
            '#' + STACK_ID + ' .ecs-av:first-child{margin-left:0}',
            '.ecs-av:hover{transform:translateY(-5px);z-index:99!important;',
            '  box-shadow:0 6px 14px rgba(0,0,0,.5)}',
            '.ecs-av img{width:100%;height:100%;object-fit:cover;display:block}',
            '.ecs-av-more{background:#555;font-size:11px}'
        ].join('');
        document.head.appendChild(s);
    }

    /* ── API helpers ─────────────────────────────────────────────────────── */
    function client()  { return window.ApiClient || null; }
    function userId()  { var c = client(); return c ? c.getCurrentUserId() : null; }
    function baseUrl() { var c = client(); return c ? (c.serverAddress ? c.serverAddress() : '') : ''; }

    function apiFetch(path) {
        var c = client();
        if (!c) return Promise.resolve(null);
        var url = c.getUrl ? c.getUrl(path) : (baseUrl() + '/' + path);
        return fetch(url, {
            headers: { 'Authorization': 'MediaBrowser Token="' + (c.accessToken ? c.accessToken() : '') + '"' }
        })
        .then(function (r) { return r.ok ? r.json() : null; })
        .catch(function () { return null; });
    }

    /* ── DOM helpers ─────────────────────────────────────────────────────── */
    function findOverview() {
        // Try several selectors across Jellyfin versions
        return (
            document.querySelector('.overview') ||
            document.querySelector('[class*="overview"]') ||
            document.querySelector('.itemDetailPage .detailText') ||
            null
        );
    }

    /* ── Core logic ──────────────────────────────────────────────────────── */
    function getIdFromHash() {
        var m = window.location.hash.match(/[?&]id=([a-f0-9-]{32,36})/i);
        return m ? m[1] : null;
    }

    function renderStack(people) {
        var old = document.getElementById(STACK_ID);
        if (old) old.remove();

        var overview = findOverview();
        if (!overview) return;

        var wrap = document.createElement('div');
        wrap.id = STACK_ID;

        var show = people.slice(0, MAX_SHOW);
        var extra = people.length - MAX_SHOW;

        show.forEach(function (p, i) {
            var el = document.createElement('div');
            el.className = 'ecs-av';
            el.title = p.Role ? (p.Name + ' — ' + p.Role) : p.Name;
            el.style.zIndex = MAX_SHOW - i;

            if (p.PrimaryImageTag) {
                var img = document.createElement('img');
                img.src = baseUrl() + '/Items/' + p.Id +
                          '/Images/Primary?fillHeight=44&fillWidth=44&quality=90&tag=' + p.PrimaryImageTag;
                img.alt = p.Name;
                el.appendChild(img);
            } else {
                el.textContent = (p.Name || '?')[0].toUpperCase();
            }
            wrap.appendChild(el);
        });

        if (extra > 0) {
            var more = document.createElement('div');
            more.className = 'ecs-av ecs-av-more';
            more.title = people.slice(MAX_SHOW).map(function (p) { return p.Name; }).join(', ');
            more.textContent = '+' + extra;
            wrap.appendChild(more);
        }

        overview.insertAdjacentElement('afterend', wrap);
    }

    function tryRender() {
        var old = document.getElementById(STACK_ID);
        if (old) old.remove();

        var id = getIdFromHash();
        if (!id || !userId()) return;

        apiFetch('Users/' + userId() + '/Items/' + id)
            .then(function (item) {
                if (!item) return null;
                if (item.Type !== 'Episode') return null;
                if (typeof item.Path !== 'string' || item.Path.indexOf(THEATRE_PATH) !== 0) return null;
                return apiFetch('Items/' + id + '/People?PersonTypes=Actor');
            })
            .then(function (data) {
                if (!data || !data.Items || !data.Items.length) return;
                // Wait for Jellyfin's own DOM to finish painting
                setTimeout(function () { renderStack(data.Items); }, 250);
            });
    }

    /* ── Navigation watcher ──────────────────────────────────────────────── */
    var lastHash = '';
    function checkNav() {
        var h = window.location.hash;
        if (h !== lastHash) {
            lastHash = h;
            if (h.indexOf('details') !== -1) {
                setTimeout(tryRender, 700);
            } else {
                var old = document.getElementById(STACK_ID);
                if (old) old.remove();
            }
        }
    }

    injectStyles();
    setInterval(checkNav, 250);

    // Handle hard-load directly onto a details page
    if (window.location.hash.indexOf('details') !== -1) {
        setTimeout(tryRender, 1200);
    }
}());
