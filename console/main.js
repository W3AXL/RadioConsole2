const { app, BrowserWindow, ipcMain, dialog } = require('electron/main');

const path = require('path')
const fs = require('fs');

const { SerialPort } = require('serialport');
const midi = require('@julusian/midi');

const configPath = path.resolve(app.getPath("userData") + '/config.json');

const { defaultConfig } = require('./lib/DefaultConfig.js');

// Global window objects
var mainWindow = null;
var periphWindow = null;
var midiWindow = null;
var editRadioWindow = null;

// Serial port object
let serialPort = null;

// Midi object
let midiInput = new midi.Input();

/***********************************************************************************
    Config Management Functions
***********************************************************************************/

/**
 * Reads the config file and returns the JSON inside, or null if the config could not be read
 */
async function readConfig() {
    // Check for existing config file
    if (!fs.existsSync(configPath)) {
        console.warn("No config.json file found, creating default at " + configPath);
        try {
            fs.writeFileSync(configPath, JSON.stringify(defaultConfig, null, 4));
            return defaultConfig;
        }
        catch (e) {
            dialog.showErrorBox(`Failed to write default config file ${configPath}: ${e}`);
            console.error(e);
        }
    // Read the file if it already exists
    } else {
        console.log("Reading config file from " + configPath);
        const configJson = fs.readFileSync(configPath, { encoding: 'utf8', flag: 'r' });
        // Try to parse
        try {
            config = JSON.parse(configJson);
            console.debug("Got config");
            console.debug(config);
            return config;
        }
        catch (e) {
            dialog.showErrorBox(`Failed to parse config JSON ${configJson}: ${e}`);
            return null;
        }
    }
}

/**
 * Saves config JSON to the config path
 */
async function saveConfig(event, args) {
    console.log("Writing config file to " + configPath);
    try {
        fs.writeFileSync(configPath, args);
        return true;
    }
    catch (e) {
        console.error("Got error saving config: " + e);
        return e;
    }
}

/***********************************************************************************
    Serial Port Functions
***********************************************************************************/

/**
 * Opens the serial port for CTS PTT
 * @param {string} path path for serial port
 */
function openSerialPort(path)
{
    // Ignore if already open
    if (serialPort != null)
    {
        return;
    }
    // Open
    console.info(`Opening serial port ${path} for CTS PTT`)
    serialPort = new SerialPort({
        path: path,
        baudRate: 9600,
        rtscts: true,
    });
    // Start callback
    setTimeout(() => { 
        serialPortCallback();
    }, 100);
}

/**
 * Closes the serial port, if open
 */
function closeSerialPort()
{
    if (serialPort != null) {
        console.info(`Closing serial port ${serialPort.path}`);
        serialPort.close();
        serialPort = null;
    }
}

/**
 * Called every 100ms to read the status of CTS for PTT
 */
async function serialPortCallback()
{
    // Only do things if the port is open
    if (serialPort != null)
    {
        // Read control lines
        serialPort.get((event, status) => {
            // Send
            mainWindow.webContents.send('serialPortStatus', status);
            // Call again
            setTimeout(() => { 
                serialPortCallback();
            }, 100);
        });
    }
}

/***********************************************************************************
    MIDI functions
***********************************************************************************/

// Midi Message Types
const midiMsgTypes = {
    NOTE_ON:        0x8,
    NOTE_OFF:       0x9,
    POLY_AFTER:     0xA,
    CTRL_CHANGE:    0xB,
    PGM_CHANGE:     0xC,
    CHAN_AFTER:     0xD,
    PITCH_WHEEL:    0xE,
}

function getMidiPorts()
{
    const portCount = midiInput.getPortCount();
    if (portCount > 0)
    {
        console.info(`Found ${portCount} midi ports:`)
        let ports = []
        for (let i = 0; i < portCount; i++)
        {
            port = {
                index: i,
                name: midiInput.getPortName(i)
            }
            ports.push(port);
            console.info(`  - port ${port.index}: ${port.name}`);
        }
        return ports;
    }
    else
    {
        console.warn("No midi ports found!");
        return null;
    }
}

function openMidiPort(port)
{
    // Ignore if none selected
    if (port < 0)
    {
        console.warn("No MIDI port selected");
        return;
    }
    // Ignore if there are no ports
    if (midiInput.getPortCount() <= 0)
    {
        console.warn("No MIDI ports found");
        return;
    }
    // Ignore if already open
    if (midiInput.isPortOpen())
    {
        return;
    }
    // Log
    console.debug(`Midi enabled, opening port ${port} (${midiInput.getPortName(port)})`);
    // Open
    try {
        midiInput.openPort(port);
    }
    catch (error)
    {
        console.error(`Encountered error while opening MIDI port:`);
        console.error(error);
    }
}

function closeMidiPort()
{
    if (midiInput && midiInput.isPortOpen()) {
        midiInput.closePort();
        midiInput = null;
    }
}

function midiMessageHandler(deltaTime, message)
{
    // Decode
    const msgType = (message[0] & 0b11110000) >> 4;
    const msgChan = (message[0] & 0b00001111);
    // Ignore certain messages
    if (msgType == midiMsgTypes.POLY_AFTER || msgType == midiMsgTypes.PGM_CHANGE || msgType == midiMsgTypes.CHAN_AFTER || msgType == midiMsgTypes.PITCH_WHEEL)
    {
        return;
    }
    // Package
    const msg = {
        type: msgType,
        chan: msgChan,
        num: message[1],
        data: message[2]
    }
    // Send to main app (for processing)
    if (mainWindow != null)
    {   
        mainWindow.webContents.send('gotMidiMessage', msg);
    }
    // Send to midi config window (for learning)
    if (periphWindow != null)
    {
        periphWindow.webContents.send('gotMidiMessage', msg);
    }
}

/***********************************************************************************
    Window Creation Functions
***********************************************************************************/

/**
 * Creates the main console window
 */
async function createMainWindow() {
    // Create the window
    mainWindow = new BrowserWindow({
        width: 1280, 
        height: 430,
        autoHideMenuBar: true,
        icon: 'console-icon.png',
        webPreferences: {
            preload: path.join(__dirname, "main-preload.js")
        },
    });

    // Handle config read & save
    ipcMain.handle("readConfig", readConfig);
    ipcMain.handle("saveConfig", saveConfig);

    // Handle serial port open/close
    ipcMain.handle('openSerialPort', (event, path) => { openSerialPort(path); });
    ipcMain.handle('closeSerialPort', (event, args) => { closeSerialPort(); });

    // Handle window closing
    mainWindow.on('closed', () => {
        closeSerialPort();
        closeMidiPort();
        mainWindow = null;
    })

    // Load & show the main window
    await mainWindow.loadFile(path.join(__dirname, "main-window.html"))
        .then(() => { mainWindow.webContents.send('appVersion', app.getVersion()); })
        .then(() => { mainWindow.show() });
}

/**
 * Creates the peripheral settings window
 */
async function createPeriphWindow(periphConfig)
{
    periphWindow = new BrowserWindow({
        width: 512,
        height: 184,
        icon: 'console-icon.png',
        autoHideMenuBar: true,
        webPreferences: {
            preload: path.join(__dirname, "dialogs/peripherals-preload.js")
        },
        resizable: false,
        parent: mainWindow,
        modal: true,
    });

    periphWindow.on('closed', () => {
        periphWindow = null;
    })

    // Query available serial ports
    var serialPorts = await SerialPort.list();
    // Query available midi ports
    var midiPorts = getMidiPorts();
    
    await periphWindow.loadFile(path.join(__dirname, "dialogs/peripherals.html"))
        .then(() => { periphWindow.webContents.send('gotSerialPorts', serialPorts); })
        .then(() => { periphWindow.webContents.send('gotMidiPorts', midiPorts); })
        .then(() => { periphWindow.webContents.send('populatePeriphConfig', periphConfig); });
}

async function createEditRadioWindow(radioConfig)
{
    editRadioWindow = new BrowserWindow({
        width: 512,
        height: 284,
        icon: 'console-icon.png',
        autoHideMenuBar: true,
        webPreferences: {
            preload: path.join(__dirname, "dialogs/edit-radio-preload.js")
        },
        resizable: false,
        parent: mainWindow,
        modal: true,
    });

    editRadioWindow.on('closed', () => {
        editRadioWindow = null;
    });

    await editRadioWindow.loadFile(path.join(__dirname, "dialogs/edit-radio.html"))
        .then(() => { if (radioConfig) { editRadioWindow.webContents.send('populateRadioConfig', radioConfig); } });
}

/***********************************************************************************
    App Runtime Entry Point
***********************************************************************************/

/**
 * App startup
 */
app.on('ready', async () => {
    // Handle creating the peripheral config window
    ipcMain.handle('showPeriphConfig', async (event, periphConfig) => {
        console.debug("Showing peripheral config window with initial data");
        console.debug(periphConfig);
        await createPeriphWindow(periphConfig);
    });

    ipcMain.on('savePeriphConfig', (event, periphConfig) => {
        // Send the data to our main window
        console.debug("Sending new peripheral config to main window");
        mainWindow.webContents.send('savePeriphConfig', periphConfig);
    });

    ipcMain.handle('openMidiPort', (event, port) => {
        openMidiPort(port);
    })

    // Handle Midi Messages
    midiInput.on('message', midiMessageHandler);

    // Handle showing/saving radio dialog
    ipcMain.handle('showRadioConfig', async (event, radioConfig) => {
        await createEditRadioWindow(radioConfig);
    });
    ipcMain.handle('saveRadioConfig', (event, radioConfig) => {
        console.debug('Saving radio config:');
        console.debug(radioConfig);
        mainWindow.webContents.send('saveRadioConfig', radioConfig);
    });
    ipcMain.handle('cancelRadioConfig', () => {
        console.debug('Cancelling radio config');
        mainWindow.webContents.send('cancelRadioConfig');
    })

    // Create the main window
    await createMainWindow();
});