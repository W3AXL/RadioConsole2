/***********************************************************************************
    Imports
***********************************************************************************/

// Local file imports
import * as Cfg from "./lib/Config"
import { RequestTracker } from './lib/RequestTracker'
import { handleHello, nowMicros } from "./lib/Protocol"
import { RadioAudioReceiver, MicCaptureManager } from "./lib/AudioPipeline"
import { Envelope, RadioCommandType, RadioStatus, RadioState, SoftkeyName, ScanState, PriorityState } from "./generated/RC2Proto"

/***********************************************************************************
    Global Variables
***********************************************************************************/

// Default Config, overwritten from main.js on page load
var config: Cfg.Configuration = {
    version: Cfg.ConfigVersion,
    radios: [],
    autoConnect: false,
    clockFormat: Cfg.ClockFormat.UTC,
    audio: {
        unselectedVolume: -9.0,
        toneVolume: -9.0,
        buttonSounds: true,
        useAGC: true
    },
    extension: {
        enabled: false,
        address: "127.0.0.1",
        port: 5555
    },
    peripherals: {
        midi: {
            enabled: false,
            port: 0,
            masterPtt: null,
            masterVol: null
        },
        serial: {
            enabled: false,
            port: "",
            pttLine: null
        }
    }
}

/**
 * Storage for radio card HTML elements
 */
interface RadioHtmlElements {
    idBase: string,
    card?: HTMLDivElement,
    txBar?: HTMLDivElement,
    rxBar?: HTMLDivElement
}

/**
 * Interface representing an instantiated radio loaded from config
 * All config parameters are stored in the corresponding config.radios item
 */
interface Radio {
    cfg: Cfg.Radio,
    status?: RadioStatus,
    requests?: RequestTracker,
    handshakeComplete: boolean,
    connection?: WebSocket,
    envSeq: number,
    audioReceiver?: RadioAudioReceiver,
    elements: RadioHtmlElements
}

// Master list of radios, populated after config is read and used for all radio connections
var radios: Radio[] = [];

/**
 * Objects required for the input (mic) audio chain
 */
interface AudioInputChain {
    analyzer: AnalyserNode,
    volume: GainNode,
    meter: HTMLElement
}

/**
 * Objects required for the output (speaker) audio chain
 */
interface AudioOutputChain {
    analyzer: AnalyserNode,
    volume: GainNode,
    meter: HTMLElement
}

/**
 * Interface representing the console's audio objects, settings, and nodes
 */
interface Audio {
    context: AudioContext,
    running: boolean,
    inputChain: AudioInputChain,
    outputChain: AudioOutputChain
}

// Audio variables
var audio = {
    // Audio context
    context: null,
    // Flag for whether we've done initial setup
    running: false,
    // DTMF generator
    dtmf: null,
    dtmfGain: 0.3,
    // Alert tone generator
    tones: null,
    tonesGain: 0.3,
    tonesPeriod: 500,
    // Input device, stream, meter, etc
    input: null,
    inputStream: null,
    inputTrack: null,
    inputAnalyzer: null,
    inputDest: null,
    inputMicGain: null,
    inputPcmData: null,
    inputMeter: document.getElementById("meter-mic"),
    // Output device, analyzer, and gain (for volume control and visualization)
    output: null,
    outputGain: null,
    outputAnalyzer: null,
    outputPcmData: null,
    outputMeter: document.getElementById("meter-spkr"),
    dummyOutputs: [],
    // AGC parameters
    agcThreshold: -50.0,
    agcKnee: 40.0,
    agcRatio: 8.0,
    agcAttack: 0.0,
    agcRelease: 0.3,
    agcMakeup: 1.1,     // right now any makeup gain causes clipping
    // TX/RX audio filter cutoff (hz)
    filterCutoff: 4000,
    // Delay for unmuting microphone after PTT (for ignoring the TPT tone)
    micUnmuteDelay: 450,
    // Delay for muting the mic after PTT is released (to account for PC audio latency)
    micMuteDelay: 100
}

const userMediaSettings = {
    audio: {
        autoGainControl: false,
        channelCount: 1,
        echoCancellation: false,
        latency: 0,
        noiseSuppression: false,
        sampleRate: 48000,
        sampleSize: 16,
        volume: 1.0
    }
}

// DTMF array
const dtmfFrequencies = {
	"1": {f1: 697, f2: 1209},
	"2": {f1: 697, f2: 1336},
	"3": {f1: 697, f2: 1477},
    "A": {f1: 697, f2: 1633},
	"4": {f1: 770, f2: 1209},
	"5": {f1: 770, f2: 1336},
	"6": {f1: 770, f2: 1477},
    "B": {f1: 770, f2: 1633},
	"7": {f1: 852, f2: 1209},
	"8": {f1: 852, f2: 1336},
	"9": {f1: 852, f2: 1477},
    "C": {f1: 852, f2: 1633},
	"*": {f1: 941, f2: 1209},
	"0": {f1: 941, f2: 1336},
	"#": {f1: 941, f2: 1477},
    "D": {f1: 941, f2: 1633}
}

const dtmfTiming = {
    "initialDelay": 250,
    "digitDuration": 200,
    "digitDelay": 100
}

// Midi Message Types
const midiMsgTypes = {
    NOTE_OFF:       0x8,
    NOTE_ON:        0x9,
    POLY_AFTER:     0xA,
    CTRL_CHANGE:    0xB,
    PGM_CHANGE:     0xC,
    CHAN_AFTER:     0xD,
    PITCH_WHEEL:    0xE,
}

// Flag to start/stop animation callback for audio meters (to save CPU when we're not doing anything)
var audio_playing = false;

// Flag to unmute the mic on receipt of a startTx ACK from the radio
var txUnmuteMic = false;

// Whether the alert tones are currently playing
var alertTonesInProgress = false;

// Whether the alert PTT has been overridden by a normal PTT
var alertPttOverride = false;

// Timeout that starts alert tone transmit
var alertStartTimeout = null;

// Timeout that ends TX after console alert delay
var alertStopTimeout = null;

// Stuck button timeout
var stuckButtonTimeout = null;

// Radio Card Tempalte
const radioCardTemplate : HTMLTemplateElement = document.querySelector<HTMLTemplateElement>('#card-template');

// Alert Dialog Template
const alertTemplate = document.querySelector("#alert-dialog-template");

// Extension websocket connection
var extensionWs = null;

// Whether we're currently editing a radio
var editingRadioIdx = -1;

/***********************************************************************************
    State variables
***********************************************************************************/

// Detected timezone
var timeZone = "";
// Selected radio
var selectedRadio = null;
var selectedRadioIdx = null;
// PTT state
var pttActive = false;
// Menu state
var menuOpen = false;
// server disconnect commanded
var disconnecting = false;

// Softkey page
var softkeyPage = 0;
var maxPages = 0;

/***********************************************************************************
    Page Setup Functions
***********************************************************************************/

// Populate version callback
window.electronAPI.getVersion((event, data) => {
    console.debug(`Got version string from main.js: ${data}`);
    $("#navbar-version").html(data);
});

/**
 * Page load function. Starts timers, etc.
 */
function pageLoad() {
    console.log("Starting client runtime");

    // Enable JQuery Tooltips
    //$( document ).tooltip();

    // Query media devices
    getAudioDevices();

    // Query radio config from client.json
    readConfig();

    // Get client timezone
    const d = new Date();
    timeZone = d.toLocaleString('en', { timeZoneName: 'short' }).split(' ').pop();

    // Setup clock timer
    setInterval(updateClock, 100);
}

/**
 * Connect to websocket and setup audio
 */
function connect() {
    // Update navbar
    $("#navbar-status").html("Connecting");
    $("#navbar-status").addClass("pending");
    // Connect websocket first
    connectWebsocket();
    // Start audio devices if they're not already started
    if (!audio.context) {
        startAudioDevices();
    }
}

/**
 * Called when GUI is fully connected to server
 */
function connected() {
    // Change button
    $("#server-connect-btn").html("Disconnect");
    $("#server-connect-btn").prop("disabled",false);
    // Change status
    $("#navbar-status").html("Connected");
    $("#navbar-status").removeClass("pending");
    $("#navbar-status").addClass("connected");
}

function radioConnected(idx) {
    // UI update
    $(`#radio${idx} .icon-connect`).removeClass('disconnected');
    $(`#radio${idx} .icon-connect`).removeClass('connecting');
    $(`#radio${idx} .icon-connect`).addClass('connected');
    $(`#radio${idx} .icon-connect`).parent().prop('title','WebRTC Connected');
    // Update master connect/disconnect button
    $(`#navbar-connect`).removeClass('disconnected');
    $(`#navbar-connect`).addClass('connected');
    // Open serial port, if configured
    if (config.Peripherals.useCtsForPtt)
    {
        window.electronAPI.openSerialPort(config.Peripherals.serialPort);
    }
}

/**
 * Disconnect from websocket and teardown audio devices
 */
function disconnect() {
    // Change button
    $("#server-connect-btn").html("Disconnecting...");
    $("#server-connect-btn").prop("disabled", true);
    // Change status
    $("#navbar-status").html("Disconnecting");
    $("#navbar-status").removeClass("connected");
    $("#navbar-status").addClass("pending");
    // Change status
    disconnecting = true;
    // disconnect websocket
    disconnectWebsocket();
}

/**
 * Called when client is done disconnecting
 */
function disconnected() {
    // Update button
    $("#server-connect-btn").html("Connect");
    $("#server-connect-btn").prop("disabled", false);
    // Change status
    $("#navbar-status").html("Disconnected");
    $("#navbar-status").removeClass("connected");
    $("#navbar-status").removeClass("pending");
    // Clear radio cards
    clearRadios();
    // Disable volume slider
    $("#console-volume").prop('disabled', true);
    // Reset variables
    disconnecting = false;
}

// Keydown handler
$(document).on("keydown", function (e) {
    switch (e.which) {
        // Spacebar
        case 32:
            e.preventDefault();
            // Start PTT if not already TXing
            if (!pttActive) {
                startPtt(true);
            }
            // Handle alert tone override
            else if (pttActive && alertTonesInProgress) {
                startPtt(false);
            }
            break;
    }
});

// Keyup handler
$(document).on("keyup", function (e) {
    switch (e.which) {
        // Spacebar
        case 32:
            e.preventDefault();
            stopPtt();
            break;
    }
});

// Handle losing focus of the window
$(window).blur(function () {
    if (pttActive) {
        console.warn("Killing active PTT due to window focus lost")
        stopPtt();
    }
})

// Bind pageLoad function to document load
$(document).ready(pageLoad());

/***********************************************************************************
    Peripheral Functions
***********************************************************************************/

// We save the CTS state so we only trigger PTT start/stop on state change
var lastCtsState = false;

// Show the peripheral config window
async function showPeriphConfig() {
    // If peripheral config doesn't exist, create it
    if (!config.hasOwnProperty('Peripherals'))
    {
        config.Peripherals = {
            serialPort: "",
            useCtsForPtt: false
        }
    }
    // Show the window
    const result = await window.electronAPI.showPeriphConfig(config.Peripherals);
}

// Peripheral config save
window.electronAPI.savePeriphConfig((event, data) => {
    console.debug("Received new peripheral config");
    console.debug(data);
    config.Peripherals = data.Peripherals;
    saveConfig();
});

// Callback for handling serial port control line status
window.electronAPI.serialPortStatus((event, status) => {
    // Handle PTT if enabled
    if (config.Peripherals.useCtsForPtt) {
        // Ignore if state hasn't changed
        if (status.cts == lastCtsState)
        {
            return;
        }
        // Start if we need to
        if (status.cts && !pttActive)
        {
            console.debug("Serial port CTS triggering PTT");
            startPtt(true);
        }
        // Handle alert tone PTT override case
        else if (status.cts && pttActive && alertTonesInProgress)
        {
            startPtt();
        }
        // Stop if we need to
        else if (!status.cts && pttActive)
        {
            console.debug("Serial port CTS releasing PTT");
            stopPtt();
        }
        // Save this new state
        lastCtsState = status.cts;
    }
})

/***********************************************************************************
    Midi Functions
***********************************************************************************/

async function showMidiConfig() {
    // If midi config doesn't exist, create it
    if (!config.hasOwnProperty('Midi'))
    {
        config.Midi = defaultConfig.Midi;
    }
    // Show the window
    const result = await window.electronAPI.showMidiConfig(config.Midi);
}

// Handler for MIDI messages recieved
window.electronAPI.gotMidiMessage((event, msg) => {
    const midiConfig = config.Midi
    //console.debug('Got midi message:');
    //console.debug(msg);
    // Check master PTT
    if (midiConfig.ccs.masterPtt.chan == msg.chan && midiConfig.ccs.masterPtt.num == msg.num)
    {
        // handle Midi Keyup
        if (msg.type == midiMsgTypes.NOTE_ON && !pttActive)
        {
            console.debug("Starting PTT from MIDI master PTT note on");
            startPtt(true);
        }
        // Handle alert tone override case
        else if (msg.type == midiMsgTypes.NOTE_ON && pttActive && alertTonesInProgress)
        {
            startPtt(false);
        }
        // Handle MIDI dekey
        else if (msg.type == midiMsgTypes.NOTE_OFF && pttActive)
        {
            console.debug("Stopping PTT from MIDI master PTT note off");
            stopPtt();
        }
    }
    // Check master volume
    else if (midiConfig.ccs.masterVol.chan == msg.chan && midiConfig.ccs.masterVol.num == msg.num)
    {
        if (msg.type == midiMsgTypes.CTRL_CHANGE)
        {
            // Get new volume scaled
            const newVolume = (msg.data / 127.0).toFixed(2);
            // Set
            setVolume(newVolume);
        }
    }
});

// Midi config save
window.electronAPI.saveMidiConfig((event, data) => {
    console.debug("Received new midi config");
    console.debug(data);
    config.Midi = data.Midi;
    saveConfig();
});

/***********************************************************************************
    Radio UI Functions
***********************************************************************************/

/**
 * Select a radio
 * @param {string} id the id of the radio to select
 */
function selectRadio(id) {
    // Check that the id is valid
    if (!$(`#${id}`).length) {
        console.warn(`Tried to select invalid radio id ${id}`);
        return;
    }
    // Check that the radio is connected before we select it
    if (radios[getRadioIndex(id)].status.State == "Disconnected") { return; }
    // Log
    console.debug("Selecting radio " + id);
    // If the radio was already selected, deselect it
    if (selectedRadio == id) {
        // Deselect all radio cards
        deselectRadios();
        // Remove the selected class
        $(`#${id}`).removeClass("selected");
        // Disable the radio controls
        updateRadioControls();
        // Update the variables
        selectedRadio = null;
        selectedRadioIdx = null;
        // Update stream volumes
        updateRadioAudio();
    } else {
        // Deselect all radio cards
        deselectRadios();
        // Select the new radio card
        $(`#${id}`).addClass("selected");
        // Update the variable
        selectedRadio = id;
        selectedRadioIdx = getRadioIndex(id);
        // Update controls
        updateRadioControls();
        updateRadioAudio();
    }
    // Update the extension
    exUpdateSelected();
}

/**
 * Deselect all radios
 */
function deselectRadios() {
    // Stop PTT if we're transmitting
    stopPtt();
    // Remove selected class from all radio cards
    $(".radio-card").removeClass("selected");
    // Set selected radio to null
    selectedRadio = null;
    selectedRadioIdx = null;
    // Update controls
    updateRadioControls();
}

/**
 * Populate radio cards based on the radios in radios[] and bind their buttons
 */
function populateRadios() {
    console.debug("Populating radio cards from initial config");
    // Add a card for each radio in the list
    radios.forEach((radio, index) => {
        console.info("Adding radio " + radio.name);
        console.debug(radio);
        // Add the radio card
        addRadioCard(index);
        // Update edit list
        addRadioToEditTable(index);
        // Populate its text
        updateRadioCard(index);
    });
}

/**
 * Clear radio cards and remove all radios from radioList
 */
function clearRadios() {
    // deselect any selected radios
    deselectRadios();
    // Clear main layout
    $("#main-layout").empty();
    // Clear radio list
    radios = [];
}

/**
 * Add a radio card with the specified id and name
 * @param {idx} index of the radio in the radios[] list
 */
function addRadioCard(idx: number) {
    // Get radio from list
    const radio = radios[idx];

    // Log
    console.debug(`Adding card for radio ${idx} (${radio.cfg.name}), html id ${radio.elements.idBase}`);

    // Clone the radio card template
    var newCard : DocumentFragment = <DocumentFragment>radioCardTemplate.content.cloneNode(true);

    // Update basic info
    newCard.querySelector(".radio-card").classList.add(radio.cfg.color);
    newCard.querySelector(".radio-card").id = radio.elements.idBase;
    newCard.querySelector(".radio-card .header h2").textContent = radio.cfg.name;

    // Bind click events, etc
    newCard.querySelector(".radio-card").addEventListener('click', function (event) {
        // Prevent continual propagation
        event.stopPropagation();
        event.stopImmediatePropagation();
        // Select the radio
        selectRadio(radio.elements.idBase);
    });

    // Add the card to the main layout
    document.querySelector("#main-layout").append(newCard);

    // Retrieve and store the new element as a javascript object in the radio array
    radios[idx].elements.card = document.querySelector(`#${radio.elements.idBase}`);

    // Store the audio bars as discrete elements, so we can update them in the animation callback without querying for them every time
    radios[idx].elements.rxBar = radios[idx].elements.card.querySelector("#rx-bar");
    radios[idx].elements.txBar = radios[idx].elements.card.querySelector("#tx-bar");
}

/**
 * Add the specified radio to the radio edit table
 * @param idx index of the radio in the radios[] list
 */
function addRadioToEditTable(idx: number) {
    // Get the radio
    const radio = radios[idx];

    // Get nice pretty display value for pan
    let panValue = "C";
    if (radio.cfg.pan != 0)
    {
        const panPercent: number = Math.abs(radio.cfg.pan / 1.0).toFixed(2) * 100;
        if (radio.cfg.pan < 0)
        {
            panValue = `L ${panPercent}%`;
        }
        else
        {
            panValue = `R ${panPercent}%`;
        }
    }

    // Create HTML content
    const tableRowHtml = `
        <td class="radio-table-name">${radio.cfg.name}</td>
        <td class="radio-table-address">${radio.cfg.address}</td>
        <td class="radio-table-port">${radio.cfg.port}</td>
        <td class="radio-table-color">${radio.cfg.color}</td>
        <td class="radio-table-pan">${panValue}</td>
        <td class="radio-table-actions">
            <a href="#" onclick="editRadio(this, '${radio.cfg.name}')" title="Edit"><ion-icon name="create-sharp"></ion-icon></a>
            &nbsp;
            <a href="#" onclick="deleteRadio(this, '${radio.cfg.name}')" title="Delete">
                <ion-icon name='trash-bin-sharp'></ion-icon>
            </a>
        </td>
    `
    const tableRow = document.createElement("tr");
    tableRow.innerHTML = tableRowHtml;

    // Get the edit radios table
    const editRadiosTable : HTMLTableElement = <HTMLTableElement>document.querySelector("#edit-radios-table");

    // If the index is smaller than the length of radios in the table right now, update the radio at that index
    if (idx < editRadiosTable.rows.length) {
        console.debug(`Updating edit radio table entry at index ${idx}`)
        editRadiosTable.rows[idx].replaceWith(tableRow);
    }
    // Otherwise, add the new row to the end
    else {
        console.debug(`Adding new radio to edit radio table`);
        editRadiosTable.appendChild(tableRow);
    }
}

/**
 * Show the radio dialog for a new radio (empty)
 */
function showAddRadioDialog()
{
    window.electronAPI.showRadioConfig(null);
}

/**
 * Show the radio dialog for an existing radio
 * @param {int} editRow 
 * @param {str} name 
 */
function editRadio(editRow, name)
{
    // Find the radio
    const idx = radios.findIndex((radio) => radio.name == name);
    // Verify found
    if (idx < 0)
    {
        alert(`Unable to edit radio ${name}: could not find radio in list`);
        return;
    }
    // Get radio config
    const radioConfig = config.Radios[idx]
    // Flag editing
    editingRadioIdx = idx;
    console.info(`Now editing radio ${radioConfig.name}`);
    console.debug(radioConfig);
    // Show window
    window.electronAPI.showRadioConfig(radioConfig);
}

/**
 * Delete a radio
 * @param {int} editRow row in the table
 * @param {str} name name of the radio
 */
function deleteRadio(editRow, name) {
    // Find the radio
    const idx = radios.findIndex((radio) => radio.name == name);
    // Verify found
    if (idx < 0)
    {
        alert(`Unable to delete radio ${name}: could not find radio in list`);
        return;
    }
    // Log
    console.info(`Removing radio ${name})`)
    console.debug(config.Radios[idx]);
    // Remove from config and radio list
    config.Radios.splice(idx, 1);
    radios.splice(idx, 1);
    // Remove card
    $(`.radio-card:contains("${name}")`).remove();
    // Remove row in radio table
    $(editRow).closest("tr").remove();
    // Save config
    saveConfig();
}

/**
 * Handle radio edit dialog cancel
 */
window.electronAPI.cancelRadioConfig(() => {
    if (editingRadioIdx >= 0)
    {
        console.debug("Clearing edit radio flag, edit cancelled");
        editingRadioIdx = -1;
    }
});

/**
 * New handler for getting new radio configurations from the radio config window
 */
window.electronAPI.saveRadioConfig((event, radioConfig: Cfg.Radio) => {
    // Debug print
    console.debug('Got new radio config from radio edit window!');
    console.debug(radioConfig);

    // Handle edit of an existing radio first
    if (editingRadioIdx >= 0)
    {
        console.info(`Updating radio at index ${editingRadioIdx}`);
        console.debug(radioConfig);
        
        // Store index
        const idx = editingRadioIdx;
        // Clear flag
        editingRadioIdx = -1;
        
        // Update radio config at index
        radios[idx].cfg = radioConfig;

        // Disconnect radio if connected
        if (radios[idx].status.state != RadioState.DISCONNECTED)
        {
            disconnectRadio(idx);
        }

        // Find the table row for this radio and get its index
        let editTableRow = $(`#edit-radios-table tr:contains('${radioConfig.name}')`);
        const editTableIndex = editTableRow.index();

        // Update the row at the index
        addRadioToEditTable(radioConfig, editTableIndex);

        // Update card
        updateRadioCard(idx);
    }
    // Otherwise, we're adding a new radio
    else
    {
        // Validate radio doesn't already exist
        if (radios.some(radio => radio.cfg.name === radioConfig.name))
        {
            alert(`Radio with name ${radioConfig.name} already exists!`);
            return;
        }
        if (radios.some(radio => radio.cfg.address === radioConfig.address) && radios.some(radio => radio.cfg.port === radioConfig.port))
        {
            alert(`Radio at destination ${radioConfig.address}:${radioConfig.port} already exists!`);
            return;
        }

        // Log
        console.log("Adding radio " + radioConfig.name);

        // Get the index for this new radio (will be at the end of the list)
        const newRadioIdx = radios.length;

        // Add radio to the list
        radios.push({
            cfg: radioConfig,
            handshakeComplete: false,
            envSeq: 0,
            elements: {
                idBase: `radio${newRadioIdx}`
            }
        });

        // Save config, which will copy the radio config to the main config object
        saveConfig();
        
        // Add the radio card
        addRadioCard(newRadioIdx);
        
        // Populate its text
        updateRadioCard(newRadioIdx);
        
        // Update edit list
        addRadioToEditTable(newRadio);
    }

    // Save config, which will copy out the radios config objects to the master config
    saveConfig();

    // Clear form
    newRadioClear();
});

function stopClick(event, obj) {
    event.stopPropagation();
    event.preventDefault();
}

function updateRadioCard(idx) {
    // Get radio from radioList
    const radio = radios[idx];
    const radioCard = radio.elements.card;

    // Update radio name in card from status, or fallback to configured name
    radioCard.querySelector(".radio-name").innerHTML = radio.status ? radio.status.name : radio.cfg.name;
    radioCard.querySelector(".radio-name").setAttribute("title", radio.status ? radio.status.description : "");

    // Update color if changed
    if (!radioCard.classList.contains(radio.cfg.color))
    {
        // Get the current list of classes for the card
        const cardClasses = radioCard.classList;
        // Iterate over them until we find a valid color
        cardClasses.forEach((className) => {
            if (Cfg.parseCardColor(className) != undefined)
            {
                const oldColor = className
                console.debug(`Updating radio card color from ${oldColor} to ${radio.cfg.color}`);
                radioCard.classList.remove(oldColor);
                radioCard.classList.add(radio.cfg.color);
            }
        })
    }

    // Update pan from config (which in turn sets the actual audio pan from the onChanged event)
    radioCard.querySelector('.radio-pan').setAttribute('value', radios[idx].cfg.pan.toString());

    // Only update any status-related items if we have a status
    if (!radio.status) {
        console.debug("No radio status received yet");
        return;
    }

    // Limit zone & channel text to 27/18 characters
    // TODO: figure out dynamic scaling of channel/zone text so we don't have to do this
    if (radio.status.zoneName != null) {
        const shortZone = radio.status.zoneName.substring(0,28);
        radioCard.querySelector("#zone-text").innerHTML = shortZone;
    }
    if (radio.status.channelName != null) {
        const shortChan = radio.status.channelName.substring(0,19);
        radioCard.querySelector("#channel-text").innerHTML = shortChan;
    }
    
    // Remove all current status classes
    radioCard.classList.remove("transmitting");
    radioCard.classList.remove("receiving");
    radioCard.classList.remove("encrypted");
    radioCard.classList.remove("disconnected");

    // Update radio state
    switch (radio.status.state) {
        case RadioState.TRANSMITTING:
            radioCard.classList.add("transmitting");
            // Check audio meter state
            checkAudioMeterCallback();
            break;
        case RadioState.RECEIVING:
            radioCard.classList.add("receiving");
            // Check audio meter state
            checkAudioMeterCallback();
            break;
        case RadioState.ENCRYPTED:
            radioCard.classList.add("encrypted");
            checkAudioMeterCallback();
            break;
        case RadioState.DISCONNECTED:
            radioCard.classList.add("disconnected");
            break;
    }

    // Update alert icon
    if (radio.status.errorMsg) {
        radioCard.querySelector("#icon-alert").classList.add("alerting");
    } else {
        radioCard.querySelector("#icon-alert").classList.remove("alerting");
    }

    // Update Scan Icon
    const radioScanIcons = radioCard.querySelector('.scan-icons');
    radioScanIcons.classList.remove("scanning");
    radioScanIcons.classList.remove("priority");
    radioScanIcons.classList.remove("priority2");
    if (radio.status.scanState == ScanState.SCANNING) {
        switch (radio.status.priorityState) {
            case PriorityState.PRIORITY_2:
                radioScanIcons.classList.add("priority2")
                console.debug("Radio scanning, priority 2");
                break;
            case PriorityState.PRIORITY_1:
                radioScanIcons.classList.add("priority")
                console.debug("Radio scanning, priority 1");
                break;
            default:
                radioScanIcons.classList.add("scanning");
                console.debug("Radio scanning, no priority");
                break;
        }
        console.debug("Radio scanning")
    } else {
        console.debug("Radio not scanning");
    }

    // Update secure icon
    radioCard.querySelector('.secure-icon').classList.remove('secure');
    if (radio.status.secure)
    {
        radioCard.querySelector('.secure-icon').classList.add('secure');
    }
}

/**
 * Update the bottom control bar based on the selected radio
 */
function updateRadioControls() {
    var softkeyStates = [false, false, false, false, false, false];
    // Update if we have a selected radio
    if (selectedRadio) {
        // Get the radio from the list
        var radio = radios[selectedRadioIdx];
        // If the radio is disconnected, don't enable the controls
        if (radio.status.State == "Disconnected") { return }
        // Enable softkeys
        $("#radio-controls .btn").removeClass("disabled");
        // Get softkey text based on page index
        var curSoftkeyPage = radio.status.Softkeys.slice(6*softkeyPage, 6+(6*softkeyPage));
        console.debug("Got current softkey page");
        console.debug(curSoftkeyPage);
        // Update softkeys on page
        curSoftkeyPage.forEach(function(softkey, index) {
            // Get text
            $(`#softkey${index+1} .btn-text`).html(softkey.Name);
            // Get state
            switch (softkey.State)
            {
                case "On":
                    $(`#softkey${index+1}`).addClass("pressed");
                    softkeyStates[index] = true;
                    break;
                case "Off":
                    $(`#softkey${index+1}`).removeClass("pressed");
                    break;
            }
        });
    // Clear if we don't
    } else {
        for (i=0; i<6; i++) {
            $(`#softkey${i+1} .btn-text`).html("");
        }
        // Disable softkeys
        $("#radio-controls .btn").addClass("disabled");
        $("#radio-controls .btn").removeClass("pressed");
        // Disable alert button and hide bar
        hideAlertBar();
    }
    exUpdateSoftkeys(softkeyStates);
}

/**
 * Handles connect button click on radio card
 * @param {event} event button click event
 * @param {object} obj html object
 */
function connectButton(event, obj) {
    // Stop propagation of click
    event.stopPropagation();
    // Get ID of radio to mute
    const radioId = $(obj).closest(".radio-card").attr('id');
    console.trace(`${radioId} connect button clicked`);
    // Get index of radio in list
    const idx = getRadioIndex(radioId);
    // If disconnected, connect
    if (radios[idx].status.State == 'Disconnected') {
        connectRadio(idx);
    } else {
        disconnectRadio(idx);
    }
}

/***********************************************************************************

    Radio Backend Functions

***********************************************************************************/

/**
 * Prepare and send an envelope message 
 * @param idx the radio index in radios[]
 * @param payload the envelope payload to send
 */
function sendEnvelope(idx: number, payload: Partial<Envelope>): void {
    const radio = radios[idx];
    const env: Envelope = Envelope.fromPartial({
        envelopeSeq: radio.envSeq++,
        timestampUs: nowMicros(),
        ...payload
    });
    radio.connection.send(Envelope.encode(env).finish());
}

/**
 * Send a command to the specified radio
 * @param idx the index of the radio
 * @param command the command to send
 * @param softkey an optional softkey
 * @returns a promise that resolves once the command is sent
 */
async function sendRadioCommand(idx: number, command: Proto.RadioCommandType, softkey?: Proto.SoftkeyName): Promise<void> {
    // Register this command in our request tracker and get the async promise
    const { requestId, promise } = radios[idx].requests.register(command);
    // Send the command
    sendEnvelope(idx, { control: { radioCommand: { command, softkey, requestId } } } );
    // Return the promise
    return promise;
}

/**
 * Start radio PTT
 */
function startPtt(micActive) {
    if (!pttActive && selectedRadio) {
        // Only send the TX command and unmute the mic if we're fully connected
        if (radios[selectedRadioIdx].handshakeComplete) {
            console.log("Starting PTT on " + selectedRadio);
            pttActive = true;

            // Clear any pending stop TX timeouts
            if (alertStopTimeout)
            {
                console.debug("Clearing alert tone stop timeout due to PTT override");
                clearTimeout(alertStopTimeout)
                alertStopTimeout = null;
            }
            
            // Flag that we want the mic to unmute or not
            txUnmuteMic = micActive;
            // Send the command
            sendRadioCommand(selectedRadioIdx, RadioCommandType.START_TX);
        }
    } else if (!pttActive && !selectedRadio) {
        pttActive = true;
        console.log("No radio selected, ignoring PTT");
    } else if (pttActive && alertTonesInProgress) {
        alertPttOverride = true;
        console.warn("PTT overriding alert tone PTT timeout");
    }
}

/**
 * Stop radio PTT
 */
function stopPtt() {
    if (pttActive) {
        console.log("PTT released");
        pttActive = false;
        // Mute mic
        setTimeout( muteMic, audio.micMuteDelay );
        // Play sound
        //playSound("sound-ptt-end");
        // Send the stop command if connected
        if (radios[selectedRadioIdx].wsConn && selectedRadio) {
            // Wait and then stop TX (handles mic latency)
            setTimeout( function() {
                radios[selectedRadioIdx].wsConn.send(JSON.stringify(
                    {
                        "radio": {
                            "command": "stopTx"
                        }
                    }
                ));
            }, radios[selectedRadioIdx].rtc.txLatency);
        }
    }
}

/**
 * Change channel on selected radio
 * @param {bool} down Whether to go down or not (heh)
 */
function changeChannel(down) {
    if (!pttActive && selectedRadio && radios[selectedRadioIdx].wsConn) {
        if (down) {
            console.log("Changing channel down on " + selectedRadio);
            radios[selectedRadioIdx].wsConn.send(JSON.stringify(
                {
                    "radio": {
                        "command": "chanDn"
                    }
                }
            ));
        } else {
            console.log("Changing channel up on " + selectedRadio);
            radios[selectedRadioIdx].wsConn.send(JSON.stringify(
                {
                    "radio": {
                        "command": "chanUp"
                    }
                }
            ));
        }
    }
}

/**
 * Press a softkey on the selected radio
 * @param {int} idx softkey index
 */
function pressSoftkey(idx) {
    // convert the current softkey index to the softkey in the radio
    var pressedKey = radios[selectedRadioIdx].status.Softkeys.slice(6*softkeyPage, 6+(6*softkeyPage))[idx-1];
    console.debug("Mapped pressed softkey to softkey:" + pressedKey.Name);
    pressButton(pressedKey.Name);
}

/**
 * Release a softkey on the selected radio
 * @param {int} idx softkey index
 */
function releaseSoftkey(idx) {
    var releasedKey = radios[selectedRadioIdx].status.Softkeys.slice(6*softkeyPage, 6+(6*softkeyPage))[idx-1];
    console.debug("Mapped pressed softkey to softkey:" + releasedKey.Name);
    releaseButton(releasedKey.Name);
}

/**
 * Left arrow button decrements the softkey page
 */
function button_left() {
    if (config.Audio.ButtonSounds) {
        playSound("sound-click");
    }
    maxPages = Math.ceil(radios[selectedRadioIdx].status.Softkeys.length / 6) - 1;
    if (softkeyPage == 0)
    {
        softkeyPage = maxPages;
    }
    else
    {
        softkeyPage--;
    }
    console.debug(`Moving to softkey page ${softkeyPage}`);
    updateRadioControls();
}

/**
 * Right arrow button increments the softkey page
 */
function button_right() {
    if (config.Audio.ButtonSounds) {
        playSound("sound-click");
    }
    maxPages = Math.ceil(radios[selectedRadioIdx].status.Softkeys.length / 6) - 1;
    if (softkeyPage == maxPages)
    {
        softkeyPage = 0;
    }
    else
    {
        softkeyPage++;
    }
    console.debug(`Moving to softkey page ${softkeyPage}`);
    updateRadioControls();
}

/**
 * Send button commands to selected radio
 * @param {string} buttonName name of button
 */
function toggleButton(buttonName) {
    if (!pttActive && selectedRadio && radios[selectedRadioIdx].wsConn) {
        console.log(`Sending button toggle: ${buttonName}`);
        radios[selectedRadioIdx].wsConn.send(JSON.stringify(
            {
                "radio": {
                    "command": "buttonToggle",
                    "options": buttonName
                }
            }
        ));
    }
}

function pressButton(buttonName) {
    if (!pttActive && selectedRadio && radios[selectedRadioIdx].wsConn) {
        console.log(`Sending button depress: ${buttonName}`);
        radios[selectedRadioIdx].wsConn.send(JSON.stringify(
            {
                "radio": {
                    "command": "buttonPress",
                    "options": buttonName
                }
            }
        ));
    }
    // Set a timeout to release the button in the event that something breaks
    stuckButtonTimeout = setTimeout(() => {
        console.debug(`Fallback button release handler for stuck button ${buttonName}`);
        releaseButton(buttonName);
    }, 1500);
}

function releaseButton(buttonName) {
    if (!pttActive && selectedRadio && radios[selectedRadioIdx].wsConn) {
        // Clear timeout if running
        if (stuckButtonTimeout != null) {
            clearTimeout(stuckButtonTimeout);
            stuckButtonTimeout = null;
        }
        console.log(`Sending button release: ${buttonName}`);
        radios[selectedRadioIdx].wsConn.send(JSON.stringify(
            {
                "radio": {
                    "command": "buttonRelease",
                    "options": buttonName
                }
            }
        ));
    }
}

function toggleMute(idx) {
    // Check mute
    if (radios[idx].mute) {
        muteRadio(idx, false);
    } else {
        muteRadio(idx, true);
    }
}

function muteButton(event, obj) {
    // Get ID of radio to mute
    const radioId = $(obj).closest(".radio-card").attr('id');
    // Get index of radio in list
    const idx = getRadioIndex(radioId);
    // toggle mute
    toggleMute(idx);
    // stop prop
    event.stopPropagation();
}

function startDTMF(radioId, number, digitTime, delayTime) {
    // Start PTT
    startPtt(false);
    // Dial (will wait for transmit active before dialing)
    dialNumber(radioId, number, digitTime, delayTime);
}

/**
 * Waits for PTT to be active and then dials the number
 * @param {int} radioIdx index of the radio dialing (for re-enabling DTMF when done)
 * @param {string} number number to dial
 * @param {int} digitTime time to play each digit
 * @param {int} delayTime time between each digit
 * 
 * @returns {int} the total duration of the dial event
 */
function dialNumber(radioId, number, digitTime, delayTime) {
    // Wait until we're transmitting
    if (radios[selectedRadioIdx].status.State != "Transmitting") {
        console.debug("Waiting for radio to start transmitting");
        setTimeout(() => {
            dialNumber(radioId, number, digitTime, delayTime);
        }, 50);
    } else {
        console.debug("Radio is now transmitting, dialing DTMF");
        // initial delay before first tone is played
        const startTime = dtmfTiming.initialDelay;
        // Set a timeout for each digit
        for (let i = 0; i < number.length; i++) {
            const nextTime = startTime + ((digitTime + delayTime) * i);
            console.debug(`Scheduling digit ${number[i]} for time ${nextTime}`);
            sendDigit(number[i], digitTime, nextTime);
        }
        // Stop PTT after dialing
        setTimeout(() => {
            stopPtt();
        }, startTime + ((digitTime + delayTime) * number.length));
        // Re-enable mic and DTMF keypad a little later
        setTimeout(()=> {
            enableDTMFKeypad(radioId, true);
            clearDTMFDialpad(radioId);
        }, startTime + ((digitTime + delayTime) * number.length));
    }
}

function startAlert(mode) {
    // Bonk if no radio is selected
    if (!selectedRadio) {
        bonk();
    }
    // Start PTT if we need to, otherwise PTT was overridden
    if (!pttActive) {
        startPtt(false);
        // Ensure mic doesn't unmute (should be covered by the above false but it gets weird sometimes)
        txUnmuteMic = false;
    } else {
        alertPttOverride = true;
    }
    // Set flag
    alertTonesInProgress = true;
    // Set and start tone gen
    switch (mode) {
        case 1:
            audio.tones.mode = "cont";
            break;
        case 2:
            audio.tones.mode = "alt";
            break;
        case 3:
            audio.tones.mode = "pulse";
            break;
    }
    // Wait for TX
    alertStartTimeout = setTimeout(() => {
        sendAlert()
    }, 50);
}

function sendAlert() {
    // Wait for TX
    if (radios[selectedRadioIdx].status.State != "Transmitting") {
        console.debug("waiting for radio to start transmitting");
        alertStartTimeout = setTimeout(() => {
            sendAlert();
        }, 50);
    } else {
        // Ensure mic is muted
        muteMic();
        console.debug("Radio transmitting, starting alert tone");
        alertStartTimeout = setTimeout(() => {
            audio.tones.start();
            alertStartTimeout = null;
        }, radios[selectedRadioIdx].rtc.txLatency)
    }
}

function stopAlert() {
    // Cancel start timeout if pending
    if (alertStartTimeout != null)
    {
        console.debug("Cancelling alert tone start");
        clearTimeout(alertStartTimeout);
        alertStartTimeout = null;
        stopPtt();
    }
    // Normal tone end with TX voice tail 
    else
    {
        console.debug("Stopping alert tones");
        // Stop the tones
        audio.tones.stop();
        // Re-enable the mic
        setTimeout(unmuteMic, audio.micUnmuteDelay + 100);
        // Only start the 5 second timer if we haven't been overridden
        if (!alertPttOverride) {
            // We wait 5 seconds after the release of the alert button before releasing PTT
            alertStopTimeout = setTimeout(() => {
                stopPtt();
                alertStopTimeout = null;
            }, 5000);
        } else {
            alertPttOverride = false;
        }
    }
    // Clear flag
    alertTonesInProgress = false;
}

/**
 * Returns true if a radio is receiving clear or encrypted traffic
 * @param {int} idx radio index
 * @returns bool
 */
function isRadioReceiving(idx) {
    // Radio is receiving if card has "receiving" or "encrypted" classes
    const classes = radios[idx].elements.card.classList;
    return (classes.contains("receiving") || classes.contains("encrypted"));
}

/**
 * Returns true if a radio is receiving or transmitting
 * @param {int} idx radio index
 * @returns bool
 */
function isRadioActive(idx) {
    const classes = radios[idx].elements.card.classList;
    return (classes.contains("receiving") || classes.contains("encrypted") || classes.contains("transmitting"));
}

/***********************************************************************************
    Global UI Functions
***********************************************************************************/

/**
 * Toggles the state of the sidebar menu
 */
function toggleMainMenu() {
    if (menuOpen) {
        $("#sidebar-mainmenu").addClass("sidebar-closed");
        $("#button-mainmenu").removeClass("button-active")
        menuOpen = false;
    } else {
        $("#sidebar-mainmenu").removeClass("sidebar-closed");
        $("#button-mainmenu").addClass("button-active")
        menuOpen = true;
    }
}

/**
 * Shows the specified popup and dims the main screen behind it
 * @param {string} id element ID of the popup to show
 */
function showConfigPopup(id) {
    console.debug(`Showing popup ${id}`);
    $("#body-dimmer").show();
    $(id).show();
}

/**
 * Close a popup window and undim the background
 * @param {string} obj the object whose parent .popup window will be closed
 */
function closePopup(obj = null) {
    // Close specific popup if specified
    if (obj) {
        $(obj).closest(".popup").hide();
    }
    // Close all popups otherwise
    else {
        $('.popup').hide();
    }
    $("#body-dimmer").hide();
}

/**
 * Update the clock based on the selected time format
 */
function updateClock() {
    if (!config) {return;}
    var timestr = "HH:mm:ss"
    if (config.clockFormat == Cfg.ClockFormat.Local) {
        var time = getTimeLocal(timestr);
        $("#clock").html(time + " " + timeZone);
    } else if (config.clockFormat == Cfg.ClockFormat.UTC) {
        $("#clock").html(getTimeUTC(timestr + " UTC"));
    } else {
        console.error("Invalid time format!")
    }
}

function connectAllButton() {
    // Connect if button is red
    if ($(`#navbar-connect`).hasClass("disconnected")) {
        radios.forEach((radio, index) => {
            // Start connecting to the radio
            connectRadio(index);
        });
    } else if ($(`#navbar-connect`).hasClass("connected")) {
        radios.forEach((radio, index) => {
            disconnectRadio(index);
        });
    }
    
}

/**
 * Show a new alert dialog
 * @param {string} id id of the alert dialog
 * @param {string} title title text
 * @param {string} text body text
 */
function showAlert(id, title, text) {
    var newAlertDialog = alertTemplate.content.cloneNode(true);
    newAlertDialog.querySelector(".alert-dialog").title = title;
    newAlertDialog.querySelector(".alert-dialog").html = text;
    newAlertDialog.id = id;
    $("body").append(newAlertDialog);
    $(`#${id}`).on('dialogclose', function(event) {
        closeAlert(id);
    });
    $(`#${id}`).dialog();
}

function closeAlert(id) {

}

/***********************************************************************************
    Global Backend Functions
***********************************************************************************/

/**
 * Returns UTC time string in given format
 * @param {string} formatString Time formatting string
 * @returns the formatted time string
 */
function getTimeUTC(formatString) {
    // Get UTC time
    var now = dayjs.utc();
    return now.format(formatString);
}

/**
 * Returns local time string in given format
 * @param {string} formatString Time formatting string
 * @returns the formatted local time string
 */
function getTimeLocal(formatString) {
    // Get local time
    var now = dayjs();
    return now.format(formatString);
}

/**
 * Get radio index from id string (radio1 returns 1)
 * @param {string} id radio id
 * @returns index of radio
 */
function getRadioIndex(id) {
    return idx = parseInt(id.replace("radio", ""));
}

/***********************************************************************************
    Config Reading/Writing to Json
***********************************************************************************/

async function readConfig() {

    // Read config via IPC
    const configObj = await window.electronAPI.readConfig();

    // Validate we got something
    if (configObj) {
        console.debug("Successfully read config from file");
    } else {
        console.error("Failed to read config from file, using defaults");
    }

    // Check if this is a recent config that has a version
    if (configObj.hasOwnProperty('version')) {
        // Ensure the version is current or older
        if (configObj.version <= config.version) {
            config = configObj as Cfg.Configuration;
            console.debug(`Loaded version ${config.version} config from file`);
            console.debug(config);
        } else {
            console.error(`Incompatible config version ${configObj.version} loaded! Using defaults`);
        }

        // Apply the loaded config
        applyConfig();
        
    // Otherwise, we try to load the legacy config file
    } else {
        console.warn("Non-versioned config loaded, attempting to convert to new format");
        
        // Populate top-level configs
        if (configObj.hasOwnProperty('Autoconnect')) {
            config.autoConnect = configObj.Autoconnect;
        }
        if (configObj.hasOwnProperty('ClockFormat')) {
            config.clockFormat = Cfg.ClockFormat[configObj.ClockFormat as string];
        }

        // Populate audio config
        if (configObj.hasOwnProperty('Audio')) {
            const audioObj = configObj.Audio;
            if (audioObj.hasOwnProperty('ButtonSounds')) {
                config.audio.buttonSounds = audioObj.ButtonSounds;
            }
            if (audioObj.hasOwnProperty('UnselectedVol')) {
                config.audio.unselectedVolume = audioObj.UnselectedVol;
            }
            if (audioObj.hasOwnProperty('ToneVolume')) {
                config.audio.toneVolume = audioObj.ToneVolume;
            }
            if (audioObj.hasOwnProperty('UseAGC')) {
                config.audio.useAGC = audioObj.UseAGC;
            }
        }

        // Populate radios from config
        if (configObj.hasOwnProperty('Radios')) {
            configObj.Radios.forEach(radio => {
                config.radios.push({
                    address: radio.address,
                    port: radio.port,
                    name: radio.name || "",
                    pan: radio.pan,
                    color: Cfg.parseCardColor(radio.color),
                    midiPttCC: radio.midiPttCC || null,
                    midiVolumeCC: radio.midiVolumeCC || null
                });
            });
            console.debug("Radio list initialized");
        }

        // Populate extension config
        if (configObj.hasOwnProperty('Extension')) {
            const extObj = configObj.Extension;
            if (extObj.hasOwnProperty('address')) {
                config.extension.address = extObj.address;
            }
            if (extObj.hasOwnProperty('port')) {
                config.extension.port = extObj.port;
            }
        }

        // Populate Peripheral Config
        if (configObj.hasOwnProperty('Peripherals')) {
            const periphObj = configObj.Peripherals;
            if (periphObj.hasOwnProperty('serialPort')) {
                config.peripherals.serial.port = periphObj.serialPort;
            }
            if (periphObj.hasOwnProperty('useCtsForPtt')) {
                if (periphObj.useCtsForPtt) {
                    config.peripherals.serial.pttLine = Cfg.SerialControlInput.CTS;
                    config.peripherals.serial.enabled = true;
                }
            }
        }

        // Populate Midi config
        if (configObj.hasOwnProperty('Midi')) {
            const midiObj = configObj.Midi;
            if (midiObj.hasOwnProperty("port")) {
                config.peripherals.midi.port = midiObj.port;
            }
            if (midiObj.hasOwnProperty("enabled")) {
                config.peripherals.midi.enabled = midiObj.enabled;
            }
            if (midiObj.hasOwnProperty("ccs")) {
                if (midiObj.ccs.hasOwnProperty('masterPtt')) {
                    config.peripherals.midi.masterPtt = {
                        channel: midiObj.ccs.masterPtt.chan,
                        number: midiObj.ccs.masterPtt.num
                    }
                }
                if (midiObj.ccs.hasOwnProperty('masterVol')) {
                    config.peripherals.midi.masterVol = {
                        channel: midiObj.ccs.masterVol.chan,
                        number: midiObj.ccs.masterVol.num
                    }
                }
            }
        }

        console.warn("Parsed old config format");
        console.debug(config);

        // Apply the loaded config
        applyConfig();

        // Warn the user that they need to save the config to apply the conversion
        alert("Old config format loaded, please save the current configuration to update your config file");
    }
}

function applyConfig() {

    // Populate the config UI checkboxes
    $("#daemon-autoconnect").prop('checked', config.autoConnect);
    $("#client-timeformat").val(config.clockFormat);
    $("#client-rxagc").prop("checked", config.audio.useAGC);
    $(`#unselected-vol option[value=${config.audio.unselectedVolume}]`).attr('selected', 'selected');
    $(`#tone-vol option[value=${config.audio.toneVolume}]`).attr('selected', 'selected');
    $('#sound-ptt').prop("volume", dbToGain(config.audio.toneVolume));
    $('#sound-ptt-end').prop("volume", dbToGain(config.audio.toneVolume));
    $('#sound-click').prop("volume", dbToGain(config.audio.toneVolume));
    $("#extension-address").val(config.extension.address);
    $("#extension-port").val(config.extension.port);

    // Try to open midi port if enabled
    if (config.peripherals.midi.enabled) {
        window.electronAPI.openMidiPort(config.peripherals.midi.port);
    }
    
    // Initialize master radio array
    config.radios.forEach( (radio, idx) => {
        radios.push({
            cfg: radio,
            handshakeComplete: false,
            envSeq: 0,
            elements: {
                idBase: `radio${idx}`
            }
        });
    });

    // Populate radio cards
    populateRadios();

    // If autoconnect is specified, autoconnect!
    if (config.autoConnect) {
        connectAllButton();
    }
}

async function saveConfig() {

    // Store client config values into our config object
    const clockFormat = $("#client-timeformat").val() as string;
    const useAgc = $("#client-rxagc").is(":checked") as boolean;
    const unselectedVol = $("#unselected-vol").val() as number;
    const toneVol = $("#tone-vol").val() as number;
    config.clockFormat = Cfg.ClockFormat[clockFormat];
    config.audio.useAGC = useAgc;
    config.audio.unselectedVolume = unselectedVol;
    config.audio.toneVolume = toneVol;

    // Store extension config values
    const extensionAddress = $("#extension-address").val() as string;
    const extensionPort = $("#extension-port").val() as number;
    config.extension.address = extensionAddress;
    config.extension.port = extensionPort;

    // Update tone audio gains
    $('#sound-ptt').prop("volume", dbToGain(config.audio.toneVolume));
    $('#sound-ptt-end').prop("volume", dbToGain(config.audio.toneVolume));
    $('#sound-click').prop("volume", dbToGain(config.audio.toneVolume));

    // Update radio audio
    if (audio.context) {
        updateRadioAudio();
    }

    const result = await window.electronAPI.saveConfig(JSON.stringify(config, null, 4));

    if (result != true)
    {
        alert("Failed to save config: " + result);
    }
}

function newRadioClear() {
    $('#new-radio-address').val('');
    $('#new-radio-port').val('');
    $('#new-radio-pan').val(0);
}

/***********************************************************************************
    Audio Handling Functions
***********************************************************************************/

function createAudioSource(idx: number): void {
    console.log(`[${radios[idx].name}]: Creating new audio source`);
    // Create audio source from the track and put it in an object with a local gain node
    var newSource = {
        filterNode: audio.context.createBiquadFilter(),
        agcNode: audio.context.createDynamicsCompressor(),
        makeupNode: audio.context.createGain(),
        gainNode: audio.context.createGain(),
        muteNode: audio.context.createGain(),
        panNode: audio.context.createStereoPanner(),
        analyzerNode: audio.context.createAnalyser(),
        leftSpkr: true,
        rightSpkr: true,
        analyzerData: new Float32Array(newSource.analyzerNode.fftSize)
    }

    // Setup lowpass filter
    newSource.filterNode.type = 'lowpass'
    newSource.filterNode.frequency.setValueAtTime(audio.filterCutoff, audio.context.currentTime);

    // Setup AGC node
    newSource.agcNode.knee.setValueAtTime(audio.agcKnee, audio.context.currentTime);
    newSource.agcNode.ratio.setValueAtTime(audio.agcRatio, audio.context.currentTime);
    newSource.agcNode.attack.setValueAtTime(audio.agcAttack, audio.context.currentTime);
    newSource.agcNode.release.setValueAtTime(audio.agcRelease, audio.context.currentTime);

    // Set current pan setting
    var newPan = $(`#radio${idx}`).find('.radio-pan').val();
    newSource.panNode.pan.setValueAtTime(newPan, audio.context.currentTime);

    // Connect all the nodes
    newSource.filterNode.connect(newSource.agcNode);
    newSource.agcNode.connect(newSource.makeupNode);
    newSource.makeupNode.connect(newSource.muteNode);
    newSource.makeupNode.connect(newSource.analyzerNode);
    newSource.muteNode.connect(newSource.gainNode);
    newSource.gainNode.connect(newSource.panNode);
    newSource.panNode.connect(audio.outputGain);

    // Add to list of radio streams
    radios[idx].audioSrc = newSource;
}

/**
 * Queries the specified type of media device
 * @param {string} type type of device to find ('audioinput', 'audiooutput', or 'videoinput')
 * @param {function} callback callback to pass the filtered list of devices once retreived
 */
async function queryDeviceType(type) {
    const devices = await navigator.mediaDevices.enumerateDevices();
    return devices.filter(device => device.kind === type)
}

/**
 * Populate the audio device lists
 */
async function getAudioDevices() {
    const audioInputs = await queryDeviceType('audioinput');
    const audioOutputs = await queryDeviceType('audiooutput');
    
    audioInputs.forEach(input => {
        var name = input.label;
        /*if (name.length > 30) {
            name = name.substring(0,30) + "...";
        }*/
        const device = input.deviceId;
        $("#audio-input").append($('<option>', {
            value: device,
            text: name
        }));
    })

    audioOutputs.forEach(output => {
        var name = output.label;
        /*if (name.length > 30) {
            name = name.substring(0,30) + "...";
        }*/
        const device = output.deviceId;
        $("#audio-output").append($('<option>', {
            value: device,
            text: name
        }));
    })
}

/** 
* Checks for browser compatibility and sets up audio devices
* @return {bool} True on success
*/
function startAudioDevices() {
    // Create audio context
    audio.context = new AudioContext();
    console.log("Created audio context");

    // Create analyzer node for volume meter under volume slider
    audio.outputAnalyzer = audio.context.createAnalyser();
    audio.outputPcmData = new Float32Array(audio.outputAnalyzer.fftSize);

    // Create gain node for output volume and connect it to the default output device
    audio.outputGain = audio.context.createGain();
    audio.outputGain.gain.value = Math.pow($("#console-volume").val() / 100, 2);
    audio.outputGain.connect(audio.context.destination);

    // Start audio input
    console.log("Running initial microphone setup");
    // New, better (?) way
    navigator.mediaDevices.getUserMedia(userMediaSettings).then(
        // Add tracks to peer connection and negotiate if successful
        function(stream) {
            // Set up mic meter dependecies
            audio.inputStream = audio.context.createMediaStreamSource(stream);
            // Add handler for when the mic stream ends
            audio.inputStream.addEventListener("inactive", (event) => {
                console.warn("Mic input stream ended!");
                restartMicStream();
            });
            audio.inputAnalyzer = audio.context.createAnalyser();
            audio.inputPcmData = new Float32Array(audio.inputAnalyzer.fftSize);
            // Create a mic gain for muting the mic when we're not talking
            audio.inputMicGain = audio.context.createGain();
            muteMic();
            // Connect input mic stream to gain, and gain to destination and analyzer
            audio.inputStream.connect(audio.inputMicGain);
            audio.inputMicGain.connect(audio.inputAnalyzer);
            // Setup DTMF generator once we have our input audio nodes
            audio.dtmf = new DualTone(audio.context, 100, 200);
            // Setup Alert Tone generator
            audio.tones = new AlertTone(audio.context, "alt", 1500, 800);
            // We are now running
            audio.running = true;
        },
        // Report a failure to capture mic
        function(e) {
            alert('Error capturing microphone device');
            return false;
        }
    );

    // Enable volume slider
    $("#console-volume").prop('disabled', false);
}

function muteMic() {
    console.log("Muting mic");
    audio.inputMicGain.gain.value = 0;
}

function unmuteMic() {
    console.log("Unmuting mic");
    audio.inputMicGain.gain.value = 1;
}

function restartMicStream() {
    console.warn("Restarting mic input stream...");
    // Re-get user media
    navigator.mediaDevices.getUserMedia(userMediaSettings).then( function(stream) {
        // Recreate the input stream
        audio.inputStream = audio.context.createMediaStreamSource(stream);
        // Add handler for when the mic stream ends
        audio.inputStream.addEventListener("inactive", (event) => {
            console.warn("Mic input stream ended!");
            restartMicStream();
        });
        // Restart the mic track to reconnect everything
        restartMicTrack();
    });
}

/**
 * Updates audio meters based on current data. Only called after both mic & speaker are set up and running (otherwise errors)
 */
function audioMeterCallback() {
    // Stop if we're told to
    if (!audio_playing) {
        return;
    }

    // Update meters
    radios.forEach((radio, idx) => {
        // Ignore radios with no connected audio
        if (radios[idx].audioSrc == null) {
            if (radios[idx].elements.rxbar.style.width != 0) {
                radios[idx].elements.rxbar.style.width = 0;
            }
            return
        }
        // Ignore radio that isn't receiving (checking for the class compensates for the rx delay)
        if (!isRadioReceiving(idx)) {
            if (radios[idx].elements.rxbar.style.width != 0) {
                radios[idx].elements.rxbar.style.width = 0;
            }
            return
        }
        // Get data
        radios[idx].audioSrc.analyzerNode.getFloatTimeDomainData(radios[idx].audioSrc.analyzerData);
        // Process into average amplitude
        var sumSquares = 0.0;
        for (const amplitude of radios[idx].audioSrc.analyzerData) { sumSquares += (amplitude * amplitude); }
        // We just scale this summed squared value by a constant to avoid actually doing an RMS calculation every single frame
        const newPct = String(Math.sqrt(sumSquares / radios[idx].audioSrc.analyzerData.length).toFixed(3) * 300);
        radios[idx].elements.rxbar.style.width = newPct;
    });

    // Input meter (only show when PTT)
    if (selectedRadioIdx != null && selectedRadioIdx >= 0) {
        if (pttActive) {
            // Get data from mic
            audio.inputAnalyzer.getFloatTimeDomainData(audio.inputPcmData);
            sumSquares = 0.0;
            for (const amplitude of audio.inputPcmData) { sumSquares += amplitude * amplitude; }
            const newPct = String(Math.sqrt(sumSquares / audio.outputPcmData.length).toFixed(3) * 300);
            // Apply to selected radio only
            radios[selectedRadioIdx].elements.txbar.style.width = newPct;
        } else {
            radios[selectedRadioIdx].elements.txbar.style.width = 0;
        }
    }

    // Request next frame
    window.requestAnimationFrame(audioMeterCallback);
}

function checkAudioMeterCallback()
{
    // Get the overall "audio doing something" status (we check classes instead of actual statuses to account for the latency delays)
    console.debug("Checking if any radio's audio is active");
    audio_active = false;
    radios.forEach((radio, idx) => {
        if (isRadioActive(idx))
        {
            console.debug(`${radio.name} audio active`);
            audio_active = true;
        }
    });
    // If audio was active and isn't any longer, stop things
    if (audio_playing && !audio_active) {
        console.debug("Stopping audio meter callback, all radios idle");
        audio_playing = false;
        zeroAudioMeters();
    // If audio wasn't active and now is, start up the animation callback again
    } else if (!audio_playing && audio_active) {
        console.debug("Starting audio meter callback, radio is active");
        audio_playing = true;
        window.requestAnimationFrame(audioMeterCallback);
    }
}

/**
 * Set all audio meters on all radio cards to zero
 */
function zeroAudioMeters()
{
    radios.forEach((radio, idx) => {
        radios[idx].elements.rxbar.style.width = 0;
        radios[idx].elements.txbar.style.width = 0;
    });
}

/**
 *  Change volume of console based on slider
 */
function volumeSlider() {
    // Convert 0-100 to 0-1 for multiplication with audio, using an inverse-square curve for better "logarithmic" volume
    const newVol = Math.pow($("#console-volume").val() / 100, 2);
    // Set gain node to new value if it exists
    if (audio.outputGain != null)
    {
        audio.outputGain.gain.value = newVol;
    }
    // Set volume of each ui html sound
    const uiSounds = document.getElementsByClassName("ui-audio");
    for (var i = 0; i < uiSounds.length; i++) {
        uiSounds.item(i).volume = newVol;
    }
}

/**
 * Changes the value of the volume slider, which will then update the console volume through the slider callback
 * @param {int} increment 
 */
function changeVolume(increment) {
    var newVal = parseInt($("#console-volume").val()) + increment;
    if (newVal < 0) { newVal = 0} else if (newVal > 100) { newVal = 100 }
    $("#console-volume").val(newVal);
    volumeSlider();
}

/**
 * Changes the master console volume
 * @param {float} level volume level from 0 to 1
 */
function setVolume(level)
{
    // Range clamping
    if (level < 0)
    {
        level = 0;
    }
    else if (level > 1)
    {
        level = 1;
    }
    // Set volume
    $("#console-volume").val(Math.round(level * 100));
    // Trigger update
    volumeSlider();
}

/**
 * Play an HTML-embedded sound object
 * @param {string} soundId id of the HTML embed object
 */
function playSound(soundId) {
    // Get embedded element
    const sndSource = document.getElementById(soundId);
    // If not null, create a new object and play it (this lets us play the same sound more than once simultaneously)
    if (sndSource)
    {
        var snd = new Audio();
        snd.type = sndSource.getAttribute('type');
        snd.src = sndSource.getAttribute('src');
        snd.play();
    }
}

/**
 * Play the error sound
 */
function bonk() {
    playSound("sound-error");
}

/**
 * Updates the audio parameters for each radio audio source based on current config and selected radio
 */
 function updateRadioAudio() {
    console.debug("Updating radio sound parameters");
    radios.forEach(function(radio, idx) {
        // Ignore if audio not connected
        if (radios[idx].audioSrc == null) {
            console.debug(`  - Audio not connected for radio ${radios[idx].name}, skipping`);
            return;
        }
        if (idx == selectedRadioIdx) {
            console.debug(`  - Radio ${radios[idx].name} is selected. Setting gain to 1`);
            radios[idx].audioSrc.gainNode.gain.setValueAtTime(1, audio.context.currentTime);
        } else {
            console.debug(`  - Radio ${radios[idx].name} is unselected. Setting gain to ${config.Audio.UnselectedVol}`);
            radios[idx].audioSrc.gainNode.gain.setValueAtTime(dbToGain(config.Audio.UnselectedVol), audio.context.currentTime);
        }
        // Set AGC based on user setting
        if (config.Audio.UseAGC) {
            console.log(`  - Enabling AGC for radio ${radios[idx].name}`);
            radios[idx].audioSrc.agcNode.threshold.setValueAtTime(audio.agcThreshold, audio.context.currentTime);
            radios[idx].audioSrc.makeupNode.gain.setValueAtTime(audio.agcMakeup, audio.context.currentTime);
        } else {
            console.log(`  - Byassing AGC for radio ${radios[idx].name}`);
            radios[idx].audioSrc.agcNode.threshold.setValueAtTime(0, audio.context.currentTime);
            radios[idx].audioSrc.makeupNode.gain.setValueAtTime(1.0, audio.context.currentTime);
        }
    });
}

/**
 * Mutes the radio's audio at the given radio index
 * @param {int} idx index of the radio in radios[]
 * @param {bool} mute whether to mute or not
 */
function muteRadio(idx, mute) {
    if (mute) {
        console.info(`Muting radio ${idx}`);
        // Set audio node
        radios[idx].audioSrc.muteNode.gain.setValueAtTime(0, audio.context.currentTime);
        // Set icon
        $(`#radio${idx} .icon-mute`).addClass('muted');
        $(`#radio${idx} .icon-mute`).prop('name', 'volume-mute-sharp');
        // Set state
        radios[idx].mute = true;
    } else {
        console.info(`Unmuting radio ${idx}`);
        // Set audio node
        radios[idx].audioSrc.muteNode.gain.setValueAtTime(1, audio.context.currentTime);
        // Set icon
        $(`#radio${idx} .icon-mute`).removeClass('muted');
        $(`#radio${idx} .icon-mute`).prop('name', 'volume-high-sharp');
        // Set state
        radios[idx].mute = false;
    }
}

/**
 * Various audio updates for the specified radio
 */
function updateAudio(idx) {
    console.debug(`[${radios[idx].name}]: Updating audio settings`);
    // Do nothing if audio sources aren't connected
    if (radios[idx].audioSrc == null) { 
        console.debug(`  - Audio sources not connected, skipping`);
        return;
    }
    // Mute if we're muted or not receiving, after the specified delay in rtc.rxLatency
    if (radios[idx].mute || !(radios[idx].status.State === 'Receiving' || radios[idx].status.State === 'Encrypted')) {
        setTimeout(function() {
            console.debug(`  - Muting audio for radio ${radios[idx].name}`);
            radios[idx].audioSrc.muteNode.gain.setValueAtTime(0, audio.context.currentTime);
        }, radios[idx].rtc.rxLatency);
    // Unmute only if we're not forced muted by the client
    } else if (!radios[idx].mute) {
        setTimeout(function() {
            console.debug(`  - Unmuting audio for radio ${radios[idx].name}`);
            radios[idx].audioSrc.muteNode.gain.setValueAtTime(1, audio.context.currentTime);
        }, radios[idx].rtc.rxLatency);
    }
}

/**
 * Shows or hides the pan dropdown
 * @param {event} event 
 * @param {object} obj 
 */
function showPanMenu(event, obj) {
    const radioCard = $(obj).closest(".radio-card");
    if (radioCard.hasClass("disconnected")) {
        console.debug("Radio disconnected, not showing pan menu");
    } else {
        $(obj).closest(".radio-card").find(".panning-dropdown").toggleClass("closed");
    }
    event.stopPropagation();
}

/**
 * Shows or hides the dtmf dropdown
 * @param {event} event 
 * @param {object} obj 
 */
 function showDTMFMenu(event, obj) {
    const radioCard = $(obj).closest(".radio-card");
    if (radioCard.hasClass("disconnected")) {
        console.debug("Radio disconnected, not showing dtmf menu");
    } else {
        $(obj).closest(".radio-card").find(".dtmf-dropdown").toggleClass("closed");
        $(obj).closest(".radio-card").find(".dtmf-dialpad").toggleClass("closed");
    }
    event.stopPropagation();
}

function closeAllDropdownMenus() {
    $(".panning-dropdown").addClass("closed");
    $(".dtmf-dropdown").addClass("closed");
    $(".dtmf-dialpad").addClass("closed");
}

/**
 * Update the speaker pan for a radio (called by the slider value change)
 * @param {event} event the calling event
 * @param {object} obj the calling html object
 */
function changePan(event, obj) {
    // Prevent from selecting the card
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();
    // Get new value
    const newPan = $(obj).val();
    // Get radio ID and index
    const radioId = $(obj).closest(".radio-card").attr('id');
    const idx = getRadioIndex(radioId);
    // Debug log
    console.debug(`[${radios[idx].name}]: Setting new pan value ${newPan}`);
    // Set pan
    radios[idx].pan = newPan;
    radios[idx].audioSrc.panNode.pan.setValueAtTime(newPan, audio.context.currentTime);
}

/**
 * Center the pan for a radio (fired by slider doubleclick)
 * @param {event} event the calling button event 
 * @param {object} obj the calling html object
 */
function centerPan(event, obj) {
    // Update slider value
    $(obj).val(0);
    // Get radio index
    const radioId = $(obj).closest(".radio-card").attr('id');
    const idx = getRadioIndex(radioId);
    // Update pan
    console.debug(`[${radios[idx].name}]: Resetting pan value`);
    // Set pan
    radios[idx].audioSrc.panNode.pan.setValueAtTime(0, audio.context.currentTime);
}

function dtmfPressed(event, obj) {
    // Make sure it's a valid cell
    const cell = event.target.closest('td');
    if (!cell) {
        console.log("No cell clicked");
        return;
    }
    // Get dialpad object
    const digitTable = obj.closest('.dtmf-table');
    // Don't do anything if the table is disabled
    if (digitTable.getAttribute('disabled')) {
        console.log("DTMF keypad is disabled");
        stopClick(event, obj);
        return;
    }
    // Get the dtmf dialpad span
    const dialpad = obj.closest('.button-stack').querySelector('.dtmf-digits');
    // Get digit number
    const digit = cell.innerHTML;
    // Get ID of the radio this dialpad is for
    const radioId = obj.closest('.radio-card').id;
    // Log
    console.debug(`Clicked dtmf ${digit}`);
    // Handle clear or call
    if (digit.includes("call-sharp")) {
        // Get number to dial
        const digits = dialpad.innerHTML;
        // Do nothing if no digits
        if (digits.length < 1) {
            return;
        }
        // Disable keypad
        enableDTMFKeypad(radioId, false);
        // switch focus to radio if it's not already
        deselectRadios();
        selectRadio(radioId);
        // Dial
        startDTMF(radioId, digits, dtmfTiming.digitDuration, dtmfTiming.digitDelay);
    } else if (digit.includes("trash-sharp")) {
        // Clear the dialpad
        dialpad.innerHTML = "";
    } else {
        // Only add digits if we're under 10
        console.debug("Adding digit to dialpad");
        if (dialpad.innerHTML.length < 10) {
            dialpad.innerHTML += digit;
        }
    }
    stopClick(event, obj);
}

function enableDTMFKeypad(radioId, enabled) {
    // Get elements
    const radioCard = document.querySelector(`#${radioId}`);
    const digitTable = radioCard.querySelector('.dtmf-table');
    // Disable
    if (enabled) {
        console.debug("Re-enabling DTMF");
        digitTable.removeAttribute('disabled');
    } else {
        console.debug("Disabling DTMF");
        digitTable.setAttribute('disabled', true);
    }
}

function clearDTMFDialpad(radioId) {
    // Get elements
    const radioCard = document.querySelector(`#${radioId}`);
    const dialpad = radioCard.querySelector('.dtmf-digits');
    dialpad.innerHTML = "";
}

/***********************************************************************************
    Console Alert Tone Functions
***********************************************************************************/

/**
 * Console alert tone generator class
 * @param {audioContext} context the audio context
 * @param {string} mode alert mode, "cont", "alt", or "pulse"
 * @param {int} freq1 primary frequency
 * @param {int} freq2 secondary frequency, not used unless in "alt" mode
 */
function AlertTone(context, mode) {
    this.context = context;
    this.mode = mode;
    // Used to cancel the tone period timeout callback
    this.timeoutId = null;
    // Used to get the current status of the generator
    this.status = 0;
}

AlertTone.prototype.setup = function() {
    // Create the audio nodes
    this.osc = this.context.createOscillator();
    this.gain = this.context.createGain();
    this.filter = this.context.createBiquadFilter();
    // Setup initial values
    switch (this.mode) {
        case "cont":
        case "pulse":
            this.osc.frequency.value = 1000;
            break;
        case "alt":
            this.osc.frequency.value = 1500;
            break;
    }
    this.gain.gain.value = audio.tonesGain;
    this.filter.type = 'lowpass';
    this.filter.frequency = '4000';
    // Connect
    this.osc.connect(this.gain);
    this.gain.connect(this.filter);
    this.filter.connect(audio.outputGain);
    this.filter.connect(audio.inputAnalyzer);
}

AlertTone.prototype.timerCallback = function() {
    // behavior changes depending on mode
    switch (this.mode) {
        case "cont":
            // Do nothing
            break;
        case "alt":
            // Get current osc frequency
            const curFreq = this.osc.frequency.value;
            // Change to the other one
            if (curFreq == 1500) {
                this.osc.frequency.value = 800;
            } else {
                this.osc.frequency.value = 1500;
            }
            break;
        case "pulse":
            // Get current gain
            const curGain = this.gain.gain.value;
            // Mute or unmute
            if (curGain == 0) {
                this.gain.gain.value = audio.tonesGain;
            } else {
                this.gain.gain.value = 0;
            }
            break;
    }
    // Set the timeout again
    this.timeoutId = setTimeout(() => {
        this.timerCallback();
    }, Math.floor(audio.tonesPeriod / 2) );
}

AlertTone.prototype.start = function() {
    this.setup();
    this.osc.start(0);
    this.status = 1;
    this.gain.gain.value = audio.tonesGain;
    // Start timer
    this.timeoutId = setTimeout(() => {
        this.timerCallback();
    }, Math.floor(audio.tonesPeriod / 2) );
}

AlertTone.prototype.stop = function() {
    this.osc.stop(0);
    if (this.timeoutId) {
        clearTimeout(this.timeoutId);
    }
    this.status = 0;
    this.gain.gain.value = 0;
}

/**
 * Show/Hide the alert bar
 */
function alertBar() {
    // Make sure button is not disabled
    if (document.getElementById("alert-bar-icon").classList.contains("disabled")) {
        return;
    }
    // Get the bar
    const bar = document.getElementById("alert-bar");
    if (bar.classList.contains("hidden")) {
        bar.classList.remove("hidden");
    } else {
        bar.classList.add("hidden");
    }
}

function hideAlertBar() {
    document.getElementById("alert-bar").classList.add("hidden");
}

/***********************************************************************************
    DTMF Functions
    Mostly lifted from the example here:
    https://codepen.io/edball/pen/EVMaVN
***********************************************************************************/

/**
 * Dual Tone Generator Class for DTMF
 * @param {audioContext} context Audio context
 * @param {int} freq1 frequency 1
 * @param {int} freq2 frequency 2
 */
function DualTone(context, freq1, freq2) {
    this.context = context;
    this.status = 0;
    this.freq1 = freq1;
    this.freq2 = freq2;
}

DualTone.prototype.setup = function() {
    // Create the audio nodes
    this.osc1 = this.context.createOscillator();
    this.osc2 = this.context.createOscillator();
    this.gainNode = this.context.createGain();
    this.filter = this.context.createBiquadFilter();
    // Setup initial values
    this.osc1.frequency.value = this.freq1;
    this.osc2.frequency.value = this.freq2;
    this.gainNode.gain.value = audio.dtmfGain;
    this.filter.type = 'lowpass';
    this.filter.frequency = '4000';
    // Connect everything
    this.osc1.connect(this.gainNode);
    this.osc2.connect(this.gainNode);
    this.gainNode.connect(this.filter);
    // Connect to both local speakers (for sidetone) and the mic destination/analyzer
    this.filter.connect(audio.outputGain);
    this.filter.connect(audio.inputAnalyzer);
}

DualTone.prototype.start = function() {
    this.setup();
    this.osc1.start(0);
    this.osc2.start(0);
    this.status = 1;
    this.gainNode.gain.value = audio.dtmfGain;
}

DualTone.prototype.stop = function() {
    this.osc1.stop(0);
    this.osc2.stop(0);
    this.status = 0;
    this.gainNode.gain.value = 0;
}

/**
 * Starts the DTMF generator for the specified digit and duration
 * @param {char} digit digit to send (0-9, A-D, # or *)
 * @param {int} duration duration to play digit in ms
 * @param {int} delay time to start playing the tone
 */
function sendDigit(digit, duration, delay) {
    const fPair = dtmfFrequencies[digit];
    if (audio.dtmf.status == 0) {
        setTimeout(() => {
            console.debug(`Starting digit ${digit}: ${fPair.f1}, ${fPair.f2}`);
            audio.dtmf.freq1 = fPair.f1;
            audio.dtmf.freq2 = fPair.f2;
            audio.dtmf.start();
        }, delay)
        setTimeout(() => {
            console.debug(`Stopping digit ${digit}`);
            audio.dtmf.stop();
        }, duration + delay);
    }
}

/***********************************************************************************
    Websocket Client Functions
***********************************************************************************/

/**
 * Create websocket connection to radio and wait for it to connect
 * @param {int} idx index of radio in radios[]
 */
function connectRadio(idx: number): void {
    // Log
    console.info(`Connecting to radio ${radios[idx].name}`);
    
    // Update radio connection icon
    $(`#radio${idx} .icon-connect`).removeClass('disconnected').addClass('connecting');
    $(`#radio${idx} .icon-connect`).parent().prop('title','Connecting to daemon');
    
    // Create audio context if we haven't already
    if (audio.context == null) {
        startAudioDevices();
    }

    // Set up the radio
    radios[idx].requests = new RequestTracker();
    radios[idx].envSeq = 0;
    radios[idx].handshakeComplete = false;
    radios[idx].connection = new WebSocket(`ws://${radios[idx].address}:${radios[idx].port}`);
    radios[idx].connection.binaryType = "arraybuffer";

    // Bind websocket callbacks
    radios[idx].connection.onopen = () => console.log(`[${radios[idx].name}]: Connection open, waiting for Hello`);
    radios[idx].connection.onmessage = (event) => handleEnvelope(idx, event);
    radios[idx].connection.onclose = (event) => handleSocketClose(event, idx);
    radios[idx].connection.onerror = (event) => handleSocketError(event, idx);
}

/**
 * Disconnect from the websocket server and close the audio handler
 * @param {int} idx radio index in radios[]
 */
function disconnectRadio(idx: number) : void {
    radios[idx].audioReceiver?.close();
    radios[idx].connection?.close();
}

/**
 * Handle a binary message from the radio's websocket
 * @param idx the radio index in radios[]
 * @param event the MessageEvent containing the binary message
 */
function handleEnvelope(idx: number, event: MessageEvent): void {
    // Decode to an envelope
    const env = Envelope.decode(new Uint8Array(event.data));

    // Handle a hello first
    if (env.control?.hello) {
        // Parse the hello and get the result
        const { result, ack } = handleHello(env.control.hello);
        // Send the ack back to the radio
        sendEnvelope(idx, { control: { helloAck: ack } } );
        // If the hello wasn't accepted, throw an error
        if (!result.accepted) {
            console.error(`[${radios[idx].name}]: ${result.reason}`);
        }
        // Either way, we completed the handshake
        radios[idx].handshakeComplete = true;
        return;
    }

    // If we haven't completed the handshake yet, ignore all other messages
    if (!radios[idx].handshakeComplete) return;

    // Next, handle all the control messages
    // Radio status
    if (env.control?.radioStatus) {
        // Call status handler
        radios[idx].requests.onStatus(env.control.radioStatus.state);
        // Update radio status
        radios[idx].status = env.control.radioStatus;
        // Fire the UI update functions
        updateRadioCard(idx);
        updateRadioControls();
        exUpdateRadio(idx);
    // ACK to a message
    } else if (env.control?.ack) {
        radios[idx].requests.resolve(env.control.ack.requestId);
    // NACK to a message
    } else if (env.control?.nack) {
        radios[idx].requests.reject(env.control.nack.requestId, env.control.nack.reason);
    // Respond to ping with a pong
    } else if (env.control?.ping) {
        sendEnvelope(idx, { control: { pong: { nonce: env.control.ping.nonce } } });
    }

    // Convert to JSON
    var msgObj;
    try {
        msgObj = JSON.parse(event.data);
    } catch (e) {
        console.warn(`[${radios[idx].name}]: Got invalid data websocket:`, event.data);
        console.warn(e);
        return;
    }

    // Iterate through each message and its data (normally we'd only get one at a time, but I suppose you could get more than one)
    for (const [key, value] of Object.entries(msgObj)) {
        // Handle message data based on key type
        switch (key) {
            // Radio status update
            case "status":
                // get status data
                var radioStatus = msgObj['status'];
                // Debug
                console.debug(`[${radios[idx].name}]: Got status update:`, radioStatus);
                // Update radio entry
                radios[idx].status = radioStatus;
                // Update radio card
                updateRadioCard(idx);
                // Update bottom controls
                updateRadioControls();
                // Update radio mute status
                updateAudio(idx);
                // Send extension update
                exUpdateRadio(idx);
                break;

            // WebRTC SDP answer
            case "webRtcAnswer":
                // get params
                answerType = value['type'];
                answerSdp = value['sdp'];
                gotRtcResponse(idx,answerType,answerSdp);
                break;

            // Speaker audio data
            case "audioData":
                // make sure it's actually speaker data
                if (value['source'] != "speaker") {
                    break;
                }
                // Process it
                getSpkrData(value['data']);
                break;

            // ACK handler
            case "ack":
                switch (value) {
                    case "startTx":
                        // Unmute mic after timeout, if requested
                        if (txUnmuteMic) {
                            setTimeout( unmuteMic, audio.micUnmuteDelay);
                            txUnmuteMic = false;
                        }
                        // Play TPT
                        playSound("sound-ptt");
                        break;
                    case "stopTx":
                        playSound("sound-ptt-end");
                        break;
                    case "chanDn":
                    case "chanUp":
                    //case "buttonPress":
                    case "buttonRelease":
                    case "buttonToggle":
                        if (config.Audio.ButtonSounds) {
                            playSound("sound-click");
                        }
                        break;
                }
                break;

            // NACK handler
            case "nack":
                console.error("Got NACK from server");
                playSound("sound-error")
                break;
        }
    }
}

/**
 * Handle the websocket closing
 * @param {event} event socket closed event
 * @param {int} idx index of radio in radios[]
 */
function handleSocketClose(event, idx) {
    // Console warning
    console.warn(`Websocket connection closed to ${radios[idx].name}`);
    console.debug(event);
    if (event.data) {console.warn(event.data);}

    // Cleanup
    if (radios[idx].rtc != null) {
        stopWebRtc(idx);
    }
    radios[idx].wsConn = null;
    radios[idx].status.State = 'Disconnected';

    // UI Update
    $(`#radio${idx} .icon-connect`).removeClass('connected');
    $(`#radio${idx} .icon-connect`).removeClass('connecting');
    $(`#radio${idx} .icon-connect`).addClass('disconnected');
    $(`#radio${idx} .icon-connect`).parent().prop('title','Disconnected');
    updateRadioCard(idx);

    // If no more radios connected, set master connect button to disconnected and close serial port
    if (!radios.some(e => e.wsConn != null)) {
        // Set navbar icon to disconnected
        $(`#navbar-connect`).removeClass("connected");
        $(`#navbar-connect`).addClass("disconnected");
        // Close serial port
        window.electronAPI.closeSerialPort();
    }
}

/**
 * Handle connection errors from the server
 * @param {event} event 
 */
function handleSocketError(event, idx) {
    console.error(`[${radios[idx].name}]: Websocket connection error:`);
    console.error(event);
}

function restartRadio(event, obj) {
    // Stop propagation of click
    event.stopPropagation();
    // Get raido index
    const radioId = $(obj).closest(".radio-card").attr('id');
    console.log(`Restarting radio ${radioId}`);
    // Get index of radio in list
    const idx = getRadioIndex(radioId);
    // Stop PTT if running
    if (radios[idx].status.State == "Transmitting" || selectedRadioIdx == idx) {
        console.info("Stopping PTT and deslecting radio");
        stopPtt();
        deselectRadios();
    }
    // Send reset command to radio
    console.info("Sending reset command");
    radios[idx].wsConn.send(JSON.stringify(
        {
            "radio": {
                "command": "reset"
            }
        }
    ));
    // Disconnect and reconnect
    disconnectRadio(idx);
    setTimeout(() => {
        connectRadio(idx);
    }, 500);
}

/***********************************************************************************
    Extension Websocket Functions
***********************************************************************************/

function extensionConnect() {
    // Disconnect if connected
    if (extensionWs) {
        extensionWs.close();
        return;
    }
    // Prepare URL
    const wsUrl = `ws://${config.Extension.address}:${config.Extension.port}`;
    // Verify valid address
    try {
        const url = new URL(wsUrl);
    }
    catch (_)
    {
        alert("Invalid extension URL, cannot open connection!");
        return;
    }
    // Create the connection
    extensionWs = new WebSocket(wsUrl);
    // Create websocket
    extensionWs.onerror = function(event) { handleExtensionError(event) };
    extensionWs.onmessage = function(event) { recvExtensionMessage(event) };
    extensionWs.onclose = function(event) { handleExtensionClose(event) };
    // Wait for active
    waitForWebSockets([extensionWs], extensionConnected);
}

function extensionConnected() {
    $("#extension-status").removeClass("disconnected");
    $("#extension-status").html("Connected");
    $("#extension-status").addClass("connected");
    $("#connect-extension").html("Disconnect");
    // Navbar icon
    $("#navbar-ext").removeClass("disconnected");
    $("#navbar-ext").addClass("connected");
}

function handleExtensionError(event) {
    console.error(`Got extension socket error: ${event}`);
}

function recvExtensionMessage(event) {
    // Convert to JSON
    var msgObj;
    try {
        msgObj = JSON.parse(event.data);
        console.debug(msgObj);
    } catch (e) {
        console.warn(`Got invalid data from extension websocket: ` + event.data);
        console.warn(e);
        return;
    }

    // Iterate through each message and its data (normally we'd only get one at a time, but I suppose you could get more than one)
    for (const [key, value] of Object.entries(msgObj)) {
        // Handle message data based on key type
        switch (key) {
            // Radio status update
            case "selRadio":
                selectRadio(`radio${value}`);
                break;
            // Key radio
            case "keyRadio":
                if (!pttActive) {
                    // Select the radio if it isn't
                    if (selectedRadioIdx != value) {
                        selectRadio(`radio${value}`);
                    }
                    // Start PTT
                    startPtt(true);
                }
                // Handle alert tone override
                else if (pttActive && selectedRadioIdx == value && alertTonesInProgress) {
                    startPtt(false);
                }
                // Bonk otherwise (presumably another radio is PTTing)
                else {
                    bonk();
                }
                break;
            // Dekey radio
            case "dekeyRadio":
                stopPtt();
                break;
            // Press softkey
            case "pressSoftkey":
                pressSoftkey(parseInt(value)+1);
                break;
            // Release softkey
            case "releaseSoftkey":
                releaseSoftkey(parseInt(value)+1);
                break;
            // Channel command
            case "channel":
                switch (value) {
                    case "up":
                        changeChannel(false);
                        break;
                    case "down":
                        changeChannel(true);
                        break;
                }
                break;
            // Volume command
            case "volume":
                switch (value) {
                    case "up":
                        changeVolume(10);
                        break;
                    case "down":
                        changeVolume(-10);
                        break;
                    case "mute":
                        if (selectedRadioIdx != null) {
                            toggleMute(selectedRadioIdx);
                        }
                        break;
                }
                break;
            // Fallback
            default:
                console.warn(`Unknown message from extension: ${JSON.stringify(msgObj)}`);
                break;
        }
    }
}

function handleExtensionClose(event) {
    $("#extension-status").removeClass("connected");
    $("#extension-status").html("Disconnected");
    $("#extension-status").addClass("disconnected");
    $("#connect-extension").html("Connect");
    // Navbar icon
    $("#navbar-ext").removeClass("connected");
    $("#navbar-ext").addClass("disconnected");
    extensionWs = null;
}

function exUpdateRadio(idx) {
    if (extensionWs) {
        if (extensionWs.readyState == WebSocket.OPEN) {
            obj = {
                radioIdx: idx,
                status: radios[idx].status
            };
            extensionWs.send(JSON.stringify(obj));
        }
    }
}

function exUpdateSelected() {
    if (extensionWs) {
        if (extensionWs.readyState == WebSocket.OPEN) {
            obj = {
                selRadioIdx: selectedRadioIdx
            }
            extensionWs.send(JSON.stringify(obj));
        }
    }
}

/**
 * Sends the status of the six softkeys to the extension
 * @param {bool[6]} states 
 */
function exUpdateSoftkeys(states) {
    if (extensionWs) {
        if (extensionWs.readyState == WebSocket.OPEN) {
            obj = {
                softkeys: states
            }
            extensionWs.send(JSON.stringify(obj));
        }
    }
}

/***********************************************************************************
    Utility Functions
***********************************************************************************/

/**
 * Escape a string to regex?
 * 
 * This was also stolen from the aiortc example. Not exactly sure what it does yet
 * @param {string} string string to escape
 * @returns {string} replaced string
 */
function escapeRegExp(string) {
    return string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); // $& means the whole matched string
}

/**
 * Converts a voltage gain value to the equivalent constant multiplier
 * @param {float} db Gain in decibels
 * @returns gain as a factor relative to 1
 */
function dbToGain(db) {
    return Math.pow(10, db/20).toFixed(3);
}
