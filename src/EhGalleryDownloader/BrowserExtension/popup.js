const button = document.querySelector('#import');
const status = document.querySelector('#status');
const diagnoseButton = document.querySelector('#diagnose');
const diagnostics = document.querySelector('#diagnostics');
const ports = Array.from({ length: 10 }, (_, index) => 17893 + index);
const requiredNames = new Set(['ipb_member_id', 'ipb_pass_hash', 'igneous']);
const gallerySites = ['e-hentai.org', 'exhentai.org'];

function show(message, kind = '') {
  status.textContent = message;
  status.className = kind;
}

async function readJson(url, options = {}, timeoutMs = 1500) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(url, { ...options, signal: controller.signal,
      credentials: 'omit', cache: 'no-store' });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || `连接失败（${response.status}）`);
    return result;
  } finally {
    clearTimeout(timeout);
  }
}

async function findSession() {
  try {
    return await Promise.any(ports.map(async port => {
      const session = await readJson(`http://127.0.0.1:${port}/session`, {
        headers: { 'X-EhGallery-Extension': '1' }
      });
      if (session.app !== 'EhGalleryDownloader'
          || !/^[a-f0-9]{32}$/i.test(session.token || '')
          || !(session.expiresInSeconds > 0)) throw new Error('接收窗口已过期或不是下载助手');
      return { ...session, port };
    }));
  } catch {
    throw new Error('没有连上下载助手。请先在软件中点击“开始接收 Cookie”，在说明窗口点击“开始等待浏览器”，再回到这里重试。');
  }
}

async function getCurrentGalleryTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.url) throw new Error('无法识别当前网页，请在画廊网页重新打开本扩展。');
  const url = new URL(tab.url);
  const host = url.hostname.toLowerCase();
  if (url.protocol !== 'https:'
      || (host !== 'e-hentai.org' && !host.endsWith('.e-hentai.org')
          && host !== 'exhentai.org' && !host.endsWith('.exhentai.org'))) {
    throw new Error('请切换到已登录的 HTTPS E-Hentai / ExHentai 网页，再从工具栏打开本扩展。');
  }
  return tab;
}

function cookieSite(domain) {
  const host = (domain || '').replace(/^\./, '').toLowerCase();
  return gallerySites.find(site => host === site || host.endsWith(`.${site}`));
}

function scopeKey(cookie) {
  const partition = cookie.partitionKey;
  return JSON.stringify([cookie.domain.toLowerCase(), cookie.path || '/',
    partition?.topLevelSite || '', partition?.hasCrossSiteAncestor ?? false]);
}

async function readCookieSnapshot(tab) {
  const stores = await chrome.cookies.getAllCookieStores();
  const store = stores.find(item => item.tabIds.includes(tab.id));
  if (!store) throw new Error('找不到当前网页的登录资料。请在同一个 Chrome 用户的普通窗口登录画廊后重试。');

  const snapshot = { cookies: [], emptyFields: [], deniedSites: [], failedSites: [],
    partitionUnavailable: false, directReadCount: 0, directReadFailed: false };
  function includeCookies(items, site) {
    for (const item of items) {
      if (!requiredNames.has(item.name) || cookieSite(item.domain) !== site) continue;
      if (typeof item.value !== 'string' || !item.value.trim()) {
        if (!snapshot.emptyFields.some(field => field.site === site && field.name === item.name))
          snapshot.emptyFields.push({ site, name: item.name });
        continue;
      }
      if (!snapshot.cookies.some(existing => existing.name === item.name
          && scopeKey(existing) === scopeKey(item))) snapshot.cookies.push(item);
    }
  }
  for (const site of gallerySites) {
    // Chromium checks each cookie against http/https according to its Secure
    // attribute. HTTPS page access alone cannot expose non-Secure cookies.
    if (chrome.permissions?.contains
        && !await chrome.permissions.contains({ origins: [`*://*.${site}/*`] }))
      snapshot.deniedSites.push(site);
  }

  const pageSite = cookieSite(new URL(tab.url).hostname);
  if (!snapshot.deniedSites.includes(pageSite)) {
    try {
      // Match Cookie-Editor's working current-page query: let Chrome select its
      // own context store when the page and extension have the same privacy mode.
      const details = { url: tab.url };
      if (Boolean(tab.incognito) !== Boolean(chrome.extension?.inIncognitoContext))
        details.storeId = store.id;
      const cookies = await chrome.cookies.getAll(details);
      snapshot.directReadCount = cookies.filter(cookie => cookieSite(cookie.domain) === pageSite).length;
      includeCookies(cookies, pageSite);
    } catch {
      snapshot.directReadFailed = true;
    }
  }
  const partitions = [undefined];
  if (chrome.cookies.getPartitionKey) {
    try {
      const partitionKey = await chrome.cookies.getPartitionKey({ tabId: tab.id, frameId: 0 });
      if (partitionKey?.topLevelSite) partitions.push(partitionKey);
    } catch {
      snapshot.partitionUnavailable = true;
    }
  }

  for (const site of gallerySites) {
    if (snapshot.deniedSites.includes(site)) continue;
    for (const partitionKey of partitions) {
      for (const name of requiredNames) {
        try {
          // A domain filter includes forum subdomains and non-root cookie paths.
          const details = { domain: site, name, storeId: store.id };
          if (partitionKey) details.partitionKey = partitionKey;
          const cookies = await chrome.cookies.getAll(details);
          includeCookies(cookies.map(cookie => ({ ...cookie,
            partitionKey: cookie.partitionKey || partitionKey })), site);
        } catch {
          if (!snapshot.failedSites.includes(site)) snapshot.failedSites.push(site);
        }
      }
    }
  }
  return snapshot;
}

function describeSnapshot(snapshot) {
  const labels = { ipb_member_id: '账号编号', ipb_pass_hash: '登录凭证', igneous: '里站凭证' };
  const lines = gallerySites.map(site => {
    if (snapshot.deniedSites.includes(site)) return `${site}：扩展缺少完整 Cookie 读取权限（HTTP / HTTPS）`;
    const cookies = snapshot.cookies.filter(cookie => cookieSite(cookie.domain) === site);
    const names = site === 'exhentai.org' ? [...requiredNames] : ['ipb_member_id', 'ipb_pass_hash'];
    const fields = names.map(name => {
      const state = cookies.some(cookie => cookie.name === name) ? '已读到'
        : snapshot.emptyFields.some(field => field.site === site && field.name === name) ? '已找到但值为空' : '未读到';
      return `${labels[name]}${state}`;
    });
    return `${site}：${fields.join('、')}${snapshot.failedSites.includes(site) ? '（部分读取失败）' : ''}`;
  });
  lines.push(snapshot.directReadFailed ? '当前网页直接读取失败。'
    : `当前网页直接读取：${snapshot.directReadCount} 项 Cookie（不显示值）。`);
  if (snapshot.partitionUnavailable) lines.push('浏览器未能读取当前页面的 Cookie 分区。');
  return lines.join('\n');
}

function scopeRank(cookie, tab, site) {
  const current = new URL(tab.url);
  const target = cookieSite(current.hostname) === site ? current : new URL(`https://${site}/`);
  const host = cookie.domain.replace(/^\./, '').toLowerCase();
  const hostMatches = target.hostname === host
    || (!cookie.hostOnly && target.hostname.endsWith(`.${host}`));
  const path = cookie.path || '/';
  const pathMatches = target.pathname === path
    || (target.pathname.startsWith(path) && (path.endsWith('/') || target.pathname[path.length] === '/'));
  return (hostMatches ? 10000 : 0) + (pathMatches ? 1000 : 0)
    + Math.min(path.length, 500) + (cookie.partitionKey ? 1 : 0);
}

function selectAccount(cookies, tab, site) {
  const groups = new Map();
  for (const cookie of cookies.filter(item => cookieSite(item.domain) === site)) {
    const key = scopeKey(cookie);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(cookie);
  }
  return [...groups.values()].filter(group => group.some(cookie => cookie.name === 'ipb_member_id')
    && group.some(cookie => cookie.name === 'ipb_pass_hash'))
    .sort((a, b) => scopeRank(b[0], tab, site) - scopeRank(a[0], tab, site))[0];
}

async function collectCookies(tab) {
  const snapshot = await readCookieSnapshot(tab);
  const summary = describeSnapshot(snapshot);
  diagnostics.textContent = summary;
  const selected = gallerySites.flatMap(site =>
    (selectAccount(snapshot.cookies, tab, site) || []).filter(cookie => cookie.name !== 'igneous'));
  if (selected.length === 0) {
    const permissionProblem = snapshot.deniedSites.length > 0;
    throw new Error((permissionProblem
      ? '扩展缺少画廊网站的完整站点访问权限（HTTP / HTTPS）。请在扩展管理页刷新“画廊登录导入”，确认新增的同域站点权限，再重新打开扩展。'
      : '扩展没有读到配对的账号 Cookie，这不能证明你没有登录。'
        + '请在同一窗口打开 E-Hentai 的 My Home 或论坛确认网站账号登录后再发送；下方显示具体缺少哪些字段。')
      + '\n读取结果（不含 Cookie 值）：\n' + summary);
  }
  const exAccount = selectAccount(snapshot.cookies, tab, 'exhentai.org');
  const accessCookies = snapshot.cookies.filter(cookie => cookieSite(cookie.domain) === 'exhentai.org'
    && cookie.name === 'igneous' && !['mystery', 'null'].includes(cookie.value.toLowerCase()));
  const accessRank = cookie => scopeRank(cookie, tab, 'exhentai.org')
    + (exAccount && scopeKey(cookie) === scopeKey(exAccount[0]) ? 100000 : 0);
  accessCookies.sort((a, b) => accessRank(b) - accessRank(a));
  if (accessCookies[0]) selected.push(accessCookies[0]);
  return selected.map(({ name, value, domain }) => ({ name, value, domain }));
}

if (diagnoseButton?.addEventListener) diagnoseButton.addEventListener('click', async () => {
  diagnoseButton.disabled = true;
  try {
    const tab = await getCurrentGalleryTab();
    diagnostics.textContent = describeSnapshot(await readCookieSnapshot(tab));
    show('读取检查完成。下方只显示字段是否读到，没有显示或发送 Cookie 值。');
  } catch {
    show('读取检查未完成。请切换到画廊网页，并确认扩展具有画廊网站的站点访问权限。', 'error');
  } finally {
    diagnoseButton.disabled = false;
  }
});

button.addEventListener('click', async () => {
  button.disabled = true;
  show('第 1 步：检查当前画廊网页…');
  try {
    const tab = await getCurrentGalleryTab();
    show('第 2 步：连接本机下载助手…');
    const session = await findSession();
    show('已连接软件。第 3 步：读取当前 Chrome 用户的画廊登录信息…');
    const cookies = await collectCookies(tab);
    const browser = navigator.userAgent.includes('Edg/') ? 'Microsoft Edge' : 'Google Chrome';
    await readJson(`http://127.0.0.1:${session.port}/import`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-EhGallery-Extension': '1'
      },
      body: JSON.stringify({
        token: session.token,
        browser,
        pageUrl: tab.url,
        cookies
      })
    }, 8000);
    show('已发送到下载助手。请回到软件查看 E-Hentai / ExHentai 验证结果；发送成功后仍需验证网站访问权限。', 'success');
    button.textContent = '已发送，请返回下载助手';
  } catch (error) {
    show(error?.name === 'AbortError' ? '连接超时，请回到软件重新开始接收后再试。'
      : error?.message || '导入失败，请重试。', 'error');
    button.disabled = false;
  }
});
