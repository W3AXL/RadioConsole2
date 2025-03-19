const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('electronAPI', {
    // Save/show config
    showPeriphConfig: (periphConfig) => ipcRenderer.on('showPeriphConfig', periphConfig),
    savePeriphConfig: (periphConfig) => ipcRenderer.invoke('savePeriphConfig', periphConfig),
    // Get list of ports
    gotPorts: (portList) => ipcRenderer.on('gotPorts', portList),
});