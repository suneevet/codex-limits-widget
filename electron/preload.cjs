'use strict';

const { contextBridge, ipcRenderer } = require('electron');

const invoke = (name, ...args) => ipcRenderer.invoke('widget:' + name, ...args);
contextBridge.exposeInMainWorld('widget', {
  initial: () => invoke('initial'),
  refresh: () => invoke('refresh'),
  activateIsland: () => invoke('activate-island'),
  login: () => invoke('login'),
  menu: () => invoke('menu'),
  open: view => invoke('open', view),
  closeSettings: () => invoke('close-settings'),
  details: () => invoke('details'),
  history: days => invoke('history', days),
  save: patch => invoke('save', patch),
  startup: enabled => invoke('startup', enabled),
  clear: () => invoke('clear'),
  onGlass: callback => { ipcRenderer.on('widget:glass', (_event, frame) => callback(frame)); },
  onUpdate: callback => { ipcRenderer.on('widget:update', (_event, value) => callback(value)); },
  onHistoryChanged: callback => { ipcRenderer.on('widget:history-changed', () => callback()); }
});
