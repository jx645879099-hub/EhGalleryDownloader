import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// No browser or real credentials are used: exercise the shipped popup with API fixtures.
export async function runExtensionTests() {
  const source = await readFile(new URL('../../src/EhGalleryDownloader/BrowserExtension/popup.js', import.meta.url), 'utf8');
  const manifest = JSON.parse(await readFile(new URL('../../src/EhGalleryDownloader/BrowserExtension/manifest.json', import.meta.url), 'utf8'));
  const passed = [];
  function fixture(overrides = {}) {
    const button = { disabled: false, addEventListener: (_, handler) => { button.click = handler; } };
    const status = {};
    const diagnose = { disabled: false, addEventListener: (_, handler) => { diagnose.click = handler; } };
    const diagnostics = {};
    const chrome = {
      tabs: { query: async () => [{ id: 77, url: 'https://exhentai.org/favorites.php' }] },
      cookies: {
        getAllCookieStores: async () => [{ id: 'regular', tabIds: [1] }, { id: 'active', tabIds: [77] }],
        getAll: async () => [
          { name: 'ipb_member_id', value: 'fixture-user', domain: '.exhentai.org' },
          { name: 'ipb_pass_hash', value: 'fixture-hash', domain: '.exhentai.org' },
          { name: 'igneous', value: 'fixture-token', domain: '.exhentai.org' }
        ]
      }
    };
    const elements = { '#import': button, '#status': status, '#diagnose': diagnose, '#diagnostics': diagnostics };
    const context = vm.createContext({ document: { querySelector: selector => elements[selector] },
      navigator: { userAgent: 'Chrome' }, chrome, URL, AbortController, setTimeout, clearTimeout,
      fetch: async () => { throw new Error('fixture: no receiver'); }, ...overrides });
    vm.runInContext(source, context);
    return { context, chrome, button, status, diagnose, diagnostics, run: code => vm.runInContext(code, context) };
  }
  const stores = fixture();
  const storeReads = [];
  stores.chrome.cookies.getAll = async details => {
    storeReads.push(details.storeId);
    return [{ name: 'ipb_member_id', value: 'fixture-user', domain: '.exhentai.org' },
      { name: 'ipb_pass_hash', value: 'fixture-hash', domain: '.exhentai.org' },
      { name: 'unrelated', value: 'not-for-import', domain: '.exhentai.org' }];
  };
  const collected = await stores.run('collectCookies({id:77,url:"https://exhentai.org/favorites.php"})');
  assert(storeReads.length > 0 && storeReads.every(store => store === undefined || store === 'active'));
  assert.equal(collected.length, 2);
  passed.push('Current-tab cookie store is selected and unrelated cookies are excluded.');

  const forumOnly = fixture();
  const forumQueries = [];
  forumOnly.chrome.cookies.getAll = async details => {
    forumQueries.push(details);
    if (details.domain !== 'e-hentai.org') return [];
    return [{ name: 'ipb_member_id', value: 'forum-user', domain: 'forums.e-hentai.org', path: '/index.php' },
      { name: 'ipb_pass_hash', value: 'forum-hash', domain: 'forums.e-hentai.org', path: '/index.php' }];
  };
  const forumCookies = await forumOnly.run('collectCookies({id:77,url:"https://exhentai.org/"})');
  assert.equal(forumCookies.find(cookie => cookie.name === 'ipb_member_id').value, 'forum-user');
  assert(forumQueries.some(query => query.domain === 'e-hentai.org' && query.storeId === 'active'));
  passed.push('Login cookies on forum hosts and non-root paths can be imported.');

  const partitioned = fixture();
  partitioned.chrome.cookies.getPartitionKey = async details => {
    assert.equal(details.tabId, 77);
    assert.equal(details.frameId, 0);
    return { topLevelSite: 'https://exhentai.org', hasCrossSiteAncestor: false };
  };
  partitioned.chrome.cookies.getAll = async details => details.partitionKey?.topLevelSite === 'https://exhentai.org'
    ? [{ name: 'ipb_member_id', value: 'partition-user', domain: '.exhentai.org', path: '/' },
      { name: 'ipb_pass_hash', value: 'partition-hash', domain: '.exhentai.org', path: '/' },
      { name: 'igneous', value: 'partition-access', domain: '.exhentai.org', path: '/' }] : [];
  const partitionCookies = await partitioned.run('collectCookies({id:77,url:"https://exhentai.org/"})');
  assert.equal(partitionCookies.find(cookie => cookie.name === 'ipb_member_id').value, 'partition-user');
  passed.push('The current page partition is read when ordinary cookie queries are empty.');

  const splitScope = fixture();
  splitScope.chrome.cookies.getAll = async () => [
    { name: 'ipb_member_id', value: 'private-user', domain: '.e-hentai.org', path: '/' },
    { name: 'ipb_pass_hash', value: 'private-hash', domain: 'forums.e-hentai.org', path: '/index.php' },
    { name: 'igneous', value: 'private-access', domain: '.exhentai.org', path: '/' }
  ];
  await assert.rejects(splitScope.run('collectCookies({id:77,url:"https://exhentai.org/"})'), error => {
    assert.match(error.message, /配对|同一/);
    assert(!/private-user|private-hash|private-access/.test(error.message));
    assert.match(error.message, /账号编号/);
    return true;
  });
  passed.push('Different host or path scopes are never combined into a false account pair.');

  const permissionDenied = fixture();
  permissionDenied.chrome.permissions = { contains: async () => false };
  permissionDenied.chrome.cookies.getAll = async () => { throw new Error('Cookie reads must not run without permission'); };
  await assert.rejects(permissionDenied.run('collectCookies({id:77,url:"https://exhentai.org/"})'), /站点访问权限/);
  passed.push('Missing site permissions are distinguished from missing account cookies.');

  const diagnosticsOnly = fixture({ fetch: async () => { throw new Error('Diagnostics must not send any data'); } });
  await diagnosticsOnly.diagnose.click();
  assert.match(diagnosticsOnly.diagnostics.textContent, /账号编号已读到/);
  assert.match(diagnosticsOnly.diagnostics.textContent, /里站凭证已读到/);
  assert(!/fixture-user|fixture-hash|fixture-token/.test(diagnosticsOnly.diagnostics.textContent));
  assert.equal(diagnosticsOnly.diagnose.disabled, false);
  passed.push('Read diagnostics show field presence only and never contact the receiver.');

  const defaultPartition = fixture();
  defaultPartition.chrome.cookies.getPartitionKey = async () => { throw new Error('Partition API unavailable'); };
  assert.equal((await defaultPartition.run('collectCookies({id:77,url:"https://exhentai.org/"})')).length, 3);
  passed.push('A partition API failure does not hide valid ordinary login cookies.');

  const scopedAccounts = fixture();
  scopedAccounts.chrome.cookies.getAll = async details => details.domain === 'e-hentai.org' ? [
    { name: 'ipb_member_id', value: 'forum-user', domain: 'forums.e-hentai.org', path: '/index.php' },
    { name: 'ipb_pass_hash', value: 'forum-hash', domain: 'forums.e-hentai.org', path: '/index.php' },
    { name: 'ipb_member_id', value: 'root-user', domain: '.e-hentai.org', path: '/' },
    { name: 'ipb_pass_hash', value: 'root-hash', domain: '.e-hentai.org', path: '/' }
  ] : [];
  const rootAccount = await scopedAccounts.run('collectCookies({id:77,url:"https://e-hentai.org/"})');
  assert.equal(rootAccount.find(cookie => cookie.name === 'ipb_member_id').value, 'root-user');
  assert.equal(rootAccount.find(cookie => cookie.name === 'ipb_pass_hash').value, 'root-hash');
  passed.push('A complete account matching the open page takes priority over a forum fallback.');

  const pageRead = fixture();
  const pageReads = [];
  pageRead.chrome.cookies.getAll = async details => {
    pageReads.push(details);
    if (details.url !== 'https://exhentai.org/favorites.php'
        || details.storeId !== undefined || details.name !== undefined) return [];
    return [{ name: 'ipb_member_id', value: 'visible-user', domain: '.exhentai.org', path: '/', storeId: '0' },
      { name: 'ipb_pass_hash', value: 'visible-hash', domain: '.exhentai.org', path: '/', storeId: '0' },
      { name: 'igneous', value: 'visible-access', domain: '.exhentai.org', path: '/', storeId: '0' },
      { name: 'unrelated', value: 'not-for-import', domain: '.exhentai.org', path: '/', storeId: '0' }];
  };
  const pageCookies = await pageRead.run('collectCookies({id:77,url:"https://exhentai.org/favorites.php",incognito:false})');
  assert.equal(pageCookies.length, 3);
  assert.equal(pageCookies.find(cookie => cookie.name === 'ipb_member_id').value, 'visible-user');
  assert(pageReads.some(details => details.url && !details.domain && !details.name && details.storeId === undefined));
  assert.match(pageRead.diagnostics.textContent, /当前网页直接读取.*4/);
  passed.push('The current-page query used by Cookie-Editor succeeds even when filtered domain queries return nothing.');

  const incognitoRead = fixture();
  let unsafeOrdinaryStoreRead = false;
  incognitoRead.chrome.extension = { inIncognitoContext: false };
  incognitoRead.chrome.cookies.getAll = async details => {
    if (details.storeId !== 'active') unsafeOrdinaryStoreRead = true;
    assert.equal(details.storeId, 'active', 'An incognito page must not read the ordinary extension cookie store');
    return [{ name: 'ipb_member_id', value: 'incognito-user', domain: '.exhentai.org', path: '/' },
      { name: 'ipb_pass_hash', value: 'incognito-hash', domain: '.exhentai.org', path: '/' },
      { name: 'igneous', value: 'incognito-access', domain: '.exhentai.org', path: '/' }];
  };
  assert.equal((await incognitoRead.run('collectCookies({id:77,url:"https://exhentai.org/",incognito:true})')).length, 3);
  assert.equal(unsafeOrdinaryStoreRead, false);
  passed.push('An ordinary extension context keeps an incognito page in its explicit cookie store.');

  const emptyValues = fixture();
  emptyValues.chrome.cookies.getAll = async () => [
    { name: 'ipb_member_id', value: '', domain: '.exhentai.org', path: '/' },
    { name: 'ipb_pass_hash', value: '', domain: '.exhentai.org', path: '/' },
    { name: 'igneous', value: '', domain: '.exhentai.org', path: '/' }
  ];
  await assert.rejects(emptyValues.run('collectCookies({id:77,url:"https://exhentai.org/"})'), /值为空/);
  passed.push('Present cookie names with empty values are reported separately from absent fields.');

  // Chromium's cookie permission check uses the cookie's Secure attribute to
  // choose http/https, independently of the page URL used in cookies.getAll().
  // https://github.com/chromium/chromium/blob/main/extensions/browser/api/cookies/cookies_helpers.cc
  function manifestAllowsCookie(cookie) {
    const protocol = cookie.secure ? 'https' : 'http';
    const host = cookie.domain.replace(/^\./, '');
    return manifest.host_permissions.some(pattern => {
      const match = /^(https?|\*):\/\/([^/]+)\//.exec(pattern);
      if (!match || (match[1] !== '*' && match[1] !== protocol)) return false;
      const patternHost = match[2];
      return patternHost === host || (patternHost.startsWith('*.')
        && (host === patternHost.slice(2) || host.endsWith(patternHost.slice(1))));
    });
  }
  const insecureCookies = [
    { name: 'ipb_member_id', value: 'legacy-user', domain: '.exhentai.org', path: '/', secure: false },
    { name: 'ipb_pass_hash', value: 'legacy-hash', domain: '.exhentai.org', path: '/', secure: false },
    { name: 'igneous', value: 'legacy-access', domain: '.exhentai.org', path: '/', secure: false }
  ];
  const legacyPermissions = fixture();
  legacyPermissions.chrome.cookies.getAll = async details => insecureCookies.filter(cookie =>
    manifestAllowsCookie(cookie) && (!details.domain || cookieSiteForTest(cookie.domain) === details.domain));
  function cookieSiteForTest(domain) { return domain.replace(/^\./, ''); }
  assert.equal((await legacyPermissions.run('collectCookies({id:77,url:"https://exhentai.org/",incognito:false})')).length, 3);
  for (const domain of ['.e-hentai.org', 'forums.e-hentai.org', '.exhentai.org']) {
    assert(manifestAllowsCookie({ domain, secure: false }), `HTTP Cookie permission missing for ${domain}`);
    assert(manifestAllowsCookie({ domain, secure: true }), `HTTPS Cookie permission missing for ${domain}`);
  }
  assert.equal(manifestAllowsCookie({ domain: '.example.com', secure: false }), false);
  assert.equal(manifestAllowsCookie({ domain: 'exhentai.org.example.com', secure: true }), false);
  passed.push('The shipped manifest can read non-Secure login cookies on HTTPS pages and stays within gallery hosts.');

  const partialPermissions = fixture();
  const checkedOrigins = [];
  partialPermissions.chrome.permissions = { contains: async details => {
    checkedOrigins.push(...details.origins);
    return details.origins.every(origin => origin.startsWith('https://'));
  } };
  await assert.rejects(partialPermissions.run('collectCookies({id:77,url:"https://exhentai.org/"})'), /HTTP/);
  assert(checkedOrigins.some(origin => origin.startsWith('*://') || origin.startsWith('http://')));
  passed.push('HTTPS-only grants are identified as incomplete cookie-reading permissions.');

  const invalidTab = fixture();
  invalidTab.chrome.tabs.query = async () => [{ id: 77, url: 'https://example.com/' }];
  await invalidTab.button.click();
  assert.match(invalidTab.status.textContent, /ExHentai/);
  assert.equal(invalidTab.button.disabled, false);
  passed.push('Unsupported pages show a useful message and allow retry.');

  const httpPage = fixture();
  httpPage.chrome.tabs.query = async () => [{ id: 77, url: 'http://exhentai.org/' }];
  await httpPage.button.click();
  assert.match(httpPage.status.textContent, /HTTPS/);
  assert.equal(httpPage.button.disabled, false);
  passed.push('The extra HTTP cookie permission does not permit importing from an HTTP page.');

  const validSession = { app: 'EhGalleryDownloader', token: 'a'.repeat(32), expiresInSeconds: 250 };
  const discovery = fixture({ fetch: async (url, options) => {
    const port = new URL(url).port;
    if (port === '17893') return new Promise((resolve, reject) => options.signal.addEventListener('abort',
      () => reject(Object.assign(new Error('aborted'), { name: 'AbortError' }))));
    if (port === '17894') return { ok: true, json: async () => validSession };
    return { ok: true, json: async () => ({ ...validSession, app: 'UnrelatedApp' }) };
  } });
  let discoveryDeadline;
  try {
    const session = await Promise.race([discovery.run('findSession()'), new Promise((resolve, reject) => {
      discoveryDeadline = setTimeout(() => reject(new Error('A stalled port blocked the real receiver')), 700);
    })]);
    assert.equal(session.port, 17894);
  } finally { clearTimeout(discoveryDeadline); }
  passed.push('A stalled or unrelated local port cannot hide the real receiver.');

  const disconnected = fixture();
  await disconnected.button.click();
  assert.match(disconnected.status.textContent, /开始接收 Cookie/);
  assert.equal(disconnected.button.disabled, false);
  passed.push('A missing receiver tells the user where to start receiving.');

  const rejected = fixture({ fetch: async url => new URL(url).pathname === '/session'
    ? { ok: true, json: async () => validSession }
    : { ok: false, status: 422, json: async () => ({ error: '缺少有效 igneous，请刷新 ExHentai 再发送。' }) } });
  await rejected.button.click();
  assert.match(rejected.status.textContent, /igneous/);
  assert.equal(rejected.button.disabled, false);
  passed.push('The receiver diagnosis reaches the popup and failed sends can be retried.');

  let transmitted;
  const success = fixture({ fetch: async (url, options) => {
    assert.equal(new URL(url).hostname, '127.0.0.1');
    assert.equal(options.credentials, 'omit');
    if (new URL(url).pathname === '/session') return { ok: true, json: async () => validSession };
    transmitted = JSON.parse(options.body);
    return { ok: true, json: async () => ({ ok: true }) };
  } });
  await success.button.click();
  assert.equal(transmitted.pageUrl, 'https://exhentai.org/favorites.php');
  assert.equal(transmitted.cookies.length, 3);
  assert.match(success.status.textContent, /验证结果/);
  assert.equal(success.button.disabled, true);
  passed.push('A successful local send directs the user back to website verification.');

  const timeout = fixture({ fetch: async (url, options) => new Promise((resolve, reject) =>
    options.signal.addEventListener('abort', () => reject(Object.assign(new Error('aborted'), { name: 'AbortError' })))) });
  await assert.rejects(timeout.run('readJson("http://127.0.0.1:17893/session",{},5)'), { name: 'AbortError' });
  passed.push('Local connection attempts have a real timeout.');
  return passed;
}
