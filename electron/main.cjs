'use strict';

const { app, BrowserWindow, dialog, ipcMain, Menu, nativeTheme, powerMonitor, screen, Tray } = require('electron');
const path = require('node:path');
const { spawn } = require('node:child_process');
const os = require('node:os');
const fs = require('node:fs');
const Bridge = require('./bridge.cjs');

const product = 'Codex Limits Widget';
const icon = path.join(__dirname, '..', 'assets', 'app.ico');
const preload = path.join(__dirname, 'preload.cjs');
const demo = process.argv.includes('--demo');
const local = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
const data = path.join(local, 'CodexLimitsWidget', 'data');
const size = { width: 356, height: 156 };
const compactIsland = { width: 288, height: 34, radius: 17 };
const expandedIsland = { width: 352, height: 152, radius: 26 };

let bridge, tray, island, preferences, details, history;
let settings = {};
let state = { demo, busy: !demo };
let startup = false;
let placement = {};
let suspended = false;
let locked = false;
let hovering = false;
let ignoreMouse = true;
let lastInside = -Infinity;
let dataTimer, placementTimer, pointerTimer, glassTimer;
let glassReadyAt = 0;
let glassFrame = null;
let readingGlass = false;
let ticking = false;
let checking = false;
let forceRefresh = false;
let menuOpen = false;
let quitting = false;
let demoDirectory;
let positionKey;
let loginChild;
let loginInProgress = false;
let loginPromptShown = false;

function browserOptions(options) {
  return {
    ...options, show: false, icon,
    webPreferences: { preload, sandbox: true, contextIsolation: true, nodeIntegration: false }
  };
}

function load(window, view) {
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', event => event.preventDefault());
  window.loadFile(path.join(__dirname, 'ui', 'index.html'), { query: { view } }).catch(report);
}

function send(window, channel, value) {
  if (window && !window.isDestroyed()) window.webContents.send(channel, value);
}

function current() {
  return { state, settings, startup, hovering, loginInProgress, reduceTransparency: reducedGlass(), canSetStartup: app.isPackaged && !demo };
}

function broadcast() {
  for (const window of [island, preferences, details, history]) send(window, 'widget:update', current());
}

function report(error) {
  if (quitting) return;
  console.error(error);
  state = { ...state, busy: false, status: error.message };
  broadcast();
}

function reducedGlass() {
  return nativeTheme.prefersReducedTransparency || nativeTheme.shouldUseHighContrastColors;
}

function invalidateGlass() {
  glassReadyAt = performance.now() + 320;
  if (glassFrame) {
    glassFrame = null;
    send(island, 'widget:glass', null);
  }
}

async function sampleGlass() {
  if (readingGlass || quitting || !island?.isVisible() || !settings.GlassIsland ||
      reducedGlass() || performance.now() < glassReadyAt) return;

  const generation = glassReadyAt;
  const { width, height } = hovering ? expandedIsland : compactIsland;
  const origin = island.getContentBounds();
  const bounds = screen.dipToScreenRect(island, {
    x: origin.x + (size.width - width) / 2, y: origin.y + 2, width, height
  });

  readingGlass = true;
  try {
    const frame = await bridge.request('glass', { ...bounds, logicalWidth: width, expanded: hovering });
    if (generation !== glassReadyAt || !island.isVisible() || reducedGlass()) return;
    if (glassFrame?.image !== frame?.image || glassFrame?.bright !== frame?.bright) {
      glassFrame = frame;
      send(island, 'widget:glass', frame);
    }
  } catch {
    // The CSS lens is also available when the Windows desktop can't be sampled.
    if (generation === glassReadyAt) invalidateGlass();
  } finally {
    readingGlass = false;
  }
}

function selectedDisplay() {
  const primary = screen.getPrimaryDisplay();
  if (settings.DisplayId == null) return primary;
  return screen.getAllDisplays().find(display => display.id === settings.DisplayId) ?? primary;
}

function centerIsland() {
  if (!island) return;
  const area = selectedDisplay().workArea;
  const x = Math.round(area.x + (area.width - size.width) / 2);
  const y = area.y + 10;
  const key = x + ':' + y;
  if (positionKey !== key) {
    island.setPosition(x, y);
    invalidateGlass();
  }
  positionKey = key;
}

function roundedHit(point, width, height, radius) {
  const x = Math.abs(point.x - size.width / 2);
  const y = Math.abs(point.y - height / 2 - 2);
  if (x > width / 2 || y > height / 2) return false;
  const cornerX = Math.max(0, x - width / 2 + radius);
  const cornerY = Math.max(0, y - height / 2 + radius);
  return cornerX ** 2 + cornerY ** 2 <= radius ** 2;
}

function trackPointer() {
  if (!island?.isVisible()) return;
  const [x, y] = island.getPosition();
  const cursor = screen.getCursorScreenPoint();
  const point = { x: cursor.x - x, y: cursor.y - y };
  const { width, height, radius } = hovering ? expandedIsland : compactIsland;
  const inside = roundedHit(point, width, height, radius);
  if (inside) lastInside = performance.now();
  const expanded = menuOpen || inside || performance.now() - lastInside < 155;
  if (expanded !== hovering) {
    hovering = expanded;
    invalidateGlass();
    send(island, 'widget:update', current());
  }
  const ignore = !inside;
  if (ignore !== ignoreMouse) {
    ignoreMouse = ignore;
    island.setIgnoreMouseEvents(ignoreMouse);
  }
}

function syncVisibility() {
  if (!island || quitting) return;
  if (placement.hidden || suspended || locked) {
    if (island.isVisible()) invalidateGlass();
    island.hide();
    details?.hide();
    hovering = false;
    lastInside = -Infinity;
    send(island, 'widget:update', current());
  } else if (!island.isVisible()) {
    centerIsland();
    island.showInactive();
  }
}

async function checkPlacement() {
  if (checking || quitting) return;
  checking = true;
  try {
    const next = await bridge.request('placement');
    const appChanged = next.app !== placement.app;
    placement = next;
    syncVisibility();
    if (appChanged) updateTrayMenu();
  } catch (error) {
    report(error);
  } finally {
    checking = false;
  }
}

async function tick(refresh = false) {
  forceRefresh ||= refresh;
  if (ticking || quitting) return;
  ticking = true;
  const method = forceRefresh ? 'refresh' : 'tick';
  forceRefresh = false;
  try {
    const previous = state;
    state = await bridge.request(method);
    if (state.alert) tray.displayBalloon({ title: state.alert.Title, content: state.alert.Message });
    if (previous.snapshot?.fetchedAt !== state.snapshot?.fetchedAt) updateTrayTooltip();
    if (previous.loginRequired !== state.loginRequired) updateTrayMenu();
    broadcast();
    promptForLogin();
  } catch (error) {
    report(error);
  } finally {
    ticking = false;
  }
}

function promptForLogin() {
  if (!state.loginRequired) {
    loginPromptShown = false;
    return;
  }
  if (demo || loginPromptShown || loginInProgress || suspended || locked || quitting) return;
  loginPromptShown = true;
  dialog.showMessageBox({
    type: 'info', title: product, message: 'Войдите в ChatGPT',
    detail: 'Для просмотра лимитов Codex потребуется вход в браузере.',
    buttons: ['Войти', 'Позже'], defaultId: 0, cancelId: 1
  }).then(({ response }) => {
    if (response === 0) return loginWithChatGpt();
  }).catch(report);
}

async function loginWithChatGpt() {
  if (demo || quitting || loginInProgress || !state.loginRequired) return;
  loginInProgress = true;
  broadcast();
  updateTrayMenu();
  try {
    const executable = await bridge.request('login-path');
    if (quitting) return;
    await new Promise((resolve, reject) => {
      loginChild = spawn(executable, ['login'], { windowsHide: true, stdio: 'ignore' });
      loginChild.once('error', reject);
      loginChild.once('exit', code => {
        if (code === 0 || quitting) resolve();
        else reject(new Error('Вход через Codex CLI не завершён. Повторите вход из меню трея.'));
      });
    });
    if (!quitting) tick(true);
  } catch (error) {
    if (!quitting) dialog.showErrorBox(product, error.message);
  } finally {
    loginChild = null;
    loginInProgress = false;
    if (!quitting) {
      broadcast();
      updateTrayMenu();
    }
  }
}

function updateTrayTooltip() {
  const snapshot = state.snapshot;
  tray.setToolTip(snapshot
    ? product + ' · 5 ч ' + (snapshot.FiveHour?.PercentText ?? '—') + ' · неделя ' + (snapshot.Weekly?.PercentText ?? '—')
    : product);
}

async function save(patch) {
  const wasGlass = settings.GlassIsland;
  const wasDisplay = settings.DisplayId;
  settings = await bridge.request('save', { patch });
  if (wasGlass !== settings.GlassIsland) invalidateGlass();
  if (wasDisplay !== settings.DisplayId) centerIsland();
  broadcast();
  updateTrayMenu();
  return settings;
}

async function setStartup(enabled) {
  if (!app.isPackaged || demo) return false;
  startup = await bridge.request('startup', { enabled });
  broadcast();
  updateTrayMenu();
  return startup;
}

function displayMenu() {
  const primary = screen.getPrimaryDisplay();
  const displays = [...screen.getAllDisplays()].sort((a, b) =>
    (a.id !== primary.id) - (b.id !== primary.id) || a.bounds.x - b.bounds.x || a.bounds.y - b.bounds.y);
  const hasSelected = displays.some(display => display.id === settings.DisplayId);
  return [
    { label: 'Основной автоматически', type: 'radio', checked: !hasSelected,
      click: () => save({ DisplayId: null }).catch(report) },
    ...displays.map((display, index) => {
      const name = display.label?.trim().replaceAll('&', '&&');
      const width = Math.round(display.size.width * display.scaleFactor);
      const height = Math.round(display.size.height * display.scaleFactor);
      const label = 'Монитор ' + (index + 1) + (name ? ' — ' + name : '') + ' (' + width + '×' + height + ')';
      return { label, type: 'radio', checked: display.id === settings.DisplayId,
        click: () => save({ DisplayId: display.id }).catch(report) };
    })
  ];
}

function menuTemplate() {
  const exclusions = settings.GameExclusions || [];
  const add = placement.app && !exclusions.some(name => name.toLowerCase() === placement.app.toLowerCase());
  return [
    { label: 'Обновить сейчас', enabled: !demo, click: () => tick(true) },
    { label: 'Войти в ChatGPT', visible: Boolean(state.loginRequired), enabled: !loginInProgress, click: loginWithChatGpt },
    { label: 'Показать виджет', click: checkPlacement },
    { label: 'Монитор', submenu: displayMenu() },
    { label: 'Подробнее', click: () => openPanel('details') },
    { label: 'История расхода', click: () => openPanel('history') },
    { label: 'Настройки', click: () => openPanel('settings') },
    { type: 'separator' },
    { label: 'Тема', submenu: [
      { label: 'Светлая', type: 'radio', checked: !settings.DarkTheme && !settings.GlassIsland, click: () => save({ DarkTheme: false, GlassIsland: false }).catch(report) },
      { label: 'Тёмная', type: 'radio', checked: settings.DarkTheme && !settings.GlassIsland, click: () => save({ DarkTheme: true, GlassIsland: false }).catch(report) },
      { label: 'Liquid Glass', type: 'radio', checked: settings.GlassIsland, click: () => save({ DarkTheme: true, GlassIsland: true }).catch(report) }
    ] },
    { label: 'Исключения для игр', submenu: [
      { label: placement.app ? 'Скрывать в ' + placement.app : 'Переключитесь на нужную игру',
        enabled: Boolean(add),
        click: () => save({ GameExclusions: [...exclusions, placement.app] }).catch(report) },
      ...exclusions.map(name => ({
        label: 'Убрать ' + name,
        click: () => save({ GameExclusions: exclusions.filter(item => item !== name) }).catch(report)
      }))
    ] },
    { label: 'Запускать при входе в Windows', type: 'checkbox', checked: startup, enabled: app.isPackaged && !demo,
      click: item => setStartup(item.checked).catch(report) },
    { type: 'separator' },
    { label: 'Закрыть', click: () => app.quit() }
  ];
}

function updateTrayMenu() {
  tray.setContextMenu(Menu.buildFromTemplate(menuTemplate()));
}

function showMenu() {
  menuOpen = true;
  Menu.buildFromTemplate(menuTemplate()).popup({ window: island, callback: () => { menuOpen = false; } });
}

const panels = {
  settings: { label: 'Настройки', width: 580, height: 760 },
  details: { label: 'Лимиты Codex', width: 462, height: 570 },
  history: { label: 'История расхода', width: 790, height: 730 }
};

function panel(view) {
  return view === 'settings' ? preferences : view === 'details' ? details : history;
}

function setPanel(view, window) {
  if (view === 'settings') preferences = window;
  else if (view === 'details') details = window;
  else history = window;
}

function openPanel(view) {
  if (view !== 'details') details?.hide();
  const existing = panel(view);
  const config = panels[view];
  const display = selectedDisplay();
  const area = display.workArea;
  const width = Math.min(config.width, area.width - 20);
  const height = Math.min(config.height, area.height - 64);
  const x = Math.round(area.x + (area.width - width) / 2);
  const y = area.y + 54;
  if (existing) {
    if (screen.getDisplayMatching(existing.getBounds()).id !== display.id) existing.setBounds({ x, y, width, height });
    existing.show();
    return;
  }
  const window = new BrowserWindow(browserOptions({
    title: product + ' · ' + config.label, autoHideMenuBar: true, alwaysOnTop: true,
    skipTaskbar: true, width, height, minWidth: Math.min(width, 370), minHeight: Math.min(height, 300),
    x, y,
    backgroundColor: settings.DarkTheme ? '#101319' : '#f4f6fa'
  }));
  setPanel(view, window);
  window.once('ready-to-show', () => window.show());
  window.on('closed', () => setPanel(view, null));
  if (view === 'details') window.on('blur', () => window.hide());
  load(window, view);
}

function registerIpc() {
  function handle(channel, windows, action) {
    ipcMain.handle('widget:' + channel, (event, ...args) => {
      if (event.senderFrame !== event.sender.mainFrame ||
          !windows().some(window => window?.webContents === event.sender)) {
        throw new Error('Недоступная операция.');
      }
      return action(...args);
    });
  }

  handle('initial', () => [island, preferences, details, history], current);
  handle('refresh', () => [island, details], () => tick(true));
  handle('login', () => [details], loginWithChatGpt);
  handle('menu', () => [island], showMenu);
  handle('open', () => [island, details], view => {
    if (Object.hasOwn(panels, view)) openPanel(view);
  });
  handle('details', () => [details], () => bridge.request('details'));
  handle('history', () => [history], days => {
    if (![1, 7, 30, 90, 180, 365].includes(days)) days = 1;
    return bridge.request('history', { days });
  });
  handle('save', () => [preferences, history], (patch) => save(patch));
  handle('startup', () => [preferences], enabled => setStartup(enabled === true));
  handle('clear', () => [preferences], async () => {
    const { response } = await dialog.showMessageBox(preferences, {
      type: 'warning', message: 'Удалить историю расхода на этом ПК?',
      buttons: ['Отмена', 'Удалить'], defaultId: 0, cancelId: 0
    });
    if (response !== 1) return false;
    await bridge.request('clear');
    send(history, 'widget:history-changed');
    return true;
  });
}

function power(next) {
  Object.assign(next, { suspended: next.suspended ?? suspended, locked: next.locked ?? locked });
  ({ suspended, locked } = next);
  syncVisibility();
  bridge.request('power', next).then(checkPlacement).catch(report);
}

async function start() {
  app.setAppUserModelId('com.codexlimits.widget');
  const bridgePath = app.isPackaged
    ? path.join(process.resourcesPath, 'CodexLimitsBridge.exe')
    : path.join(__dirname, '..', 'bin', 'CodexLimitsBridge.exe');
  const launchPath = process.env.PORTABLE_EXECUTABLE_FILE || process.execPath;
  const source = app.isPackaged ? path.dirname(launchPath) : path.join(__dirname, '..');
  if (demo) demoDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-limits-demo-'));
  bridge = new Bridge(bridgePath, [
    String(process.pid), demoDirectory || data, demo ? 'demo' : 'live', launchPath,
    path.join(source, 'data'),
    path.join(local, 'Programs', product, 'data'),
    ...(!app.isPackaged ? [path.join(source, 'bin', 'data')] : [])
  ]);
  ({ settings, startup } = await bridge.request('settings'));
  tray = new Tray(icon);
  tray.on('click', () => tick(true));
  updateTrayTooltip();
  updateTrayMenu();

  island = new BrowserWindow(browserOptions({
    ...size, title: product, frame: false, transparent: true, backgroundColor: '#00000000',
    resizable: false, movable: false, focusable: false, alwaysOnTop: true, skipTaskbar: true,
    hasShadow: false, roundedCorners: false
  }));
  island.setIgnoreMouseEvents(true);
  centerIsland();
  registerIpc();
  island.once('ready-to-show', () => {
    checkPlacement();
    placementTimer = setInterval(checkPlacement, 300);
    pointerTimer = setInterval(trackPointer, 30);
    glassTimer = setInterval(sampleGlass, 400);
  });
  load(island, 'island');
  tick();
  dataTimer = setInterval(tick, 1000);
  function onDisplayChanged() {
    centerIsland();
    invalidateGlass();
    updateTrayMenu();
  }
  screen.on('display-metrics-changed', onDisplayChanged);
  nativeTheme.on('updated', () => { invalidateGlass(); broadcast(); });
  screen.on('display-added', onDisplayChanged);
  screen.on('display-removed', onDisplayChanged);
  powerMonitor.on('suspend', () => power({ suspended: true }));
  powerMonitor.on('resume', () => power({ suspended: false }));
  powerMonitor.on('lock-screen', () => power({ locked: true }));
  powerMonitor.on('unlock-screen', () => power({ locked: false }));
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => { if (island) checkPlacement(); });
  app.on('window-all-closed', () => app.quit());
  app.on('before-quit', () => {
    quitting = true;
    clearInterval(dataTimer);
    clearInterval(placementTimer);
    clearInterval(pointerTimer);
    clearInterval(glassTimer);
    loginChild?.kill();
    bridge?.close();
    tray?.destroy();
    if (demoDirectory) fs.rmSync(demoDirectory, { recursive: true, force: true });
  });
  app.whenReady().then(start).catch(error => {
    dialog.showErrorBox(product, error.message);
    app.quit();
  });
}
