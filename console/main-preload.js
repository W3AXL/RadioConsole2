const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('electronAPI', {
  // Config functions
  readConfig: () => ipcRenderer.invoke('readConfig'),
  saveConfig: (configJson) => ipcRenderer.invoke('saveConfig', configJson),
  // Version string
  getVersion: (version) => ipcRenderer.on('appVersion', version),
  // Peripheral window
  showPeriphConfig: (periphConfig) => ipcRenderer.invoke('showPeriphConfig', periphConfig),
  savePeriphConfig: (periphConfig) => ipcRenderer.on('savePeriphConfig', periphConfig),
  // Serial port open/close
  openSerialPort: (path) => ipcRenderer.invoke('openSerialPort', path),
  closeSerialPort: () => ipcRenderer.invoke('closeSerialPort'),
  // Serial port status
  serialPortStatus: (status) => ipcRenderer.on('serialPortStatus', status),
});