// Styles only: never bootstrap Jellyfin Web JS, save native preferences, or change Web files.
window.DeletionQueueTheme = (async function () {
    'use strict';
    const base = new URL('../', new URL('./', location.href));
    const web = new URL('web/', base);
    const response = await fetch(new URL('index.html', web), { cache: 'no-store', credentials: 'omit', signal: AbortSignal.timeout(10000) });
    if (!response.ok || Number(response.headers.get('content-length')) > 1024 * 1024) throw new Error('Jellyfin styles unavailable');
    const html = await response.text();
    if (html.length > 1024 * 1024) throw new Error('Jellyfin styles unavailable');
    const parsed = new DOMParser().parseFromString(html, 'text/html');
    const styles = Array.from(parsed.querySelectorAll('link[rel="stylesheet"]')).slice(0, 16);
    if (!styles.length) throw new Error('Jellyfin styles unavailable');
    const native = await fetch(new URL('DeletionQueue/assets/native-styles', base), { cache: 'no-store', credentials: 'omit', signal: AbortSignal.timeout(10000) });
    if (!native.ok) throw new Error('Jellyfin control styles unavailable');
    const chunks = await native.json();
    if (!Array.isArray(chunks) || chunks.length > 8 || chunks.some(c => typeof c !== 'string' || !/^[a-z0-9._-]+\.css$/i.test(c))) throw new Error('Invalid Jellyfin control styles');
    const urls = [...new Set([...styles.map(s => new URL(s.getAttribute('href'), web).href), ...chunks.map(c => new URL(c, web).href)])];
    await Promise.all(urls.map(href => new Promise((resolve, reject) => {
        const url = new URL(href);
        if (url.origin !== location.origin || !url.pathname.startsWith(web.pathname) || !url.pathname.endsWith('.css')) { reject(new Error('Invalid theme asset')); return; }
        const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = url.href;
        const timeout = setTimeout(() => reject(new Error('Jellyfin stylesheet timeout')), 10000);
        link.onload = () => { clearTimeout(timeout); resolve(); }; link.onerror = () => { clearTimeout(timeout); reject(new Error('Jellyfin stylesheet unavailable')); };
        document.head.append(link);
    })));
    // Only layout rules are plugin-owned. Native CSS + theme + Branding CSS own every color/control.
    const layout = document.createElement('link'); layout.rel = 'stylesheet'; layout.href = new URL('assets/queue.css', new URL('./', location.href)); document.head.append(layout);
    const nativeTheme = document.createElement('link'); nativeTheme.rel = 'stylesheet';
    document.head.insertBefore(nativeTheme, layout);
    let selectedTheme;
    function applyNativeTheme(theme) {
        document.documentElement.dataset.theme = theme;
        if (theme === selectedTheme) return Promise.resolve();
        selectedTheme = theme;
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => reject(new Error('Jellyfin theme unavailable')), 10000);
            nativeTheme.onload = () => { clearTimeout(timeout); resolve(); };
            nativeTheme.onerror = () => { clearTimeout(timeout); reject(new Error('Jellyfin theme unavailable')); };
            nativeTheme.href = new URL('themes/' + theme + '/theme.css', web);
        });
    }
    await applyNativeTheme('dark');
    const custom = document.createElement('style'); custom.id = 'deletionQueueUserCss';
    const branding = document.createElement('link'); branding.rel = 'stylesheet'; branding.href = new URL('Branding/Css', base);
    document.head.append(branding, custom);
    window.DeletionQueueApplyTheme = async userId => {
        const id = String(userId || '');
        if (!/^[a-f0-9-]{32,36}$/i.test(id)) return;
        const theme = localStorage.getItem(id + '-appTheme') || 'dark';
        const disabled = localStorage.getItem(id + '-disableCustomCss') === 'true';
        branding.disabled = disabled;
        const css = localStorage.getItem(id + '-customCss') || '';
        custom.textContent = !disabled && css.length <= 131072 ? css : '';
        await applyNativeTheme(/^[a-z0-9_-]{1,40}$/i.test(theme) ? theme : 'dark');
    };
    document.documentElement.classList.add(matchMedia('(max-width: 767px)').matches ? 'layout-mobile' : 'layout-desktop');
    return true;
}());
// Attach a rejection handler immediately, including when session initialization fails before awaiting styles.
window.DeletionQueueTheme.catch(() => {});
