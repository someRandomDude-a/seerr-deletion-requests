(function () {
    'use strict';
    const page = document.getElementById('deletionQueueAdmin');
    if (!page || page.dataset.bound) return;
    page.dataset.bound = 'true';
    const el = id => page.querySelector('#' + id);
    let current;
    let busy = false;
    const value = (o, name) => o[name] ?? o[name[0].toUpperCase() + name.slice(1)];
    const authorityNumber = v => ({ Denied: 0, OwnRequests: 1, AnyAccessibleItem: 2 })[v] ?? v;
    const normalizedId = id => id.replaceAll('-', '').toLowerCase();
    // Jellyfin renders Guid values without hyphens, but System.Text.Json's Guid dictionary keys require D format.
    const policyId = id => normalizedId(id).replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5');
    const confirmOptIn = () => new Promise(resolve => {
        const dialog = el('dqOptInDialog');
        const finish = result => { dialog.close(); el('dqOptInCancel').onclick = null; el('dqOptInYes').onclick = null; dialog.oncancel = null; resolve(result); };
        el('dqOptInCancel').onclick = () => finish(false); el('dqOptInYes').onclick = () => finish(true);
        dialog.oncancel = e => { e.preventDefault(); finish(false); };
        dialog.showModal();
    });
    const clearEvidence = () => {
        current = null;
        for (const id of ['dqAccounts', 'dqCoverage', 'dqBacklog', 'dqJournal']) el(id).replaceChildren();
        el('dqKey').value = ''; el('dqUrl').value = '';
    };
    const error = e => { clearEvidence(); el('dqError').textContent = 'Operation unavailable. Refresh and review your session, revisions and integrity status.'; console.warn('Deletion queue operation failed', e?.status); };
    const request = (path, method = 'GET', body) => ApiClient.ajax({
        url: ApiClient.getUrl('DeletionQueue/' + path), type: method,
        dataType: method === 'GET' ? 'json' : undefined,
        contentType: 'application/json', data: body ? JSON.stringify(body) : undefined
    });
    async function refresh() {
        if (busy) return;
        busy = true;
        page.setAttribute('aria-busy', 'true');
        try {
            current = await request('admin/status');
            el('dqError').textContent = '';
            el('dqMode').textContent = value(current, 'mode');
            if (!value(current, 'storageAvailable')) { clearEvidence(); el('dqError').textContent = 'Storage failed integrity validation; no reset was performed.'; return; }
            const policy = value(current, 'policy');
            const seerr = value(current, 'seerr');
            el('dqUrl').value = value(seerr, 'baseUrl');
            el('dqKey').value = '';
            el('dqAuthority').value = authorityNumber(value(policy, 'defaultAuthority'));
            el('dqDays').value = value(policy, 'activityDays');
            el('dqHours').value = value(policy, 'delayHours');
            el('dqAuto').checked = value(policy, 'automaticDeletion');
            el('dqSuspend').checked = value(policy, 'suspended');
            el('dqAccounts').replaceChildren();
            const overrides = value(policy, 'accounts');
            for (const account of value(current, 'accounts')) {
                const row = document.createElement('div');
                row.className = 'selectContainer';
                // Jellyfin Web 12.2's v0 custom-element polyfill expects a string, not v1's { is } object.
                const select = document.createElement('select', 'emby-select');
                select.setAttribute('label', value(account, 'username'));
                select.dataset.userId = value(account, 'id');
                for (const [v, text] of [['', 'Inherit global'], ['0', 'Denied'], ['1', 'Own requests'], ['2', 'Any accessible item']]) {
                    const option = document.createElement('option'); option.value = v; option.textContent = text; select.append(option);
                }
                const overrideKey = Object.keys(overrides).find(id => normalizedId(id) === normalizedId(select.dataset.userId));
                select.value = authorityNumber(overrides[overrideKey]) ?? '';
                row.append(select); el('dqAccounts').append(row);
            }
            el('dqCoverage').textContent = JSON.stringify({
                coverageSince: value(current, 'coverageSince'), heartbeat: value(current, 'lastHeartbeat'),
                healthy: value(current, 'coverageHealthy'), clockHealthy: value(current, 'clockHealthy'),
                problem: value(current, 'coverageProblem'), localIntegrityVerified: value(current, 'localIntegrityVerified'),
                continuousWindowCollected: value(current, 'continuousWindowCollected'), fullActivityScanVerified: value(current, 'fullActivityScanVerified'),
                pinnedDeletionContractAvailable: value(current, 'pinnedDeletionContractAvailable'),
                independentRollbackProtection: value(current, 'independentRollbackProtection'),
                historyAuthentication: value(current, 'historyAuthentication')
            }, null, 2);
            el('dqBacklog').replaceChildren();
            for (const proposal of value(current, 'proposals')) {
                const details = document.createElement('details'); const summary = document.createElement('summary');
                summary.textContent = value(value(proposal, 'target'), 'title') + ' — ' + value(proposal, 'status') + ' / ' + value(proposal, 'reason');
                const pre = document.createElement('pre'); pre.className = 'dq-evidence';
                pre.textContent = JSON.stringify(proposal, null, 2); details.append(summary, pre); el('dqBacklog').append(details);
            }
            el('dqJournal').textContent = JSON.stringify(value(current, 'journal'), null, 2);
            el('dqUserLink').href = ApiClient.getUrl('DeletionQueue/ui');
        } catch (e) { error(e); } finally { busy = false; page.setAttribute('aria-busy', 'false'); }
    }
    el('dqRefresh').addEventListener('click', refresh);
    page.addEventListener('pageshow', refresh);
    el('dqConnection').addEventListener('submit', async e => {
        e.preventDefault(); if (!current || busy) return;
        try {
            await request('admin/seerr', 'PATCH', { commandId: crypto.randomUUID(), revision: value(value(current, 'seerr'), 'revision'),
                baseUrl: el('dqUrl').value, apiKey: el('dqKey').value || null });
            await refresh();
        } catch (err) { error(err); }
    });
    el('dqPolicy').addEventListener('submit', async e => {
        e.preventDefault(); if (!current || busy) return;
        const old = value(current, 'policy');
        const accounts = Object.fromEntries(Object.entries(value(old, 'accounts')).map(([id, authority]) => [policyId(id), authority]));
        for (const select of el('dqAccounts').querySelectorAll('select')) {
            for (const id of Object.keys(accounts)) if (normalizedId(id) === normalizedId(select.dataset.userId)) delete accounts[id];
            if (select.value !== '') accounts[policyId(select.dataset.userId)] = Number(select.value);
        }
        const automaticDeletion = el('dqAuto').checked;
        if (automaticDeletion && !value(old, 'automaticDeletion') && !await confirmOptIn()) return;
        try {
            await request('admin/policy', 'PATCH', { commandId: crypto.randomUUID(), revision: value(old, 'revision'), policy: {
                defaultAuthority: Number(el('dqAuthority').value), accounts,
                activityDays: Number(el('dqDays').value), delayHours: Number(el('dqHours').value),
                automaticDeletion, suspended: el('dqSuspend').checked
            } });
            await refresh();
        } catch (err) { error(err); }
    });
    refresh();
}());
