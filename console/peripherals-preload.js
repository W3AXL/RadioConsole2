const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('electronAPI', {
    // Save/show config
    populatePeriphConfig: (periphConfig) => ipcRenderer.on('populatePeriphConfig', periphConfig),
    savePeriphConfig: (periphConfig) => ipcRenderer.invoke('savePeriphConfig', periphConfig),
    // Get list of ports
    gotPorts: (portList) => ipcRenderer.on('gotPorts', portList),
});