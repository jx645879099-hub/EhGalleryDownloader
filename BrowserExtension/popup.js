const button = document.querySelector('#import');
const status = document.querySelector('#status');
const ports = Array.from({ length: 10 }, (_, index) => 17893 + index);
const requiredNames = new Set(['ipb_member_id', 'ipb_pass_hash', 'igneous']);

function show(message, kind = '') {
  status.textContent = message;
  status.className = kind;
}

async function findSession() {
  for (const port of ports) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/session`, {
        headers: { 'X-EhGallery-Extension': '1' },
        cache: 'no-store'
      });
      if (response.ok) return { port, ...(await response.json()) };
    } catch { }
  }
  throw new Error('软件尚未等待导入。请先在软件中点击“从当前网页导入”。');
}

async function getCurrentGalleryTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.url) throw new Error('无法识别当前网页。');
  const url = new URL(tab.url);
  const host = url.hostname.toLowerCase();
  if (host !== 'e-hentai.org' && !host.endsWith('.e-hentai.org')
      && host !== 'exhentai.org' && !host.endsWith('.exhentai.org')) {
    throw new Error('请先切换到已登录的 E-Hentai 或 ExHentai 网页。');
  }
  return tab;
}

async function collectCookies(tabUrl) {
  const collected = [];
  for (const url of [tabUrl, 'https://e-hentai.org/', 'https://exhentai.org/']) {
    try {
      const cookies = await chrome.cookies.getAll({ url });
      for (const cookie of cookies) {
        if (requiredNames.has(cookie.name)
            && !collected.some(item => item.name === cookie.name && item.domain === cookie.domain)) {
          collected.push({ name: cookie.name, value: cookie.value, domain: cookie.domain });
        }
      }
    } catch { }
  }
  return collected;
}

button.addEventListener('click', async () => {
  button.disabled = true;
  show('正在检查当前网页和本机软件…');
  try {
    const tab = await getCurrentGalleryTab();
    const session = await findSession();
    const cookies = await collectCookies(tab.url);
    const browser = navigator.userAgent.includes('Edg/') ? 'Microsoft Edge' : 'Google Chrome';
    const response = await fetch(`http://127.0.0.1:${session.port}/import`, {
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
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.error || '软件拒绝了这次导入。');
    show('已安全交给画廊下载助手，正在验证登录。', 'success');
  } catch (error) {
    show(error?.message || '导入失败，请重试。', 'error');
    button.disabled = false;
  }
});
