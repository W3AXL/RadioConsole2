const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('electronAPI', {
    // Save/show config
    populateRadioConfig: (radioConfig) => ipcRenderer.on('populateRadioConfig', radioConfig),
    // Add new radio
    saveRadioConfig: (radioConfig) => ipcRenderer.invoke('saveRadioConfig', radioConfig),
    // Cancel edit
    cancelRadioConfig: (data) => ipcRenderer.invoke('cancelRadioConfig', data),
});