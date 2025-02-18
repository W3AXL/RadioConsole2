const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('electronAPI', {
  readConfig: () => ipcRenderer.invoke('readConfig'),
  saveConfig: (configJson) => ipcRenderer.invoke('saveConfig', configJson),
  getVersion: (listener) => ipcRenderer.on('appVersion', listener),
});