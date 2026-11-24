'use strict';

const $ = (selector, root = document) => root.querySelector(selector);
const view = new URLSearchParams(location.search).get('view') || 'island';
const root = $('#app');
root.append($('#' + view + '-template').content.cloneNode(true));
document.body.classList.add(view + '-view');

const moscow = new Intl.DateTimeFormat('ru-RU', {
  timeZone: 'Europe/Moscow', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit'
});
const chartDay = new Intl.DateTimeFormat('ru-RU', { timeZone: 'Europe/Moscow', day: 'numeric', month: 'short' });
const chartTime = new Intl.DateTimeFormat('ru-RU', { timeZone: 'Europe/Moscow', hour: '2-digit', minute: '2-digit' });
const number = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 1 });
const rangeValues = [1, 7, 30, 90, 180, 365];
const swatches = ['#8caaff', '#86e3ca', '#90d5f5', '#c0aaff', '#ffc583', '#ffabc1'];
let model;
let settingsForm;
let settingsPanel;
let savedSettings;
let lastRead;
let historyResult;
let readId = 0;

function node(tag, text) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  return element;
}

function theme(settings) {
  return settings.GlassIsland ? 'glass' : settings.DarkTheme ? 'dark' : 'light';
}

function accent(value) {
  return value == null ? null : '#' + value.toString(16).padStart(6, '0');
}

function styleTheme(element, settings) {
  element.dataset.theme = theme(settings);
  element.style.setProperty('--accent', accent(settings.AccentRgb) || '');
  element.style.setProperty('--bar-thickness', (settings.BarThickness || 5) + 'px');
}

function countdown(reset) {
  if (!reset || Number.isNaN(new Date(reset * 1000).valueOf())) return '—';
  const minutes = Math.ceil((reset - Date.now() / 1000) / 60);
  if (minutes <= 0) return 'ожидание обновления';
  if (minutes >= 1440) return Math.floor(minutes / 1440) + ' д ' + Math.floor(minutes % 1440 / 60) + ' ч';
  if (minutes >= 60) return Math.floor(minutes / 60) + ' ч ' + minutes % 60 + ' мин';
  return minutes + ' мин';
}

function quotaPercent(quota) {
  if (!quota || quota.ResetsAt && quota.ResetsAt <= Date.now() / 1000) return '—';
  if (quota.Remaining > 0 && quota.Remaining < 1) return '<1%';
  return quota.PercentText;
}

function renderQuota(element, quota, settings, absolute = false) {
  const percentage = quotaPercent(quota);
  const meter = $('.meter', element);
  const time = $('small', element);
  $('strong', element).textContent = percentage;
  meter.hidden = settings.ShowUsage === false;
  meter.firstElementChild.style.width = (percentage === '—' ? 0 : quota.Remaining) + '%';
  time.hidden = settings.ShowRemaining === false;
  time.textContent = resetLabel(quota, absolute);
}

function resetLabel(quota, absolute = false) {
  const reset = countdown(quota?.ResetsAt);
  if (reset === '—') return 'Сброс: время неизвестно';
  const clock = absolute ? ' · ' + moscow.format(quota.ResetsAt * 1000) + ' МСК' : '';
  if (reset === 'ожидание обновления') return 'Сброс: ждём обновления' + clock;
  return 'Сброс через ' + reset + clock;
}

function quotaSignal(quota) {
  if (!quota || (quota.ResetsAt && quota.ResetsAt <= Date.now() / 1000)) return 'unknown';
  if (quota.Remaining === 0) return 'empty';
  if (quota.Remaining <= 5) return 'critical';
  if (quota.Remaining <= 15) return 'low';
  return 'normal';
}

function renderIsland(element, state, settings, expanded) {
  styleTheme(element, settings);
  element.classList.toggle('expanded', expanded);
  element.classList.toggle('busy', state.busy);
  element.classList.toggle('stale', state.stale);
  const action = $('.island-action', element);
  if (action instanceof HTMLButtonElement) {
    action.title = settings.ExpandOnClick ? (expanded ? 'Свернуть островок' : 'Раскрыть островок') : 'Обновить лимиты';
    if (settings.ExpandOnClick) action.setAttribute('aria-expanded', expanded);
    else action.removeAttribute('aria-expanded');
  }
  const five = state.snapshot?.FiveHour;
  const week = state.snapshot?.Weekly;
  const showWeek = week?.Remaining === 0 || (!five && Boolean(week));
  const quota = showWeek ? week : five;
  const label = showWeek ? 'Неделя' : '5 ч';
  const caption = label + ' ' + quotaPercent(quota) + ' · ' + resetLabel(quota);
  element.dataset.signal = quotaSignal(quota);
  $('.compact', element).textContent = caption;
  $('.island-title', element).textContent = state.demo ? 'Лимиты Codex · Демо'
    : state.snapshot?.FromSessionLog ? 'Лимиты Codex · Из журнала'
    : state.stale ? 'Лимиты Codex · Предыдущие данные' : 'Лимиты Codex';
  const islandSettings = { ...settings, ShowRemaining: true };
  for (const row of element.querySelectorAll('[data-quota]')) renderQuota(row, state.snapshot?.[row.dataset.quota], islandSettings);
}

function applyUpdate(next) {
  model = next;
  const { state, settings } = next;
  if (view === 'island') {
    updateIslandView(next);
    return;
  }
  styleTheme(document.documentElement, settings);
  if (view === 'details') renderDetails();
  if (view === 'history') {
    updateRanges();
    readHistoryIfChanged();
  }
}

function updateIslandView(next) {
  const island = root.firstElementChild;
  const opening = next.settingsOpen && (!settingsPanel || settingsPanel.hidden);
  if (opening && !settingsPanel) {
    root.append($('#settings-template').content.cloneNode(true));
    settingsPanel = $('#island-settings');
    setupSettings();
    $('#close-settings').onclick = closeIslandSettings;
  }

  if (next.settingsOpen) {
    if (opening) {
      const { width, height } = island.getBoundingClientRect();
      settingsPanel.style.setProperty('--opening-width', width + 'px');
      settingsPanel.style.setProperty('--opening-height', height + 'px');
    }
    island.hidden = true;
    document.body.classList.add('settings-view');
    document.documentElement.classList.toggle('reduce-glass', next.reduceTransparency);
    settingsPanel.hidden = false;
    if (opening) {
      populateSettings(next);
      $('#close-settings').focus({ preventScroll: true });
    } else {
      updateGlassPreview($('.preview .island'));
    }
    return;
  }

  if (settingsPanel && !settingsPanel.hidden) {
    settingsPanel.hidden = true;
    document.body.classList.remove('settings-view');
    document.documentElement.classList.remove('reduce-glass');
    delete document.documentElement.dataset.theme;
    document.documentElement.style.removeProperty('--accent');
    document.documentElement.style.removeProperty('--bar-thickness');
  }
  island.hidden = false;
  island.classList.toggle('reduce-glass', next.reduceTransparency);
  renderIsland(island, next.state, next.settings, next.hovering);
}

function closeIslandSettings() {
  if (!$('#close-settings').disabled) window.widget.closeSettings();
}

function renderForecasts(data) {
  for (const element of root.querySelectorAll('[data-forecast]')) {
    element.hidden = !data?.enabled;
    if (!data?.enabled) continue;
    const key = element.dataset.forecast;
    const lines = [data[key], data[key + 'Detail'], data[key + 'Pattern']];
    if (key === 'week') lines.push(data.weekDaily, data.weekAverage);
    if (view === 'details') lines.push('Расход за 24 ч · ' + number.format(data[key + 'Spent']) + ' п. п.');
    element.replaceChildren(...lines.map(line => node('p', line)));
  }
}

function renderDetails() {
  const { state, settings } = model;
  const snapshot = state.snapshot;
  const source = state.demo ? 'Демонстрационные данные' : snapshot?.FromSessionLog ? 'Из локального журнала'
    : state.stale ? 'Предыдущие данные' : 'Codex CLI';
  $('#reading').textContent = snapshot ? source + ' · ' + moscow.format(snapshot.fetchedAt * 1000) + ' МСК' : 'Данных пока нет';
  const status = $('#request-status');
  status.textContent = state.busy ? 'Обновление…' : state.status || '';
  status.hidden = !status.textContent;
  $('#refresh').hidden = Boolean(state.loginRequired);
  $('#refresh').disabled = state.busy || state.demo;
  $('#login').hidden = !state.loginRequired;
  $('#login').disabled = model.loginInProgress;
  for (const row of root.querySelectorAll('[data-quota]')) {
    renderQuota(row, snapshot?.[row.dataset.quota], settings, true);
    row.style.fontSize = (settings.FontSize + 2) + 'px';
    $('.meter', row).style.maxWidth = (settings.BarWidth / 110 * 100) + '%';
  }
  const credits = $('#credits');
  credits.hidden = !settings.ShowReset || snapshot?.ResetCredits == null;
  const expiry = snapshot?.expiry?.Text;
  credits.textContent = 'Кредиты сброса · ' + snapshot?.ResetCredits + (expiry ? ' · ' + expiry : '');
  const key = readingKey();
  if (lastRead !== key) {
    lastRead = key;
    window.widget.details().then(data => {
      if (lastRead === key) renderForecasts(data);
    }).catch(error => {
      status.hidden = false;
      status.textContent = error.message;
    });
  }
}

const settingsTabs = [
  ['appearance', 'Внешний вид', [0, 1]],
  ['alerts', 'Уведомления', [2]],
  ['storage', 'История и запуск', [3, 4]]
];

const groups = [
  ['Оформление', [
    ['Theme', 'Тема', ['Светлая', 'Тёмная', 'Liquid Glass']],
    ['Accent', 'Цвет акцента', 'accent']
  ]],
  ['Отображение данных', [
    ['FontSize', 'Размер текста', [11, 14]],
    ['BarWidth', 'Длина полосы', [50, 110]],
    ['BarThickness', 'Толщина полосы', [3, 7]],
    ['ShowReset', 'Показывать кредиты сброса', 'check'],
    ['ShowUsage', 'Показывать полосы лимитов', 'check'],
    ['ShowRemaining', 'Показывать время до сброса в подробностях', 'check']
  ]],
  ['Уведомления о лимитах', [
    ['FiveHourLowAlert', 'Мало осталось от 5-часового лимита', 'check'],
    ['FiveHourLowPercent', 'Порог 5-часового лимита, %', [1, 100]],
    ['WeeklyLowAlert', 'Мало осталось от недельного лимита', 'check'],
    ['WeeklyLowPercent', 'Порог недельного лимита, %', [1, 100]],
    ['FiveHourResetAlert', 'Напоминать о сбросе 5-часового лимита', 'check'],
    ['FiveHourResetMinutes', 'За сколько минут предупредить', [1, 300]],
    ['WeeklyResetAlert', 'Напоминать о сбросе недельного лимита', 'check'],
    ['WeeklyResetMode', 'Время недельного напоминания', ['По дню и времени (KST)', 'До сброса']],
    ['WeeklyReminderDays', 'За сколько дней предупредить', [0, 7]],
    ['WeeklyReminderClock', 'Время напоминания (KST)', 'time'],
    ['WeeklyResetMinutes', 'За сколько минут предупредить', [1, 10080]]
  ]],
  ['История и обновление', [
    ['EfficientPolling', 'Адаптировать частоту обновления', 'check'],
    ['HistoryEnabled', 'Сохранять историю на этом ПК', 'check'],
    ['HistoryDays', 'Срок хранения', { 30: '30 дней', 90: '90 дней', 180: '180 дней', 365: '365 дней' }],
    ['ForecastDays', 'Период для прогноза', { 1: '1 день', 3: '3 дня', 7: '7 дней', 14: '14 дней' }]
  ]],
  ['Запуск', [
    ['Startup', 'Запускать при входе в Windows', 'check']
  ]]
];

function options(select, entries) {
  select.replaceChildren(...entries.map(([value, label]) => {
    const option = node('option', label);
    option.value = value;
    return option;
  }));
}

function field(name, label, type) {
  const row = node('label', label);
  row.className = 'field';
  let control;
  if (type === 'accent') {
    const controls = node('span');
    controls.className = 'accent-controls';
    control = node('input');
    control.type = 'text';
    control.placeholder = '#RRGGBB';
    control.pattern = '#?[0-9a-fA-F]{6}';
    control.maxLength = 7;
    const picker = node('input');
    picker.type = 'color';
    picker.setAttribute('aria-label', 'Выбрать цвет акцента');
    picker.oninput = () => setAccent(picker.value);
    picker.id = 'accent-picker';
    controls.append(control, picker);
    row.append(controls);
  } else if (Array.isArray(type) && typeof type[0] === 'number') {
    control = node('input');
    control.type = 'number';
    [control.min, control.max] = type;
    control.required = true;
    row.append(control);
  } else if (typeof type === 'object') {
    control = node('select');
    options(control, Object.entries(type));
    row.append(control);
  } else {
    control = node('input');
    control.type = type === 'check' ? 'checkbox' : type;
    if (type === 'check') control.setAttribute('role', 'switch');
    control.required = type === 'time';
    row.append(control);
  }
  control.name = name;
  return row;
}

function setAccent(value) {
  settingsForm.elements.Accent.value = value;
  preview();
}

function setupSettings() {
  settingsForm = $('#preferences');
  for (const [index, [title, fields]] of groups.entries()) {
    const section = node('fieldset');
    section.dataset.group = index;
    section.append(node('legend', title), ...fields.map(args => field(...args)));
    if (title === 'Оформление') {
      const palette = node('div');
      palette.className = 'accent-palette';
      const swatchRow = node('div');
      swatchRow.className = 'swatches';
      for (const color of swatches) {
        const button = node('button');
        button.type = 'button';
        button.className = 'swatch';
        button.style.setProperty('--swatch', color);
        button.dataset.color = color;
        button.setAttribute('aria-label', color);
        button.onclick = () => setAccent(color);
        swatchRow.append(button);
      }
      const reset = node('button', 'Сбросить цвет');
      reset.type = 'button';
      reset.onclick = () => setAccent('');
      const stage = node('div');
      stage.className = 'preview-stage';
      const preview = node('div');
      preview.className = 'preview';
      preview.setAttribute('aria-label', 'Предпросмотр оформления');
      preview.append($('#island-template').content.cloneNode(true));
      const previewButton = $('.island-action', preview);
      const sample = node('span');
      sample.className = previewButton.className;
      sample.append(...previewButton.childNodes);
      previewButton.replaceWith(sample);
      $('.island-refresh', preview).remove();
      $('.island-gear', preview).remove();
      const caption = node('p', 'Предпросмотр');
      caption.className = 'preview-caption';
      stage.append(preview, caption);
      palette.append(swatchRow, reset);
      section.append(palette, stage);
    }
    if (title === 'История и обновление') {
      const clear = node('button', 'Удалить историю');
      clear.type = 'button';
      clear.className = 'danger';
      clear.onclick = async () => {
        try {
          if (await window.widget.clear()) $('#save-status').value = 'История удалена';
        } catch (error) {
          $('#save-status').value = error.message;
        }
      };
      section.append(clear);
    }
    $('.settings-sections').append(section);
  }
  setupSettingsTabs();
  settingsForm.oninput = preview;
  settingsForm.onsubmit = saveSettings;
  settingsForm.addEventListener('invalid', event => {
    const fieldset = event.target.closest('fieldset');
    const tab = fieldset?.parentElement?.getAttribute('aria-labelledby');
    if (tab) document.getElementById(tab).click();
  }, true);
}

function setupSettingsTabs() {
  const register = $('.settings-tabs');
  const sections = $('.settings-sections');
  const buttons = settingsTabs.map(([key, title, indices]) => {
    const panel = node('div');
    panel.id = 'settings-' + key;
    panel.setAttribute('role', 'tabpanel');
    panel.setAttribute('aria-labelledby', 'tab-' + key);
    for (const index of indices) panel.append(sections.querySelector('[data-group="' + index + '"]'));
    sections.append(panel);

    const button = node('button', title);
    button.type = 'button';
    button.id = 'tab-' + key;
    button.setAttribute('role', 'tab');
    button.setAttribute('aria-controls', panel.id);
    register.append(button);
    return button;
  });

  function activate(selected) {
    for (const button of buttons) {
      const active = button === selected;
      button.setAttribute('aria-selected', active);
      button.tabIndex = active ? 0 : -1;
      document.getElementById(button.getAttribute('aria-controls')).hidden = !active;
    }
    sections.scrollTop = 0;
    updateGlassPreview($('.preview .island'));
  }

  for (const button of buttons) button.onclick = () => activate(button);
  register.onkeydown = event => {
    const index = buttons.indexOf(event.target);
    if (index < 0) return;
    let target;
    if (event.key === 'Home') target = buttons[0];
    else if (event.key === 'End') target = buttons.at(-1);
    else if (event.key === 'ArrowLeft') target = buttons[(index + buttons.length - 1) % buttons.length];
    else if (event.key === 'ArrowRight') target = buttons[(index + 1) % buttons.length];
    if (target) {
      event.preventDefault();
      activate(target);
      target.focus();
    }
  };
  activate(buttons[0]);
}

function populateSettings({ settings, startup, canSetStartup }) {
  savedSettings = settings;
  const controls = settingsForm.elements;
  for (const [, fields] of groups) {
    for (const [key, , type] of fields) {
      if (!Object.hasOwn(settings, key)) continue;
      if (type === 'check') controls[key].checked = settings[key];
      else controls[key].value = settings[key];
    }
  }
  if (!settings.LowQuotaAlert) controls.FiveHourLowAlert.checked = controls.WeeklyLowAlert.checked = false;
  controls.Theme.value = ['light', 'dark', 'glass'].indexOf(theme(settings));
  controls.Accent.value = accent(settings.AccentRgb) || '';
  controls.WeeklyReminderClock.value = [settings.WeeklyReminderHour, settings.WeeklyReminderMinute]
    .map(part => String(part).padStart(2, '0')).join(':');
  controls.Startup.checked = startup;
  controls.Startup.closest('fieldset').hidden = !canSetStartup;
  preview();
  $('#save-status').value = '';
}

function readSettings() {
  const fields = settingsForm.elements;
  const patch = {};
  for (const [, group] of groups) {
    for (const [key, , type] of group) {
      if (!Object.hasOwn(savedSettings, key)) continue;
      patch[key] = type === 'check' ? fields[key].checked : Number(fields[key].value);
    }
  }
  patch.DarkTheme = fields.Theme.value !== '0';
  patch.GlassIsland = fields.Theme.value === '2';
  const color = fields.Accent.value.trim();
  patch.AccentRgb = /^[#]?[\da-f]{6}$/i.test(color) ? parseInt(color.replace('#', ''), 16) : null;
  patch.LowQuotaAlert = patch.FiveHourLowAlert || patch.WeeklyLowAlert;
  [patch.WeeklyReminderHour, patch.WeeklyReminderMinute] = fields.WeeklyReminderClock.value.split(':').map(Number);
  return patch;
}

function preview() {
  if (!savedSettings) return;
  const settings = readSettings();
  styleTheme(document.documentElement, { ...settings, AccentRgb: null });
  const now = Math.floor(Date.now() / 1000);
  const snapshot = {
    FiveHour: { Remaining: 72, PercentText: '72%', ResetsAt: now + 7200 },
    Weekly: { Remaining: 48, PercentText: '48%', ResetsAt: now + 259200 }
  };
  const sample = $('.preview .island');
  renderIsland(sample, { snapshot }, settings, true);
  updateGlassPreview(sample);
  for (const choice of settingsForm.querySelectorAll('.swatch')) {
    choice.setAttribute('aria-pressed', choice.dataset.color === accent(settings.AccentRgb));
  }
  $('#save-status').value = 'Изменения не сохранены';
  $('#accent-picker').value = accent(settings.AccentRgb) || swatches[0];
  const controls = settingsForm.elements;
  controls.FiveHourLowPercent.disabled = !settings.FiveHourLowAlert;
  controls.WeeklyLowPercent.disabled = !settings.WeeklyLowAlert;
  controls.FiveHourResetMinutes.disabled = !settings.FiveHourResetAlert;
  controls.WeeklyResetMode.disabled = !settings.WeeklyResetAlert;
  controls.WeeklyReminderDays.disabled = controls.WeeklyReminderClock.disabled = !settings.WeeklyResetAlert || settings.WeeklyResetMode === 1;
  controls.WeeklyResetMinutes.disabled = !settings.WeeklyResetAlert || settings.WeeklyResetMode !== 1;
}

async function saveSettings(event) {
  event.preventDefault();
  const output = $('#save-status');
  const button = $('button[type="submit"]', settingsForm);
  const patch = readSettings();
  if (!patch.ShowUsage && !patch.ShowRemaining && !patch.ShowReset) {
    output.value = 'Выберите хотя бы один элемент для отображения.';
    return;
  }
  if (patch.HistoryDays < savedSettings.HistoryDays &&
      !window.confirm('Записи старше выбранного срока будут удалены. Сохранить?')) return;
  const startupRequested = settingsForm.elements.Startup.checked;
  button.disabled = $('#close-settings').disabled = true;
  try {
    const settings = await window.widget.save(patch);
    if (model.settingsOpen && model.canSetStartup && startupRequested !== model.startup) {
      await window.widget.startup(startupRequested);
    }
    if (model.settingsOpen) {
      populateSettings({ ...model, settings });
      output.value = 'Настройки сохранены';
    }
  } catch (error) {
    output.value = error.message;
  } finally {
    button.disabled = $('#close-settings').disabled = false;
  }
}

function readingKey() {
  return [model.state.snapshot?.fetchedAt, model.state.stale, model.settings.HistoryEnabled, model.settings.HistoryDays,
    model.settings.ForecastDays, model.settings.AccentRgb, theme(model.settings), Math.floor(Date.now() / 60_000)].join('|');
}

function updateRanges() {
  const range = $('#range');
  const days = model.settings.HistoryDays;
  if (range.dataset.retention === String(days)) return;
  range.dataset.retention = days;
  options(range, rangeValues.filter(value => value <= days).map(value => [value, value === 1 ? '24 часа' : value + ' дн.']));
  range.value = model.settings.HistoryRangeDays;
}

async function readHistoryIfChanged(force = false) {
  const days = Number($('#range').value || 1);
  const key = readingKey() + '|' + days;
  if (!force && key === lastRead) return;
  lastRead = key;
  const id = ++readId;
  try {
    const result = await window.widget.history(days);
    if (id !== readId) return;
    historyResult = result;
    $('#sample-status').textContent = result.StorageFailed ? 'Ошибка доступа к истории.'
      : result.samples.length === 0 ? 'За выбранный период записей нет.'
      : model.settings.HistoryEnabled ? 'На графиках показан остаток лимитов · время МСК'
      : 'Запись истории выключена · показаны сохранённые данные';
    for (const kind of ['five', 'week']) {
      $('#' + kind + '-total').textContent = 'Расход · ' + number.format(result[kind + 'Spent']) + ' п. п.';
    }
    renderForecasts(result.details);
    renderCharts();
  } catch (error) {
    if (id === readId) $('#sample-status').textContent = error.message;
  }
}

function renderCharts() {
  if (!historyResult) return;
  drawChart($('#five-chart'), 'Five');
  drawChart($('#week-chart'), 'Week');
}

function drawChart(canvas, key) {
  const { begin, end, samples } = historyResult;
  const dpr = window.devicePixelRatio;
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  canvas.width = Math.round(width * dpr);
  canvas.height = Math.round(height * dpr);
  const ctx = canvas.getContext('2d');
  ctx.scale(dpr, dpr);
  const css = getComputedStyle(document.documentElement);
  ctx.font = '11px Segoe UI';
  ctx.fillStyle = css.getPropertyValue('--muted').trim();
  ctx.strokeStyle = css.getPropertyValue('--line').trim();
  const left = 36, right = width - 14, top = 10, bottom = height - 28;
  for (const value of [0, 25, 50, 75, 100]) {
    const y = bottom - (bottom - top) * value / 100;
    ctx.fillText(value + '%', 0, y + 4);
    ctx.beginPath();
    ctx.moveTo(left, y);
    ctx.lineTo(right, y);
    ctx.stroke();
  }
  const date = end - begin <= 86401 ? chartTime : chartDay;
  for (let tick = 0; tick <= 4; tick++) {
    const label = date.format((begin + (end - begin) * tick / 4) * 1000);
    const offset = ctx.measureText(label).width / 2;
    ctx.fillText(label, Math.min(width - offset * 2, left + (right - left) * tick / 4 - offset), height - 6);
  }
  const color = css.getPropertyValue('--accent').trim();
  ctx.strokeStyle = color;
  ctx.fillStyle = color;
  ctx.lineWidth = 1.6;
  ctx.beginPath();
  let previous;
  const points = [];
  for (const sample of samples) {
    const value = sample[key];
    if (value == null) {
      previous = undefined;
      continue;
    }
    const x = left + (right - left) * (sample.At - begin) / (end - begin);
    const y = bottom - (bottom - top) * value / 100;
    if (previous && sample.At - previous.At <= 600) ctx.lineTo(x, y);
    else {
      ctx.moveTo(x, y);
      points.push([x, y]);
    }
    previous = sample;
  }
  ctx.stroke();
  for (const [x, y] of points) ctx.fillRect(x - 1, y - 1, 2, 2);
}

async function boot() {
  const initial = await window.widget.initial();
  if (view === 'island') {
    const island = $('.island');
    const rim = $('.glass-rim', island);
    window.widget.onGlass(frame => {
      rim.hidden = !frame;
      if (frame) rim.src = 'data:image/png;base64,' + frame.image;
      else rim.removeAttribute('src');
      island.classList.toggle('sampled-glass', Boolean(frame));
      island.classList.toggle('bright-glass', Boolean(frame?.bright));
    });
    $('.island-action', island).onclick = () => window.widget.activateIsland();
    $('.island-refresh', island).onclick = () => window.widget.refresh();
    $('.island-gear', island).onclick = () => window.widget.open('settings');
    root.oncontextmenu = event => {
      if (model.settingsOpen) return;
      event.preventDefault();
      window.widget.menu();
    };
    window.addEventListener('keydown', event => {
      if (event.key !== 'Escape' || event.defaultPrevented || !model.settingsOpen || $('select:open', settingsPanel)) return;
      event.preventDefault();
      closeIslandSettings();
    });
  } else if (view === 'details') {
    $('#refresh').onclick = () => window.widget.refresh();
    $('#login').onclick = () => window.widget.login();
    $('#open-history').onclick = () => window.widget.open('history');
  } else {
    $('#range').onchange = async event => {
      const days = Number(event.target.value);
      readHistoryIfChanged();
      try {
        await window.widget.save({ HistoryRangeDays: days });
      } catch (error) {
        $('#sample-status').textContent = error.message;
      }
    };
    window.addEventListener('resize', renderCharts);
    window.widget.onHistoryChanged(() => readHistoryIfChanged(true));
  }
  applyUpdate(initial);
  window.widget.onUpdate(applyUpdate);
}

boot().catch(error => { root.textContent = error.message; });
