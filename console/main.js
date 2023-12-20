const { create } = require('domain')
const electron = require('electron')
const app = electron.app
const BrowserWindow = electron.BrowserWindow

const path = require('path')
const url = require('url')

let win

function createWindow() {
    // Create the window
    win = new BrowserWindow({
        width: 1280, 
        height: 430,
        autoHideMenuBar: true,
        icon: 'console-icon@4x.png'
    })

    // Load the page
    win.loadURL(url.format({
        pathname: path.join(__dirname, "index.html"),
        protocol: "file",
        slashes: true
    }))

    // Open DevTools
    win.webContents.openDevTools()
}

app.on('ready', createWindow)