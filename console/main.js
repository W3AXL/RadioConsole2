const { app, BrowserWindow, ipcMain } = require('electron/main');

const path = require('path')
const fs = require('fs');

const { SerialPort } = require('serialport');

const configPath = path.resolve(app.getPath("userData") + '/config.json');

// Default config, saved if no config.json exists currently
const defaultConfig = {
    Radios: [],
    Autoconnect: false,
    ClockFormat: "UTC",
    Audio: {
        ButtonSounds: true,
        UnselectedVol: -9.0,
        ToneVolume: -9.0,
        UseAGC: true,
    },
    Extension: {
        address: "127.0.0.1",
        port: 5555
    },
    Peripherals: {
        serialPort: "",
        useCtsForPtt: false
    }
}

// Global window objects
let mainWindow;
let periphWindow;

// Serial port object
let serialPort = null;

/**
 * Reads the config file and returns the JSON inside
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
            console.error("Failed to write default config file " + configPath + "!");
            console.error(e);
        }
    // Read the file if it already exists
    } else {
        console.log("Reading config file from " + configPath);
        const configJson = fs.readFileSync(configPath, { encoding: 'utf8', flag: 'r' });
        return configJson;
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
        log.error("Got error saving config: " + e);
        return e;
    }
}

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

/**
 * Creates the main console window
 */
async function createMainWindow() {
    // Create the window
    win = new BrowserWindow({
        width: 1280, 
        height: 430,
        autoHideMenuBar: true,
        icon: 'console-icon.png',
        webPreferences: {
            preload: path.join(__dirname, "main-preload.js")
        }
    });

    // Handle config read & save
    ipcMain.handle("readConfig", readConfig);
    ipcMain.handle("saveConfig", saveConfig);

    // Handle serial port open/close
    ipcMain.handle('openSerialPort', (event, path) => { openSerialPort(path); });
    ipcMain.handle('closeSerialPort', (event, args) => { closeSerialPort(); });

    // Load & show the main window
    await win.loadFile(path.join(__dirname, "index.html"))
        .then(() => { win.webContents.send('appVersion', app.getVersion()); })
        .then(() => { win.show() });

    return win;
}

/**
 * Creates the peripheral settings window
 */
async function createPeriphWindow(periphConfig)
{
    win = new BrowserWindow({
        width: 512,
        height: 194,
        icon: 'console-icon.png',
        autoHideMenuBar: true,
        webPreferences: {
            preload: path.join(__dirname, "peripherals-preload.js")
        }
    });

    // Query available serial ports
    var serialPorts = await SerialPort.list();
    
    await win.loadFile(path.join(__dirname, "peripherals.html"))
        .then(() => { win.webContents.send('gotPorts', serialPorts); })
        .then(() => { win.webContents.send('showPeriphConfig', periphConfig); });
    
    return win;
}

/**
 * App startup
 */
app.on('ready', async () => {
    mainWindow = await createMainWindow();

    // Handle creating the peripheral config window
    ipcMain.handle('showPeriphConfig', (event, periphConfig) => {
        console.debug("Showing peripheral config window with initial data");
        console.debug(periphConfig);
        periphWindow = createPeriphWindow(periphConfig);
    });

    ipcMain.handle('savePeriphConfig', (event, periphConfig) => {
        // Send the data to our main window
        mainWindow.webContents.send('savePeriphConfig', periphConfig);
    });
});