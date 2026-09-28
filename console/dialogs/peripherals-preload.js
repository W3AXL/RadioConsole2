const { contextBridge, ipcRenderer } = require('electron/renderer')

contextBridge.exposeInMainWorld('electronAPI', {
    // Save/show config
    populatePeriphConfig: (periphConfig) => ipcRenderer.on('populatePeriphConfig', periphConfig),
    savePeriphConfig: (periphConfig) => ipcRenderer.send('savePeriphConfig', periphConfig),
    // Get list of serial ports
    gotSerialPorts: (serialPortList) => ipcRenderer.on('gotSerialPorts', serialPortList),
    // Get list of MIDI ports
    gotMidiPorts: (midiPortList) => ipcRenderer.on('gotMidiPorts', midiPortList),
    // Handle midi message (only used for learning)
    gotMidiMessage: (message) => ipcRenderer.on('gotMidiMessage', message),
});