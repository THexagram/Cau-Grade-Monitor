const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const readline = require('readline');
const { pathToFileURL } = require('url');
const { chromium } = require('playwright-core');

const ROOT = __dirname;
const DEFAULT_CONFIG = 'config.json';
const GUI_EVENT_PREFIX = '@@CAU_EVENT@@';
const DEFAULT_LOW_RESOURCE_VIEWPORT = { width: 1100, height: 720 };
const DEFAULT_BROWSER_ARGS = [
  '--disable-background-networking',
  '--disable-component-update',
  '--disable-default-apps',
  '--disable-domain-reliability',
  '--disable-client-side-phishing-detection',
  '--disable-sync',
  '--metrics-recording-only',
  '--no-first-run',
  '--no-default-browser-check',
  '--safebrowsing-disable-auto-update',
  '--disable-gpu',
  '--disable-notifications',
  '--mute-audio',
  '--aggressive-cache-discard',
  '--disk-cache-size=10485760',
  '--media-cache-size=1048576'
];

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function nowText() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function log(message) {
  console.log(`[${nowText()}] ${message}`);
}

function emitGuiEvent(type, data = {}) {
  if (process.env.CAU_GUI_EVENTS !== '1') return;
  console.log(`${GUI_EVENT_PREFIX}${JSON.stringify({ type, timestamp: new Date().toISOString(), ...data })}`);
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function writeJson(file, data) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(data, null, 2), 'utf8');
}

function loadEnvFile(file) {
  if (!fs.existsSync(file)) return;
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const index = trimmed.indexOf('=');
    if (index === -1) continue;
    const key = trimmed.slice(0, index).trim();
    let value = trimmed.slice(index + 1).trim();
    if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1);
    }
    if (!process.env[key]) process.env[key] = value;
  }
}

function parseArgs(argv) {
  const args = {
    config: path.join(ROOT, DEFAULT_CONFIG),
    once: false,
    testFeishu: false,
    noPrompt: false
  };
  for (let i = 2; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--once') args.once = true;
    else if (arg === '--test-feishu') args.testFeishu = true;
    else if (arg === '--no-prompt') args.noPrompt = true;
    else if (arg === '--config') {
      i += 1;
      args.config = path.resolve(argv[i]);
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }
  return args;
}

function resolvePathMaybe(base, value) {
  if (!value) return value;
  return path.isAbsolute(value) ? value : path.join(base, value);
}

function positiveNumber(value, fallback) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? number : fallback;
}

function addUnique(target, items) {
  for (const item of items) {
    if (!target.includes(item)) target.push(item);
  }
  return target;
}

function loadConfig(configPath) {
  loadEnvFile(path.join(path.dirname(configPath), '.env'));
  if (!fs.existsSync(configPath)) {
    const example = path.join(path.dirname(configPath), 'config.example.json');
    if (!fs.existsSync(example)) {
      throw new Error(`Missing ${configPath}`);
    }
    fs.copyFileSync(example, configPath);
    log(`Created ${configPath} from config.example.json. Please edit it if needed.`);
  }

  const config = readJson(configPath);
  const base = path.dirname(configPath);
  if (typeof config.manualLogin !== 'boolean') config.manualLogin = true;
  if (!config.loginUrl) config.loginUrl = 'https://one.cau.edu.cn/tp_up/view?m=up#act=portal/viewhome';
  config.intervalSeconds = positiveNumber(config.intervalSeconds, 60);
  config.initialLoginRetrySeconds = positiveNumber(config.initialLoginRetrySeconds, 10);
  config.errorNotifyCooldownSeconds = positiveNumber(config.errorNotifyCooldownSeconds, 1800);
  config.emptyResultNotifyAfter = Math.max(1, Math.floor(positiveNumber(config.emptyResultNotifyAfter, 3)));
  config.emptyResultRetrySeconds = positiveNumber(config.emptyResultRetrySeconds, config.intervalSeconds);
  config.requestTimeoutMs = Math.max(positiveNumber(config.requestTimeoutMs, 30000), 30000);
  config.logging = config.logging || {};
  config.logging.noChangeEverySeconds = positiveNumber(config.logging.noChangeEverySeconds, 1800);
  config.logging.loginNeededEverySeconds = positiveNumber(config.logging.loginNeededEverySeconds, 300);
  config.stateFile = resolvePathMaybe(base, config.stateFile || 'state.json');
  config.browser = config.browser || {};
  config.browser.lowResourceMode = config.browser.lowResourceMode !== false;
  config.browser.closeExtraPages = config.browser.closeExtraPages !== false;
  config.browser.maxScanPages = positiveNumber(config.browser.maxScanPages, 6);
  config.browser.blockResourcesAfterFirstSuccess = config.browser.blockResourcesAfterFirstSuccess !== false;
  config.browser.blockResourceTypes = Array.isArray(config.browser.blockResourceTypes)
    ? config.browser.blockResourceTypes
    : ['image', 'media', 'font'];
  config.browser.userDataDir = resolvePathMaybe(base, config.browser.userDataDir || 'browser-profile');
  config.proxy = config.proxy || {};
  config.proxy.server = process.env.MONITOR_PROXY_SERVER || config.proxy.server || '';
  config.proxy.enabled = Boolean(config.proxy.server) && config.proxy.enabled !== false;
  config.proxy.browser = config.proxy.browser !== false;
  config.proxy.bypass = config.proxy.bypass || 'localhost,127.0.0.1';
  config.feishu = config.feishu || {};
  config.feishu.webhook = process.env[config.feishu.webhookEnv || 'FEISHU_WEBHOOK_URL'] || config.feishu.webhook || '';
  config.feishu.secret = process.env[config.feishu.secretEnv || 'FEISHU_BOT_SECRET'] || config.feishu.secret || '';
  config.browser.args = Array.isArray(config.browser.args) ? config.browser.args : [];
  if (config.browser.lowResourceMode) addUnique(config.browser.args, DEFAULT_BROWSER_ARGS);
  config.browser.proxyPacFile = resolvePathMaybe(base, config.browser.proxyPacFile || '');
  config.browser.loadExtensionDir = resolvePathMaybe(base, config.browser.loadExtensionDir || '');
  return config;
}

function proxyOptions(config) {
  if (!config.proxy || !config.proxy.enabled) return null;
  const proxy = { server: config.proxy.server };
  if (config.proxy.bypass) proxy.bypass = config.proxy.bypass;
  if (config.proxy.username) proxy.username = config.proxy.username;
  if (config.proxy.password) proxy.password = config.proxy.password;
  return proxy;
}

function displayProxyServer(server) {
  return String(server || '').replace(/:\/\/([^:@/]+):([^@/]+)@/, '://$1:***@');
}

function feishuSign(secret, timestamp) {
  const stringToSign = `${timestamp}\n${secret}`;
  return crypto.createHmac('sha256', stringToSign).update('').digest('base64');
}

async function sendFeishu(config, text) {
  const webhook = config.feishu.webhook;
  if (!webhook) {
    log('Feishu webhook is empty; skip notification.');
    return;
  }

  const payload = {
    msg_type: 'text',
    content: {
      text: config.feishu.atAll ? `${text}\n<at user_id="all">所有人</at>` : text
    }
  };

  if (config.feishu.secret) {
    const timestamp = Math.floor(Date.now() / 1000);
    payload.timestamp = String(timestamp);
    payload.sign = feishuSign(config.feishu.secret, timestamp);
  }

  const response = await fetch(webhook, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=utf-8' },
    body: JSON.stringify(payload)
  });
  const responseText = await response.text();
  if (!response.ok) {
    throw new Error(`Feishu HTTP ${response.status}: ${responseText}`);
  }
  let parsed = {};
  try {
    parsed = JSON.parse(responseText);
  } catch (error) {
    parsed = {};
  }
  if (parsed.code && parsed.code !== 0) {
    throw new Error(`Feishu error ${parsed.code}: ${parsed.msg || responseText}`);
  }
}

function normalize(text) {
  return String(text || '').replace(/\s+/g, ' ').trim();
}

function rowKey(row) {
  return [row.term, row.code, row.name, row.credit].join('||');
}

function rowSummary(row) {
  return `${row.term} ${row.name}: ${row.score}`;
}

function signature(rows) {
  return rows.map((row) => `${rowKey(row)}=>${row.score}`).sort().join('\n');
}

function diffRows(previousRows, currentRows) {
  const previous = new Map(previousRows.map((row) => [rowKey(row), row]));
  const changes = [];
  for (const row of currentRows) {
    const old = previous.get(rowKey(row));
    if (!old) {
      changes.push({ type: 'new', row });
    } else if (old.score !== row.score) {
      changes.push({ type: 'changed', row, oldScore: old.score });
    }
  }
  return changes;
}

const GRADE_POINTS = new Map([
  ['A+', 4.0],
  ['A', 4.0],
  ['A-', 3.7],
  ['B+', 3.3],
  ['B', 3.0],
  ['B-', 2.7],
  ['C+', 2.3],
  ['C', 2.0],
  ['D+', 1.5],
  ['D', 1.0],
  ['F', 0]
]);

function toHalfWidth(text) {
  return String(text || '')
    .replace(/[！-～]/g, (char) => String.fromCharCode(char.charCodeAt(0) - 0xfee0))
    .replace(/　/g, ' ');
}

function normalizeScore(score) {
  return toHalfWidth(score).replace(/\s+/g, '').toUpperCase();
}

function parseCredit(credit) {
  const match = toHalfWidth(credit).match(/\d+(?:\.\d+)?/);
  if (!match) return null;
  const value = Number(match[0]);
  return Number.isFinite(value) ? value : null;
}

function gradePointForScore(score) {
  const normalized = normalizeScore(score);
  return GRADE_POINTS.has(normalized) ? GRADE_POINTS.get(normalized) : null;
}

function hasRequiredMarker(value) {
  const text = normalize(value);
  return text.includes('必修') && !text.includes('非必修');
}

function isRequiredRow(row) {
  const markers = [row.type, row.property, row.nature, row.category, row.group];
  if (markers.some(hasRequiredMarker)) return true;
  return Array.isArray(row.raw) && row.raw.some(hasRequiredMarker);
}

function formatGpa(gpa) {
  return gpa === null ? '暂无可计算必修绩点' : gpa.toFixed(2);
}

function calculateRequiredGpa(rows) {
  let weightedPoints = 0;
  let totalCredits = 0;
  let counted = 0;
  let required = 0;

  for (const row of rows) {
    if (!isRequiredRow(row)) continue;
    required += 1;
    const credit = parseCredit(row.credit);
    const gradePoint = gradePointForScore(row.score);
    if (credit === null || gradePoint === null) continue;
    weightedPoints += gradePoint * credit;
    totalCredits += credit;
    counted += 1;
  }

  const gpa = totalCredits > 0 ? weightedPoints / totalCredits : null;
  return {
    gpa,
    formatted: formatGpa(gpa),
    counted,
    required,
    credits: totalCredits
  };
}

function formatStartupSummary(rows, gpaInfo) {
  return `当前共有${rows.length}科成绩，绩点为：${gpaInfo.formatted}\n时间：${nowText()}`;
}

function formatChanges(changes, gpaInfo) {
  return changes.map((change) => {
    const credit = change.row.credit || '-';
    const courseName = change.row.name || '未知课程';
    const courseLabel = courseName.endsWith('课') ? courseName : `${courseName}课`;
    const score = change.type === 'changed'
      ? `${change.oldScore} -> ${change.row.score}`
      : change.row.score;
    return `${courseLabel}成绩：${score}\n学分：${credit}\n当前总绩点为：${gpaInfo.formatted}`;
  });
}

async function extractRowsFromFrame(frame) {
  let rows = [];
  try {
    rows = await frame.evaluate(() => {
      function textOf(node) {
        return (node && (node.textContent || node.innerText) || '').replace(/\s+/g, ' ').trim();
      }
      function normalizeHeader(name) {
        return name.replace(/\s+/g, '');
      }

      const result = [];
      const tables = Array.from(document.querySelectorAll('table'));
      for (const table of tables) {
        let headerCells = null;
        const tableRows = Array.from(table.querySelectorAll('tr'));
        for (const tr of tableRows) {
          const cells = Array.from(tr.children).map(textOf);
          if (cells.length < 5) continue;
          const joined = cells.join('|');
          if (!headerCells && /课程名称/.test(joined) && /成绩/.test(joined)) {
            headerCells = cells.map(normalizeHeader);
            continue;
          }
          if (!headerCells || !/^\d+$/.test(cells[0])) continue;

          const item = {};
          headerCells.forEach((header, index) => {
            item[header] = cells[index] || '';
          });
          const name = item['课程名称'] || cells[3] || '';
          const score = item['成绩'] || cells[5] || '';
          if (!name || !score) continue;

          result.push({
            term: item['开课学期'] || cells[1] || '',
            code: item['课程编号'] || cells[2] || '',
            name,
            credit: item['学分'] || cells[4] || '',
            score,
            method: item['修读方式'] || cells[6] || '',
            type: item['类型'] || item['课程类型'] || item['课程性质'] || item['课程属性'] || cells[7] || '',
            property: item['课程属性'] || item['课程性质'] || item['课程类型'] || item['类型'] || cells[7] || '',
            group: item['课组类别'] || cells[8] || '',
            raw: cells
          });
        }
      }
      return result;
    });
  } catch (error) {
    rows = [];
  }

  for (const child of frame.childFrames()) {
    rows = rows.concat(await extractRowsFromFrame(child));
  }
  return rows;
}

async function extractRowsFromVisiblePage(page) {
  return extractRowsFromFrame(page.mainFrame());
}

async function fetchRowsInPage(page, listUrl, term, timeoutMs) {
  return page.evaluate(async ({ listUrl, term, timeoutMs }) => {
    function textOf(node) {
      return (node && (node.textContent || node.innerText) || '').replace(/\s+/g, ' ').trim();
    }
    function extract(doc) {
      const result = [];
      const tables = Array.from(doc.querySelectorAll('table'));
      for (const table of tables) {
        let headerCells = null;
        for (const tr of Array.from(table.querySelectorAll('tr'))) {
          const cells = Array.from(tr.children).map(textOf);
          if (cells.length < 5) continue;
          const joined = cells.join('|');
          if (!headerCells && /课程名称/.test(joined) && /成绩/.test(joined)) {
            headerCells = cells.map((value) => value.replace(/\s+/g, ''));
            continue;
          }
          if (!headerCells || !/^\d+$/.test(cells[0])) continue;
          const item = {};
          headerCells.forEach((header, index) => {
            item[header] = cells[index] || '';
          });
          const name = item['课程名称'] || cells[3] || '';
          const score = item['成绩'] || cells[5] || '';
          if (!name || !score) continue;
          result.push({
            term: item['开课学期'] || cells[1] || '',
            code: item['课程编号'] || cells[2] || '',
            name,
            credit: item['学分'] || cells[4] || '',
            score,
            method: item['修读方式'] || cells[6] || '',
            type: item['类型'] || item['课程类型'] || item['课程性质'] || item['课程属性'] || cells[7] || '',
            property: item['课程属性'] || item['课程性质'] || item['课程类型'] || item['类型'] || cells[7] || '',
            group: item['课组类别'] || cells[8] || '',
            raw: cells
          });
        }
      }
      return result;
    }

    function detectTerm() {
      const select = document.querySelector('select[name="kksj"],#kksj');
      if (select) return select.value || '';
      const frame = document.querySelector('iframe[src*="cjcx_list"],frame[src*="cjcx_list"]');
      const match = frame && (frame.getAttribute('src') || '').match(/[?&]kksj=([^&]+)/);
      return match ? decodeURIComponent(match[1]) : '';
    }

    const actualTerm = term === 'auto' ? detectTerm() : (term || '');
    const url = new URL(listUrl, location.origin);
    if (actualTerm) url.searchParams.set('kksj', actualTerm);
    url.searchParams.set('_', String(Date.now()));

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const response = await fetch(url.toString(), {
        credentials: 'include',
        cache: 'no-store',
        signal: controller.signal
      });
      const html = await response.text();
      const doc = new DOMParser().parseFromString(html, 'text/html');
      const rows = extract(doc);
      const bodyText = textOf(doc.body).slice(0, 500);
      return {
        ok: response.ok,
        status: response.status,
        url: response.url,
        rows,
        bodyText
      };
    } finally {
      clearTimeout(timer);
    }
  }, { listUrl, term, timeoutMs });
}

async function refreshListFrame(page, listUrl, term) {
  return page.evaluate(({ listUrl, term }) => {
    function detectTerm() {
      const select = document.querySelector('select[name="kksj"],#kksj');
      if (select) return select.value || '';
      const frame = document.querySelector('iframe[src*="cjcx_list"],frame[src*="cjcx_list"]');
      const match = frame && (frame.getAttribute('src') || '').match(/[?&]kksj=([^&]+)/);
      return match ? decodeURIComponent(match[1]) : '';
    }
    function findFrame(doc) {
      const direct = doc.querySelector('iframe[src*="cjcx_list"],frame[src*="cjcx_list"]');
      if (direct) return direct;
      for (const frame of Array.from(doc.querySelectorAll('iframe,frame'))) {
        try {
          const found = findFrame(frame.contentDocument);
          if (found) return found;
        } catch (error) {
          // Ignore inaccessible frames.
        }
      }
      return null;
    }

    const target = findFrame(document);
    if (!target) return { refreshed: false, reason: 'no cjcx_list frame' };

    const actualTerm = term === 'auto' ? detectTerm() : (term || '');
    const url = new URL(listUrl, location.origin);
    if (actualTerm) url.searchParams.set('kksj', actualTerm);
    url.searchParams.set('_', String(Date.now()));
    target.src = url.pathname + url.search;
    return { refreshed: true, url: url.toString(), term: actualTerm };
  }, { listUrl, term });
}

async function ensureGradePage(page, config) {
  const url = page.url();
  if (config.manualLogin) return;
  if (!url || url === 'about:blank' || !url.includes('/jsxsd/')) {
    await page.goto(config.gradeUrl, { waitUntil: 'domcontentloaded', timeout: 60000 });
  }
}

async function checkGrades(page, config) {
  await ensureGradePage(page, config);

  let fetched = null;
  try {
    fetched = await fetchRowsInPage(page, config.listUrl, config.term, config.requestTimeoutMs);
    if (fetched.rows && fetched.rows.length) {
      return { rows: fetched.rows, source: 'fetch', detail: fetched };
    }
  } catch (error) {
    fetched = { error: error.message };
  }

  let refreshed = null;
  try {
    refreshed = await refreshListFrame(page, config.listUrl, config.term);
    await page.waitForTimeout(3000);
  } catch (error) {
    refreshed = { refreshed: false, error: error.message };
  }

  const visibleRows = await extractRowsFromVisiblePage(page);
  return { rows: visibleRows, source: 'visible', detail: { fetched, refreshed } };
}

async function checkGradesAcrossPages(context, config, preferredPage) {
  const pages = context.pages().filter((candidate) => !candidate.isClosed());
  const orderedPages = [];
  if (preferredPage && !preferredPage.isClosed()) orderedPages.push(preferredPage);
  for (const candidate of pages) {
    if (!orderedPages.includes(candidate)) orderedPages.push(candidate);
  }
  const pagesToScan = orderedPages.slice(0, config.browser.maxScanPages);

  const attempts = [];
  for (const candidate of pagesToScan) {
    const url = candidate.url();
    const isJwPage = /newjw\.cau\.edu\.cn|\/jsxsd\//i.test(url);

    if (isJwPage) {
      try {
        const result = await checkGrades(candidate, config);
        if (result.rows && result.rows.length) {
          return { page: candidate, ...result };
        }
        attempts.push({ url, source: result.source, detail: result.detail });
      } catch (error) {
        attempts.push({ url, checkError: error.message });
      }
    }

    let rows = [];
    try {
      rows = await extractRowsFromVisiblePage(candidate);
      if (rows.length) {
        return {
          page: candidate,
          rows,
          source: isJwPage ? 'visible-after-fetch-fallback' : 'visible-non-grade-tab',
          detail: { url }
        };
      }
    } catch (error) {
      attempts.push({ url, visibleError: error.message });
      continue;
    }

    if (!isJwPage) {
      attempts.push({ url, source: 'skip-non-grade-tab' });
    } else {
      attempts.push({ url, source: 'no-grade-rows-after-fetch' });
    }
  }

  return {
    page: preferredPage,
    rows: [],
    source: 'all-tabs',
    detail: {
      message: 'No grade table found in any open tab. Open the grade query page in this browser window.',
      scannedPages: pagesToScan.length,
      totalPages: orderedPages.length,
      attempts: attempts.slice(0, 10)
    }
  };
}

function loadState(file) {
  if (!fs.existsSync(file)) return null;
  return readJson(file);
}

function saveState(file, rows) {
  writeJson(file, {
    updatedAt: new Date().toISOString(),
    rows,
    signature: signature(rows)
  });
}

function browserViewport(config) {
  const viewport = config.browser.viewport || DEFAULT_LOW_RESOURCE_VIEWPORT;
  if (!config.browser.lowResourceMode) return viewport;
  return {
    width: Math.min(positiveNumber(viewport.width, DEFAULT_LOW_RESOURCE_VIEWPORT.width), DEFAULT_LOW_RESOURCE_VIEWPORT.width),
    height: Math.min(positiveNumber(viewport.height, DEFAULT_LOW_RESOURCE_VIEWPORT.height), DEFAULT_LOW_RESOURCE_VIEWPORT.height)
  };
}

async function launchBrowser(config) {
  const launchOptions = {
    headless: Boolean(config.browser.headless),
    viewport: browserViewport(config),
    deviceScaleFactor: 1
  };
  if (config.browser.channel) launchOptions.channel = config.browser.channel;
  if (config.browser.executablePath) launchOptions.executablePath = config.browser.executablePath;
  launchOptions.args = [...config.browser.args];
  if (config.browser.proxyPacFile) {
    launchOptions.args.push(`--proxy-pac-url=${pathToFileURL(config.browser.proxyPacFile).href}`);
    log(`Browser PAC enabled: ${config.browser.proxyPacFile}`);
  }
  if (config.browser.loadExtensionDir) {
    launchOptions.args.push(`--disable-extensions-except=${config.browser.loadExtensionDir}`);
    launchOptions.args.push(`--load-extension=${config.browser.loadExtensionDir}`);
    log(`Browser extension enabled: ${config.browser.loadExtensionDir}`);
  }
  const proxy = proxyOptions(config);
  if (proxy && config.proxy.browser) {
    launchOptions.proxy = proxy;
    log(`Browser proxy enabled: ${displayProxyServer(proxy.server)}`);
  } else if (proxy) {
    log(`Browser proxy disabled; proxy remains available for keepalive: ${displayProxyServer(proxy.server)}`);
  }

  fs.mkdirSync(config.browser.userDataDir, { recursive: true });
  return chromium.launchPersistentContext(config.browser.userDataDir, launchOptions);
}

async function closeExtraPages(context, activePage, config) {
  if (!config.browser.closeExtraPages || !activePage || activePage.isClosed()) return;
  const extraPages = context.pages().filter((candidate) => candidate !== activePage && !candidate.isClosed());
  if (!extraPages.length) return;

  let closed = 0;
  for (const candidate of extraPages) {
    try {
      await candidate.close({ runBeforeUnload: false });
      closed += 1;
    } catch (error) {
      log(`Failed to close extra browser tab: ${error.message}`);
    }
  }
  if (closed) log(`Closed ${closed} extra browser tab(s) to reduce memory use.`);
}

async function enableResourceBlocking(context, config) {
  if (!config.browser.blockResourcesAfterFirstSuccess) return false;
  const blockedTypes = new Set(config.browser.blockResourceTypes.map((value) => String(value || '').toLowerCase()));
  if (!blockedTypes.size) return false;

  await context.route('**/*', async (route) => {
    const resourceType = route.request().resourceType();
    if (blockedTypes.has(resourceType)) {
      await route.abort().catch(() => null);
      return;
    }
    await route.continue().catch(() => null);
  });
  log(`Low resource mode: blocking future ${Array.from(blockedTypes).join(', ')} requests.`);
  return true;
}

async function askEnter(prompt) {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  await new Promise((resolve) => rl.question(prompt, resolve));
  rl.close();
}

async function testFeishu(config) {
  if (!config.feishu.webhook) {
    throw new Error('FEISHU_WEBHOOK_URL is empty. Edit .env before testing Feishu.');
  }
  await sendFeishu(config, `成绩监控飞书测试成功\n时间：${nowText()}`);
  log('Feishu test sent.');
}

async function runMonitor(config, options) {
  const context = await launchBrowser(config);
  let page = context.pages()[0];
  if (!page) page = await context.newPage();

  let state = loadState(config.stateFile);
  let lastErrorNotifyAt = 0;
  let lastNoRowsLogAt = 0;
  let lastNoChangeLogAt = 0;
  let emptyResultStreak = 0;
  let startupNotified = false;
  let resourceBlockingInitialized = false;
  let checkInProgress = false;

  const startUrl = config.manualLogin ? (config.loginUrl || config.gradeUrl) : config.gradeUrl;
  log('Browser started.');
  emitGuiEvent('browser_started', { url: startUrl });
  if (config.manualLogin) {
    log('Manual login mode: log in through the opened portal page, then open the grade query page in this same browser window.');
  } else {
    log('Auto navigation mode: if the grade page asks for login, please log in in the opened browser window.');
  }
  await page.goto(startUrl, { waitUntil: 'domcontentloaded', timeout: 60000 }).catch((error) => {
    log(`Initial navigation failed: ${error.message}`);
  });

  log(`VPN keepalive disabled. Grade polling runs every ${config.intervalSeconds}s.`);

  async function performCheck(reason) {
    if (checkInProgress) {
      log(`Skip ${reason} check: another check is already running.`);
      return { ok: false, skipped: true, reason: 'check already running' };
    }

    checkInProgress = true;
    let nextSleepSeconds = config.intervalSeconds;
    try {
      const result = config.manualLogin
        ? await checkGradesAcrossPages(context, config, page)
        : await checkGrades(page, config).then((singleResult) => ({ page, ...singleResult }));
      if (result.page && !result.page.isClosed()) page = result.page;
      const rows = result.rows || [];
      if (!rows.length) {
        emptyResultStreak += 1;
        const hasBaseline = Boolean(state && state.rows && state.rows.length);
        const message = [
          'No grade table parsed yet. Login may be pending/expired, or the grade query page is not open.',
          hasBaseline
            ? `empty result streak: ${emptyResultStreak}/${config.emptyResultNotifyAfter}; previous baseline rows: ${state.rows.length}`
            : `empty result streak: ${emptyResultStreak}`,
          `source: ${result.source}`,
          `detail: ${JSON.stringify(result.detail).slice(0, 700)}`
        ].join('\n');
        const logNow = Date.now();
        if (emptyResultStreak === 1 || !lastNoRowsLogAt || reason !== 'scheduled' || logNow - lastNoRowsLogAt > config.logging.loginNeededEverySeconds * 1000) {
          log(message);
          lastNoRowsLogAt = logNow;
        }
        nextSleepSeconds = hasBaseline ? config.emptyResultRetrySeconds : config.initialLoginRetrySeconds;
        const now = Date.now();
        const shouldNotify = reason !== 'scheduled' || !hasBaseline || emptyResultStreak >= config.emptyResultNotifyAfter;
        if (shouldNotify && (reason !== 'scheduled' || now - lastErrorNotifyAt > config.errorNotifyCooldownSeconds * 1000)) {
          await sendFeishu(config, `[Grade Monitor] Login/navigation needed\n${message}`);
          lastErrorNotifyAt = now;
        }
        emitGuiEvent('login_needed', {
          emptyResultStreak,
          previousRows: hasBaseline ? state.rows.length : 0,
          source: result.source
        });
        return { ok: false, rows: 0, nextSleepSeconds, message };
      }

      if (emptyResultStreak) {
        log(`Grade table recovered after ${emptyResultStreak} empty check(s). Rows=${rows.length}; source=${result.source}.`);
        emptyResultStreak = 0;
      }

      if (!resourceBlockingInitialized) {
        await enableResourceBlocking(context, config).catch((error) => {
          log(`Failed to enable low resource request blocking: ${error.message}`);
        });
        resourceBlockingInitialized = true;
      }
      await closeExtraPages(context, page, config);

      const gpaInfo = calculateRequiredGpa(rows);
      const startupSummary = formatStartupSummary(rows, gpaInfo);
      emitGuiEvent('snapshot', {
        rows: rows.length,
        gpa: gpaInfo.formatted,
        required: gpaInfo.required,
        countedRequired: gpaInfo.counted,
        credits: gpaInfo.credits,
        source: result.source
      });
      async function sendStartupSummaryOnce() {
        if (config.feishu.notifyOnStart && !startupNotified) {
          await sendFeishu(config, startupSummary);
          startupNotified = true;
        }
      }

      if (!state || !state.rows || !state.rows.length) {
        saveState(config.stateFile, rows);
        state = loadState(config.stateFile);
        log(`Baseline saved: ${rows.length} rows.`);
        await sendStartupSummaryOnce();
        if (reason !== 'scheduled') {
          await sendFeishu(config, `[Grade Monitor] Manual check OK\n${startupSummary}\nBaseline saved`);
        }
        return { ok: true, rows: rows.length, nextSleepSeconds, baselineSaved: true };
      } else {
        await sendStartupSummaryOnce();
        const changes = diffRows(state.rows, rows);
        const currentSignature = signature(rows);
        if (changes.length && currentSignature !== state.signature) {
          const lines = formatChanges(changes, gpaInfo);
          const text = [
            '检测到新成绩或成绩变化',
            `时间：${nowText()}`,
            '',
            ...lines.slice(0, 30),
            lines.length > 30 ? `... ${lines.length - 30} more` : ''
          ].filter(Boolean).join('\n');
          log(text);
          emitGuiEvent('grades_changed', {
            count: changes.length,
            courses: changes.slice(0, 30).map((change) => ({
              type: change.type,
              name: change.row.name,
              score: change.row.score,
              oldScore: change.oldScore || '',
              credit: change.row.credit || ''
            }))
          });
          await sendFeishu(config, text);
          saveState(config.stateFile, rows);
          state = loadState(config.stateFile);
          return { ok: true, rows: rows.length, changes: changes.length, nextSleepSeconds };
        } else {
          const now = Date.now();
          if (!lastNoChangeLogAt || reason !== 'scheduled' || now - lastNoChangeLogAt > config.logging.noChangeEverySeconds * 1000) {
            log(`No change. Rows=${rows.length}; source=${result.source}.`);
            lastNoChangeLogAt = now;
          }
          if (reason !== 'scheduled') {
            await sendFeishu(config, `[Grade Monitor] Manual check OK\n${startupSummary}\nNo change`);
          }
          return { ok: true, rows: rows.length, changes: 0, nextSleepSeconds };
        }
      }
    } catch (error) {
      log(`Check failed: ${error.stack || error.message}`);
      emitGuiEvent('check_failed', { message: error.message });
      const now = Date.now();
      if (reason !== 'scheduled' || now - lastErrorNotifyAt > config.errorNotifyCooldownSeconds * 1000) {
        await sendFeishu(config, `[Grade Monitor] Check failed\n${error.message}\nTime: ${nowText()}`).catch((sendError) => {
          log(`Failed to send error notification: ${sendError.message}`);
        });
        lastErrorNotifyAt = now;
      }
      return { ok: false, error: error.message, nextSleepSeconds };
    } finally {
      checkInProgress = false;
    }
  }

  while (true) {
    const checkResult = await performCheck('scheduled');
    const nextSleepSeconds = checkResult.nextSleepSeconds || config.intervalSeconds;

    if (options.once) {
      await context.close();
      return;
    }

    await sleep(nextSleepSeconds * 1000);
  }
}

async function main() {
  const args = parseArgs(process.argv);
  const config = loadConfig(args.config);

  if (args.testFeishu) {
    await testFeishu(config);
    return;
  }

  if (!config.feishu.webhook && !args.noPrompt) {
    log('WARNING: FEISHU_WEBHOOK_URL is empty. Notifications will be skipped.');
    await askEnter('Press Enter to continue anyway, or Ctrl+C to stop...');
  }

  await runMonitor(config, { once: args.once });
}

main().catch((error) => {
  emitGuiEvent('fatal', { message: error.message });
  console.error(error.stack || error.message);
  process.exitCode = 1;
});
