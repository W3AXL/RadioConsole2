const { app, BrowserWindow, ipcMain } = require('electron/main');

const path = require('path')
const fs = require('fs');

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
    }
}

let mainWin

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

async function createWindow() {
    // Create the window
    mainWin = new BrowserWindow({
        width: 1280, 
        height: 430,
        autoHideMenuBar: true,
        icon: 'console-icon.png',
        webPreferences: {
            preload: path.join(__dirname, "preload.js")
        }
    });

    ipcMain.handle("readConfig", readConfig);
    ipcMain.handle("saveConfig", saveConfig);

    mainWin.loadFile(path.join(__dirname, "index.html"))
        .then(() => { mainWin.webContents.send('appVersion', app.getVersion()); })
        .then(() => { mainWin.show() });
}

app.on('ready', () => {
    createWindow();
});