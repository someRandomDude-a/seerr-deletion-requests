(async function () {
    'use strict';
    const el = id => document.getElementById(id);
    const root = new URL('./', location.href); // Works with a Jellyfin base URL as well as at origin root.
    const originRoot = new URL('../', root);
    el('jellyfinHome').href = new URL('web/', originRoot);
    const get = (o, name) => o[name] ?? o[name[0].toUpperCase() + name.slice(1)];
    let token, pref, pending, selection, expectedServerId, poll, noticeDeliveryIncomplete = false, previewGeneration = 0, offset = 0;
    const commandId = () => {
        const bytes = crypto.getRandomValues(new Uint8Array(16)); bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
        const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
        return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16) + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
    };
    const session = () => {
        try {
            const raw = localStorage.getItem('jellyfin_credentials') || '{}';
            if (raw.length > 524288) return null;
            const credentials = JSON.parse(raw);
            const found = (Array.isArray(credentials?.Servers) ? credentials.Servers : []).find(s => s && String(s.Id).toLowerCase() === String(expectedServerId).toLowerCase());
            return typeof found?.AccessToken === 'string' && found.AccessToken.length <= 1024 ? found : null;
        } catch { return null; }
    };
    function clearPrivate() {
        clearInterval(poll); token = null; pref = null; pending = null; selection = null; previewGeneration++;
        el('queue').replaceChildren(); el('notices').replaceChildren(); el('noticeCount').textContent = '0';
        el('noticeCount').hidden = true;
        el('previewTitle').textContent = ''; el('identifier').value = ''; el('personalHours').value = '48';
        el('notifications').checked = false; el('is4k').checked = false; el('noticeStatus').textContent = '';
        el('preview').hidden = true; if (el('notificationDialog').open) el('notificationDialog').close();
    }
    const fail = e => { el('error').textContent = e.message || 'Operation unavailable. Refresh to obtain the current snapshot.'; };
    el('helpToggle').onclick = () => { el('help').hidden = !el('help').hidden; el('helpToggle').setAttribute('aria-expanded', String(!el('help').hidden)); };
    async function api(path, method = 'GET', body) {
        const authenticatedToken = token;
        if (!authenticatedToken || session()?.AccessToken !== authenticatedToken) { clearPrivate(); throw new Error('Session changed. Reload after signing in.'); }
        const response = await fetch(new URL(path, root), { method, cache: 'no-store', credentials: 'omit',
            signal: AbortSignal.timeout(15000),
            headers: { Authorization: 'MediaBrowser Token="' + encodeURIComponent(authenticatedToken) + '"', ...(body ? { 'Content-Type': 'application/json' } : {}) },
            body: body ? JSON.stringify(body) : undefined });
        if (session()?.AccessToken !== authenticatedToken) { clearPrivate(); throw new Error('Session changed. Reload after signing in.'); }
        if (response.status === 401 || response.status === 403) clearPrivate();
        if (path === 'me/notices') noticeDeliveryIncomplete = response.headers.get('X-DeletionQueue-Notice-Delivery') === 'incomplete';
        if (!response.ok) { let code = 'OperationUnavailable'; try { const body = await response.json(); code = body.code || body.Code || code; } catch {} throw new Error(code); }
        return response.status === 204 ? undefined : response.json();
    }
    function text(tag, content, className) { const node = document.createElement(tag); node.textContent = content; if (className) node.className = className; return node; }
    function button(label, action) { const node = text('button', label, 'emby-button raised'); node.setAttribute('is', 'emby-button'); node.type = 'button'; node.onclick = async () => { node.disabled = true; try { await action(); el('error').textContent = ''; } catch (e) { fail(e); } finally { node.disabled = false; } }; return node; }
    function renderProposal(p) {
        const id = get(p, 'id'), revision = get(p, 'revision'), status = get(p, 'status');
        const article = document.createElement('article');
        article.id = 'proposal-' + id;
        article.append(text('h3', get(p, 'title') + ' · ' + get(p, 'ownership')),
            text('p', status + ' — ' + get(p, 'reason')),
            text('p', 'Your action: ' + get(p, 'requiredAction'), 'secondary'));
        const deadline = get(p, 'notBefore');
        if (deadline) article.append(text('p', 'Not before ' + new Date(deadline).toLocaleString() + ', subject to rechecks', 'secondary'));
        const own = get(p, 'ownAnswer');
        article.append(text('p', 'Your answer: ' + (own === 0 || own === 'Yes' ? 'Yes' : own === 1 || own === 'No' ? 'No' : 'Not answered for this snapshot'), 'secondary'));
        if (get(p, 'ownActivityAt')) article.append(text('p', 'Your activity: ' + new Date(get(p, 'ownActivityAt')).toLocaleString(), 'secondary'));
        const actions = document.createElement('div'); actions.className = 'dq-actions';
        if (!['Claimed', 'Uncertain', 'FilesDeleted', 'Completed', 'Cancelled'].includes(status)) {
            actions.append(text('span', 'Allow deletion?'));
            for (const [label, answer] of [['Yes', 0], ['No', 1]]) actions.append(button(label, async () => {
                await api('proposals/' + id + '/consent', 'POST', { commandId: commandId(), revision, snapshot: get(p, 'ownSnapshot'), answer }); await refresh();
            }));
        }
        actions.append(button('Help', async () => { el('help').hidden = false; el('helpToggle').setAttribute('aria-expanded', 'true'); el('help').scrollIntoView(); }));
        if (get(p, 'canUndo')) actions.append(button('Undo', async () => { await api('proposals/' + id + '/undo', 'POST', { commandId: commandId(), revision }); await refresh(); }));
        const subscriptions = pref ? get(pref, 'subscriptions') : [];
        const subscribed = subscriptions.includes(id);
        actions.append(button(subscribed ? 'Unsubscribe' : 'Subscribe', async () => {
            await api('proposals/' + id + '/subscription', subscribed ? 'DELETE' : 'POST', { commandId: commandId(), revision: get(pref, 'revision') }); await refresh();
        }));
        article.append(actions);
        const existing = document.getElementById(article.id);
        if (existing) existing.replaceWith(article); else el('queue').append(article);
        return article;
    }
    async function loadNotices() {
        const notices = await api('me/notices');
        const unread = notices.filter(n => get(n, 'isRead') !== true).length;
        el('noticeCount').textContent = unread >= 100 ? '100+' : String(unread);
        el('noticeCount').hidden = unread === 0;
        el('notificationBell').setAttribute('aria-label', 'Notifications: ' + unread + ' unread');
        el('noticeStatus').textContent = noticeDeliveryIncomplete ? 'Notification delivery is incomplete. Review the queue directly; consent and cancellation still require explicit actions.' : notices.length ? 'Opening notifications does not mark them read or approve deletion.' : 'No notifications.';
        el('notices').replaceChildren();
        for (const notice of notices) {
            const row = document.createElement('article'); row.className = 'listItem';
            const body = document.createElement('div'); body.className = 'listItemBody';
            body.append(text('p', get(notice, 'text'), 'listItemBodyText'));
            body.append(button('Review proposal', async () => {
                const proposal = await api('proposals/' + get(notice, 'proposalId'));
                const row = renderProposal(proposal); el('notificationDialog').close(); row.tabIndex = -1; row.focus(); row.scrollIntoView();
            }));
            if (get(notice, 'isRead') !== true) body.append(button('Mark read', async () => {
                await api('me/notices/' + get(notice, 'id') + '/read', 'POST', { commandId: commandId() }); await loadNotices();
            }));
            row.append(body); el('notices').append(row);
        }
    }
    async function loadPage() {
        const rows = await api('proposals?skip=' + offset + '&take=50');
        rows.forEach(renderProposal); offset += rows.length; el('loadMore').hidden = rows.length < 50;
    }
    async function refresh() {
        el('error').textContent = '';
        pref = await api('me/preferences');
        el('personalHours').value = get(pref, 'delayHours'); el('notifications').checked = get(pref, 'notifications');
        el('queue').replaceChildren(); offset = 0; await loadPage();
        await loadNotices();
        el('status').textContent = 'Your private queue. Reading this page has no consent or activity side effects.';
    }
    try {
        const info = await fetch(new URL('System/Info/Public', originRoot), { cache: 'no-store' }).then(r => r.json());
        // Reuse only the existing Jellyfin Web session for THIS server; no credentials are collected or persisted here.
        expectedServerId = info.Id;
        const server = session();
        token = server?.AccessToken;
        if (!token) throw new Error('Sign in to Jellyfin Web on this same origin, then reload this page. API keys are not accepted.');
        await window.DeletionQueueTheme;
        await window.DeletionQueueApplyTheme(server.UserId);
        await refresh();
        poll = setInterval(() => { if (document.visibilityState === 'visible') loadNotices().catch(fail); }, 30000);
    } catch (e) { fail(e); }
    window.addEventListener('pagehide', clearPrivate);
    window.addEventListener('pageshow', e => { if (e.persisted) location.reload(); });
    window.addEventListener('storage', e => {
        if (e.key === 'jellyfin_credentials') { clearPrivate(); fail(new Error('Session changed. Reload after signing in.')); }
        else if (token && window.DeletionQueueApplyTheme) window.DeletionQueueApplyTheme(session()?.UserId).catch(fail);
    });
    el('notificationBell').onclick = async () => {
        try { await loadNotices(); if (!el('notificationDialog').open) el('notificationDialog').showModal(); } catch (e) { fail(e); }
    };
    el('closeNotices').onclick = () => el('notificationDialog').close();
    el('proposeForm').addEventListener('input', () => { previewGeneration++; pending = null; el('preview').hidden = true; });
    el('refresh').onclick = () => refresh().catch(fail);
    el('loadMore').onclick = () => loadPage().catch(fail);
    el('proposeForm').onsubmit = async e => {
        e.preventDefault(); pending = null; el('preview').hidden = true;
        const generation = ++previewGeneration;
        const input = el('identifier').value.trim();
        const selected = { itemId: el('selectionType').value === 'item' ? input : null,
            requestId: el('selectionType').value === 'request' ? Number(input) : null, is4K: el('is4k').checked };
        try {
            const result = await api('targets/preview', 'POST', selected);
            if (generation !== previewGeneration) return;
            selection = selected; pending = result; el('previewTitle').textContent = get(pending, 'title'); el('preview').hidden = false;
        } catch (err) { fail(err); }
    };
    el('confirmProposal').onclick = async () => {
        if (!pending) return;
        const preview = pending; pending = null; el('preview').hidden = true;
        try { await api('proposals', 'POST', { commandId: commandId(), selection, fingerprint: get(preview, 'fingerprint'), snapshot: get(preview, 'snapshot') }); await refresh(); } catch (e) { fail(e); }
    };
    el('preferencesForm').onsubmit = async e => {
        e.preventDefault(); if (!pref) return;
        try { await api('me/preferences', 'PATCH', { commandId: commandId(), revision: get(pref, 'revision'), delayHours: Number(el('personalHours').value), notifications: el('notifications').checked }); await refresh(); } catch (err) { fail(err); }
    };
}());
