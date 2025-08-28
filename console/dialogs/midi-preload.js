const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('electronAPI', {
    // Save/show config
    populateMidiConfig: (midiConfig) => ipcRenderer.on('populateMidiConfig', midiConfig),
    saveMidiConfig: (midiConfig) => ipcRenderer.invoke('saveMidiConfig', midiConfig),
    // Get list of midi ports
    gotPorts: (portList) => ipcRenderer.on('gotPorts', portList),
    // Handle midi message (only used for learning)
    gotMidiMessage: (message) => ipcRenderer.on('gotMidiMessage', message),
});