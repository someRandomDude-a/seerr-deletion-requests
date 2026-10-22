(function () {
    'use strict';
    const page = document.getElementById('deletionQueueAdmin');
    if (!page || page.dataset.bound) return;
    page.dataset.bound = 'true';
    const el = id => page.querySelector('#' + id);
    let current;
    let busy = false;
    let accountDraft = {};
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
        for (const id of ['dqUsers', 'dqCoverage', 'dqBacklog', 'dqJournal']) el(id).replaceChildren();
        accountDraft = {}; el('dqResetUser').disabled = true; el('dqEnhanced').disabled = true;
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
            el('dqAuto').checked = value(policy, 'automaticDeletion') && value(policy, 'seerrManagedDeletionAcknowledged');
            el('dqRequireAdminApproval').checked = value(policy, 'requireAdministratorApproval');
            el('dqSuspend').checked = value(policy, 'suspended');
            const selected = el('dqUsers').value;
            el('dqUsers').replaceChildren();
            const overrides = value(policy, 'accounts');
            accountDraft = Object.fromEntries(Object.entries(overrides).map(([id, authority]) => [policyId(id), authorityNumber(authority)]));
            for (const account of value(current, 'accounts')) {
                const option = document.createElement('option'); option.value = policyId(value(account, 'id'));
                option.textContent = value(account, 'username'); el('dqUsers').append(option);
            }
            if ([...el('dqUsers').options].some(o => o.value === selected)) el('dqUsers').value = selected;
            el('dqResetUser').disabled = !el('dqUsers').value;
            el('dqUserAuthority').value = accountDraft[el('dqUsers').value] ?? '';
            const available = value(value(current, 'enhanced'), 'connectionAvailable');
            el('dqEnhanced').disabled = !available;
            el('dqEnhancedStatus').textContent = available ? 'Enhanced connection detected. Import it without exposing its API key. Later Enhanced changes require another import.' : 'No unambiguous enabled Enhanced connection found. Configure Seerr manually if Enhanced has multiple URLs or is unavailable.';
            el('dqCoverage').textContent = JSON.stringify({
                coverageSince: value(current, 'coverageSince'), heartbeat: value(current, 'lastHeartbeat'),
                healthy: value(current, 'coverageHealthy'), clockHealthy: value(current, 'clockHealthy'),
                problem: value(current, 'coverageProblem'), localIntegrityVerified: value(current, 'localIntegrityVerified'),
                continuousWindowCollected: value(current, 'continuousWindowCollected'), perProposalInventoryChecksEnabled: value(current, 'perProposalInventoryChecksEnabled'),
                seerrDelegationTrusted: value(current, 'seerrDelegationTrusted'),
                independentRollbackProtection: value(current, 'independentRollbackProtection'),
                historyAuthentication: value(current, 'historyAuthentication')
            }, null, 2);
            el('dqBacklog').replaceChildren();
            for (const proposal of value(current, 'proposals')) {
                const details = document.createElement('details'); const summary = document.createElement('summary');
                summary.textContent = value(value(proposal, 'target'), 'title') + ' — ' + value(proposal, 'status') + ' / ' + value(proposal, 'reason');
                const pre = document.createElement('pre'); pre.className = 'dq-evidence';
                pre.textContent = JSON.stringify(proposal, null, 2); details.append(summary, pre); el('dqBacklog').append(details);
                if (value(policy, 'requireAdministratorApproval') && !value(proposal, 'terminal') && !value(proposal, 'claim')) {
                    const snapshots = value(current, 'administratorApprovalSnapshots');
                    const key = Object.keys(snapshots).find(k => normalizedId(k) === normalizedId(value(proposal, 'id')));
                    for (const [label, approved] of [['Approve removal', true], ['Revoke administrator approval', false]]) {
                        const decision = document.createElement('button'); decision.type = 'button'; decision.className = 'emby-button raised';
                        decision.textContent = label;
                        decision.onclick = async () => {
                            decision.disabled = true;
                            try { await request('admin/proposals/' + value(proposal, 'id') + '/approval', 'POST', {
                                commandId: crypto.randomUUID(), revision: value(proposal, 'revision'), snapshot: snapshots[key], approved }); await refresh(); }
                            catch (err) { error(err); } finally { decision.disabled = false; }
                        };
                        details.append(decision);
                    }
                }
                if (value(proposal, 'status') === 4 || value(proposal, 'status') === 'Uncertain') {
                    const reconcile = document.createElement('button'); reconcile.type = 'button'; reconcile.className = 'emby-button raised';
                    reconcile.textContent = 'Recheck Seerr outcome (never resend deletion)';
                    reconcile.onclick = async () => {
                        reconcile.disabled = true;
                        try { await request('admin/proposals/' + value(proposal, 'id') + '/reconcile', 'POST', { commandId: crypto.randomUUID(), revision: value(proposal, 'revision') }); await refresh(); }
                        catch (err) { error(err); } finally { reconcile.disabled = false; }
                    };
                    details.append(reconcile);
                }
            }
            el('dqJournal').textContent = JSON.stringify(value(current, 'journal'), null, 2);
        } catch (e) { error(e); } finally { busy = false; page.setAttribute('aria-busy', 'false'); }
    }
    el('dqRefresh').addEventListener('click', refresh);
    el('dqUsers').addEventListener('change', () => { el('dqUserAuthority').value = accountDraft[el('dqUsers').value] ?? ''; });
    el('dqUserAuthority').addEventListener('change', () => {
        const id = el('dqUsers').value; if (!id) return;
        if (el('dqUserAuthority').value === '') delete accountDraft[id]; else accountDraft[id] = Number(el('dqUserAuthority').value);
    });
    el('dqEnhanced').addEventListener('click', async () => {
        if (!current || busy || !confirm('Import the current Enhanced Seerr connection? This replaces the queue connection and restarts activity coverage.')) return;
        try { await request('admin/seerr/enhanced', 'POST', { commandId: crypto.randomUUID(), revision: value(value(current, 'seerr'), 'revision') }); await refresh(); }
        catch (err) { error(err); }
    });
    el('dqResetUser').addEventListener('click', async () => {
        if (!current || busy || !el('dqUsers').value || !confirm('Reset this user’s permission and personal preferences? Retained data and existing deadlines will not be reset. Other unsaved edits are not saved.')) return;
        const id = el('dqUsers').value;
        const account = value(current, 'accounts').find(a => normalizedId(value(a, 'id')) === normalizedId(id));
        if (!account) return;
        try { await request('admin/users/' + id + '/reset', 'POST', { commandId: crypto.randomUUID(),
            revision: value(value(current, 'policy'), 'revision'), preferencesRevision: value(account, 'preferencesRevision') }); await refresh(); }
        catch (err) { error(err); }
    });
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
        const accounts = { ...accountDraft };
        const automaticDeletion = el('dqAuto').checked;
        if (automaticDeletion && !(value(old, 'automaticDeletion') && value(old, 'seerrManagedDeletionAcknowledged')) && !await confirmOptIn()) return;
        try {
            await request('admin/policy', 'PATCH', { commandId: crypto.randomUUID(), revision: value(old, 'revision'), policy: {
                defaultAuthority: Number(el('dqAuthority').value), accounts,
                activityDays: Number(el('dqDays').value), delayHours: Number(el('dqHours').value),
                automaticDeletion, seerrManagedDeletionAcknowledged: automaticDeletion,
                requireAdministratorApproval: el('dqRequireAdminApproval').checked, suspended: el('dqSuspend').checked
            } });
            await refresh();
        } catch (err) { error(err); }
    });
    refresh();
}());
