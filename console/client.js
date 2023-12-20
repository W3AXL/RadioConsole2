/***********************************************************************************
    Global Variables
***********************************************************************************/

var version = "2.1.0";

// Local user config variables (saved to cookie)
var config = {
    timeFormat: "Local",

    tpt: "mot",
    btnSounds: true,

    audio: {
        rxAgc: false,
        unselectedVol: -3.0,
        input: "default",
        output: "default"
    },
    
    serverAddress: "",
    serverPort: 0,
    serverAutoConn: false,

    extensionAddress: "",
    extensionPort: 0
}

// Radio List (read from radio config initially and populated with audio sources/sinks and rtc connections)
var radios = [];

// Audio variables
var audio = {
    // Audio context
    context: null,
    // Flag for whether we've done initial setup
    running: false,
    // DTMF generator
    dtmf: null,
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

// WebRTC Variables
const rtcConf = {
    // Audio codec
    bitrate: 16000,
    codec: "opus/48000/2",  // stereo opus
    //codec: "PCMU/8000",
    
    // Base audio encoding/decoding latency. This is added to the current webRTC round trip time when audio functions are called so that actions align with the audio
    // This is found experimentally and varies slightly depending on daemon system performance, but ~350ms is about right for most of the tested codecs
    rxBaseLatency: 350,
    txBaseLatency: 350,
    // RTT (round-trip time) parameters for RTC connection
    rttLimit: 0.25,
    rttSize: 25,
    
    // Whether to disable FEC and enable CBR (this actually causes more latency annoyingly)
    cbr: false
}

// Flag to start/stop animation callback for audio meters (to save CPU when we're not doing anything)
var audio_playing = false;

testInput = null;

// Radio Card Tempalte
const radioCardTemplate = document.querySelector('#card-template');

// Alert Dialog Template
const alertTemplate = document.querySelector("#alert-dialog-template");

// Radio JSON validation
const validColors = ["red","amber","green","blue","purple"];

// Extension websocket connection
var extensionWs = null;

/***********************************************************************************
    State variables
***********************************************************************************/

// Detected timezone
timeZone = "";
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

/**
 * Page load function. Starts timers, etc.
 */
function pageLoad() {
    console.log("Starting client runtime");

    // Enable JQuery Tooltips
    //$( document ).tooltip();

    // Populate version
    $("#navbar-version").html(version);

    // Query media devices
    getAudioDevices();

    // Query radio config from client.json
    readRadioConfig();

    // Read user config from cookie
    readUserConfig();

    // Get client timezone
    const d = new Date();
    timeZone = d.toLocaleString('en', { timeZoneName: 'short' }).split(' ').pop();

    // Setup clock timer
    setInterval(updateClock, 100);

    // If autoconnect is specified, autoconnect!
    if (config.autoconnect) {
        connectAllButton();
    }

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
    $(`#radio${idx} .icon-connect`).parent().prop('title','Connected, OK');
    // Update master connect/disconnect button
    $(`#navbar-connect`).removeClass('disconnected');
    $(`#navbar-connect`).addClass('connected');
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
            startPtt(true);
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
    // Add a card for each radio in the list
    radios.forEach((radio, index) => {
        console.log("Adding radio " + radio.name);
        // Add the radio card
        addRadioCard("radio" + String(index), radio.name, radio.color);
        // Populate its text
        updateRadioCard(index);
    });
    // Bind the cards
    bindRadioCardButtons();
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
 * @param {string} id ID of the card element
 * @param {string} name Name to display in header
 */
function addRadioCard(id, name, color) {
    // New, much easier way to add new cards
    var newCard = radioCardTemplate.content.cloneNode(true);
    newCard.querySelector(".radio-card").classList.add(color);
    newCard.querySelector(".radio-card").id = id;
    newCard.querySelector(".radio-card .header h2").textContent = name;

    $("#main-layout").append(newCard);
}

function stopClick(event, obj) {
    event.stopPropagation();
    event.preventDefault();
}

/**
 * Binds radio card buttons & click events to functions
 */
function bindRadioCardButtons() {
    // Bind clicking of the card to selection of a radio
    $(".radio-card").on('click', function (event) {
        // Prevent continual propagation
        event.stopPropagation();
        event.stopImmediatePropagation();
        // Get the ID of the selecting item
        var cardId = $(this).attr('id');
        // Select the radio
        selectRadio(cardId);
    })
    // Bind the minimize button
    $(".minimize-radio-card").click(function (event) {

    })
}

function updateRadioCard(idx) {
    // Get radio from radioList
    var radio = radios[idx];

    // Get card object
    var radioCard = $("#radio" + String(idx));

    // Update card name & description
    radioCard.find(".radio-name").html(radio.status.Name);
    radioCard.find(".radio-name").attr("title", radio.status.Description);

    // Limit zone & channel text to 27/18 characters
    // TODO: figure out dynamic scaling of channel/zone text so we don't have to do this
    if (radio.status.ZoneName != null) {
        const shortZone = radio.status.ZoneName.substring(0,28);
        radioCard.find("#zone-text").html(shortZone);
    }

    if (radio.status.ChannelName != null) {
        const shortChan = radio.status.ChannelName.substring(0,19);
        radioCard.find("#channel-text").html(shortChan);
    }
    
    // Remove all current classes
    setTimeout(function() {
        radioCard.removeClass("transmitting");
    }, radios[idx].rtc.txLatency);
    setTimeout(function () {
        radioCard.removeClass("receiving");
    }, radios[idx].rtc.rxLatency);
    radioCard.removeClass("disconnected");

    // Update radio state
    switch (radio.status.State) {
        case "Transmitting":
            setTimeout(function() {
                radioCard.addClass("transmitting");
                // Check audio meter state
                checkAudioMeterCallback();
            }, radios[idx].rtc.txLatency);
            break;
        case "Receiving":
            setTimeout(function() {
                radioCard.addClass("receiving");
                // Check audio meter state
                checkAudioMeterCallback();
            }, radios[idx].rtc.rxLatency); // used to unmute after latency delay but this makes sure we don't miss anything
            break;
        case "Disconnected":
            radioCard.addClass("disconnected");
            break;
    }

    // Update mute icon
    if (radio.status.muted) {
        radioCard.find("#icon-mute").attr('name', 'volume-mute-sharp');
        radioCard.find("#icon-mute").addClass("muted");
    } else {
        radioCard.find("#icon-mute").attr('name', 'volume-high-sharp');
        radioCard.find("#icon-mute").removeClass("muted");
    }

    // Update alert icon
    if (radio.status.Error) {
        radioCard.find("#icon-alert").addClass("alerting");
    } else {
        radioCard.find("#icon-alert").removeClass("alerting");
    }

    // Update Scan Icon
    radioCard.find('.scan-icons').removeClass("scanning");
    radioCard.find('.scan-icons').removeClass("priority");
    radioCard.find('.scan-icons').removeClass("priority2");
    switch (radio.status.ScanState) {
        case "Priority2":
            console.debug("Got priority 2 status");
            radioCard.find('.scan-icons').addClass("priority2");
            break;
        case "Priority1":
            console.debug("Got priority 1 status");
            radioCard.find('.scan-icons').addClass("priority");
            break;
        case "Scanning":
            console.debug("Got scanning status");
            radioCard.find('.scan-icons').addClass("scanning");
            break;
        default:
            console.debug("Radio not scanning");
            break;
    }

    // Update pan from config (which in turn is set when you adjust the pan slider)
    radioCard.find('.radio-pan').attr('value', radios[idx].pan);
}

/**
 * Update the bottom control bar based on the selected radio
 */
function updateRadioControls() {
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
                    break;
                case "Off":
                    $(`#softkey${index+1}`).removeClass("pressed");
                    break;
            }
        });
        exUpdateSoftkeys(curSoftkeyPage);
        // Clear if we don't
    } else {
        for (i=0; i<6; i++) {
            $(`#softkey${i+1} .btn-text`).html("");
        }
        // Disable softkeys
        $("#radio-controls .btn").addClass("disabled");
        $("#radio-controls .btn").removeClass("pressed");
        // Clear softkey states on extension
        exUpdateSoftkeys([false, false, false, false, false, false]);
    }
    exUpdateSoftkeys();
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
 * Start radio PTT
 */
function startPtt(micActive) {
    if (!pttActive && selectedRadio) {
        // Only send the TX command and unmute the mic if we have a valid socket
        if (radios[selectedRadioIdx].wsConn) {
            console.log("Starting PTT on " + selectedRadio);
            pttActive = true;
            // Unmute mic after timeout, if requested
            if (micActive) {setTimeout( unmuteMic, audio.micUnmuteDelay);}
            // Play TPT
            playSound("sound-ptt");
            // Send radio keyup after latency timeout
            setTimeout( function() {
                radios[selectedRadioIdx].wsConn.send(JSON.stringify(
                    {
                        "radio": {
                            "command": "startTx"
                        }
                    }
                ));
            }, radios[selectedRadioIdx].rtc.txLatency);
        }
    } else if (!pttActive && !selectedRadio) {
        pttActive = true;
        console.log("No radio selected, ignoring PTT");
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
                playSound("sound-ptt-end");
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
            if (config.btnSounds) {
                playSound("sound-click");
            }
            console.log("Changing channel down on " + selectedRadio);
            radios[selectedRadioIdx].wsConn.send(JSON.stringify(
                {
                    "radio": {
                        "command": "chanDn"
                    }
                }
            ));
        } else {
            if (config.btnSounds) {
                playSound("sound-click");
            }
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
    if (config.btnSounds) {
        playSound("sound-click");
    }
    var releasedKey = radios[selectedRadioIdx].status.Softkeys.slice(6*softkeyPage, 6+(6*softkeyPage))[idx-1];
    console.debug("Mapped pressed softkey to softkey:" + releasedKey.Name);
    releaseButton(releasedKey.Name);
}

/**
 * Left arrow button decrements the softkey page
 */
function button_left() {
    if (config.btnSounds) {
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
    if (config.btnSounds) {
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
    setTimeout(() => {
        console.debug(`Fallback button release handler for stuck button ${buttonName}`);
        releaseButton(buttonName);
    }, 1500);
}

function releaseButton(buttonName) {
    if (!pttActive && selectedRadio && radios[selectedRadioIdx].wsConn) {
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
        }, startTime + ((digitTime + delayTime) * number.length) + rtcConf.txBaseLatency);
    }
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
    var timestr = "HH:mm:ss"
    if (config.timeFormat == "Local") {
        var time = getTimeLocal(timestr);
        $("#clock").html(time + " " + timeZone);
    } else if (config.timeFormat == "UTC") {
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

/**
 * Save the server config input
 */
function saveDaemonConfig() {
    // Disconnect from existing server

    // Get values
    const autoconnect = $("#daemon-autoconnect").prop('checked');

    // Save config info
    config.serverAutoConn = autoconnect;

    // Save config to cookie
    saveUserConfig();
}

/**
 * Save the client config
 */
function saveClientConfig() {
    // Get values
    const timeFormat = $("#client-timeformat").val();
    const rxAgc = $("#client-rxagc").is(":checked");
    const unselectedVol = $("#unselected-vol").val();
    const toneVol = $("#tone-vol").val();

    // Set config
    config.timeFormat = timeFormat;
    config.audio.rxAgc = rxAgc;
    config.audio.unselectedVol = parseFloat(unselectedVol);
    config.audio.toneVol = parseFloat(toneVol);

    // Save config to cookie
    saveUserConfig();

    // Update radio audio
    if (audio.context) {
        updateRadioAudio();
    }

    // Update tone audio
    $('#sound-ptt').prop("volume", dbToGain(config.audio.toneVol));
    $('#sound-ptt-end').prop("volume", dbToGain(config.audio.toneVol));
    $('#sound-click').prop("volume", dbToGain(config.audio.toneVol));
}

/**
 * Save the extension config
 */
function saveExtensionConfig() {
    // Get values
    const extensionAddress = $("#extension-address").val();
    const extensionPort = $("#extension-port").val();

    // Set
    config.extensionAddress = extensionAddress;
    config.extensionPort = parseInt(extensionPort);

    // Save
    saveUserConfig();
}

/**
 * Save config to cookie, as JSON
 */
function saveUserConfig() {
    // Convert config object to cookie
    configJson = JSON.stringify(config);
    console.log("Saving config json: " + configJson);
    // Save to cookie
    Cookies.set('config',configJson);
}

/**
 * Read config from cookie
 */
function readUserConfig() {
    // Read config from cookie
    configJson = Cookies.get('config');
    // Only try and parse if we have a stored config cookie
    if (configJson) {
        // Convert to config object
        config = JSON.parse(configJson);
        // Update server popup values
        $("#daemon-autoconnect").prop('checked',config.daemonAutoConn);
        // Update client popup values
        $("#client-timeformat").val(config.timeFormat);
        $("#client-rxagc").prop("checked", config.audio.rxAgc);
        $(`#unselected-vol option[value=${config.audio.unselectedVol}]`).attr('selected', 'selected');
        // Update extension popup values
        $("#extension-address").val(config.extensionAddress);
        $("#extension-port").val(config.extensionPort);
        // Tone volume elements
        $(`#tone-vol option[value=${config.audio.toneVol}]`).attr('selected', 'selected');
        $('#sound-ptt').prop("volume", dbToGain(config.audio.toneVol));
        $('#sound-ptt-end').prop("volume", dbToGain(config.audio.toneVol));
        $('#sound-click').prop("volume", dbToGain(config.audio.toneVol));
    } else {
        console.warn("No config cookie detected, using defaults");
    }
}

/**
 * Clears out config from cookies
 */
function clearConfig() {
    Cookies.remove('config');
}

/***********************************************************************************
    Radio Config Reading
***********************************************************************************/

function readRadioConfig() {
    // Read the json config file from the server
    var radioJson = [];
    $.ajax({
        type: 'GET',
        url: './radios.json',
        dataType: 'json',
        success: gotRadioConfig,
        error: function(obj, status, error) {alert(`Failed to read radios.json: ${error}`);console.error(`${status}: ${error}`);},
        async: true,
        timeout: 100
    });
}

function gotRadioConfig(data, status, obj) {
    console.debug(`Successfully read radios.json, status: ${status}`);
    radioJson = data;
    console.debug(radioJson['RadioList']);
    radios = radioJson['RadioList'];
    // Populate default values (this map function adds the key,value pairs to every item in the list)
    radios = radios.map(v => ({
        ...v,
        status: {
            State: 'Disconnected'
        },
        rtc: {},
        wsConn: null,
        audioSrc: null,
    }));

    // Validate Input
    radios.forEach((radio, idx) => {
        // Validate Color
        if (!validColors.includes(radios[idx].color)) {
            console.warn(`Color ${radios[idx].color} not valid, defaulting to blue`);
            radios[idx].color = "blue";
        }
        // Default pan
        if (!Object.hasOwn(radios[idx], 'pan')) {
            console.debug(`Radio ${idx} has no pan property, defaulting to 0`);
            radios[idx].pan = 0;
        }
        // Default name (used for logging until we get the proper name)
        radios[idx].name = radios[idx].address + ":" + radios[idx].port;
    });

    // Populate radio cards
    populateRadios();

    // Connect and load if autoconnect is true
    if (config.serverAutoConn) {
        //connect()
    }
}

/***********************************************************************************
    WebRTC Functions

    These are adapted/borrowed from:
    https://github.com/webrtc/samples/tree/gh-pages/src/content/peerconnection/audio
***********************************************************************************/

function dummyTrack() {
    osc = audio.context.createOscillator();
    dst = osc.connect(audio.context.createMediaStreamDestination());
    osc.start();
    return Object.assign(dst.stream.getAudioTracks()[0], {enabled: false});
}

/**
 * Initiate WebRTC connection with server
 * @param {int} idx index of radio in radios[]
 * @returns {boolean} true if connection starts successfully
 */
function startWebRtc(idx) {
    console.log(`Starting WebRTC session for ${radios[idx].name}`);
    
    // Find the right getUserMedia()
    // This isn't needed with the new method below
    /*if (!navigator.getUserMedia) {
        navigator.getUserMedia = navigator.getUserMedia || navigator.webkitGetUserMedia || navigator.mozGetUserMedia || navigator.msGetUserMedia;
    }*/
    // Check if the input track has ended, and restart if so

    if (!audio.running) {
        console.warn(`Waiting for mic services to be running for radio ${idx}`);
        setTimeout(startWebRtc, 100, idx);
    } else {
        // Restart mic track if needed
        if (!audio.inputDest.stream.active) {
            console.warn(`Mic stream was inactive, restarting`);
            restartMicTrack();
        }
        // Create peer
        radios[idx].rtc.peer = createPeerConnection(idx);
        if (radios[idx].rtc.peer) {
            console.log("Created peer connection");
        } else {
            console.error("Failed to create peer connection");
            return false
        }
        // Connect track
        console.log(`Adding mic track to radio ${idx}`);
        radios[idx].rtc.peer.addTrack(audio.inputTrack);
    }
}

/**
 * Stop RTC session for radio at index
 * @param {int} idx index of radio in radios[]
 * @returns 
 */
function stopWebRtc(idx) {
    // Return if there was never a peer connection to begin with
    console.debug(`Stopping RTC for radio ${idx}`);
    if (!radios[idx].rtc.hasOwnProperty('peer')) {
        console.log("No peer connection created");
        return
    }

    // Return if stuff is already closed
    if (radios[idx].rtc.peer.connectionState == "closed") {
        console.log("RTC peer connection already closed");
        return
    }

    // Close any local audio
    /*radios[idx].rtc.peer.getSenders().forEach(function(sender, idx) {
        console.debug(`Stopping RTC sender ${idx}`);
        sender.track.stop();
    });*/

    // Close any active peer transceivers
    if (radios[idx].rtc.peer.getTransceivers) {
        radios[idx].rtc.peer.getTransceivers().forEach(function(tx, idx) {
            console.debug(`Stopping RTC txcvr ${idx}`);
            if (tx.stop) {
                tx.stop();
            }
        })
    }

    // Close the peer connection
    console.log("Closing peer connection");
    radios[idx].rtc.peer.close();

    // Reset audio routing
    radios[idx].audioSrc = null;
}

/**
 * Create a new WebRTC peer connection
 * @param {int} idx the index of the radio in radios[]
 * @returns {RTCPeerConnection} the created peer connection object
 */
function createPeerConnection(idx) {

    // Create config object
    var rtcConfig = {
        sdpSemantics: 'unified-plan',
        iceServers: [],
    };

    // Create peer
    var peer = new RTCPeerConnection(rtcConfig);

    // Register event listeners for debug
    peer.addEventListener('icegatheringstatechange', function() {
        console.log(`new peer iceGatheringState for radio ${radios[idx].name}: ${peer.iceGatheringState}`);
    }, false);

    peer.addEventListener('iceconnectionstatechange', function() {
        console.log(`new peer iceConnectionState for radio ${radios[idx].name}: ${peer.iceConnectionState}`);
        if (peer.iceConnectionState == "connected") {
            // update UI
            radioConnected(idx);
            // Create array for averaging roundTripTime
            radios[idx].rtc.rttArray = new Array(rtcConf.rttSize).fill(0);
            // Start monitoring roundTripTime
            checkRoundTripTime(idx);
        } else if (peer.iceConnectionState == "failed") {
            // Disconnect the client if we had an error (for now, maybe auto-reconnect later?)
            console.error(`WebRTC ICE connection failed for radio ${radios[idx].name}`);
            stopWebRtc(idx);
            radios[idx].wsConn.close();
        } else if (peer.iceConnectionState == "disconnected") {
            console.error(`WebRTC ICE connection disconnected for radio ${radios[idx].name}`);
            stopWebRtc(idx);
            if (radios[idx].wsConn) {
                radios[idx].wsConn.close();
            }
        }
    }, false);

    peer.addEventListener('signalingstatechange', function() {
        console.log(`new peer signallingState for radio ${radios[idx].name}: ${peer.signalingState}`);
    })

    peer.addEventListener('connectionstatechange', function() {
        console.log(`new peer connectionState for radio ${radios[idx].name}: ${peer.connectionState}`);
        if (peer.connectionState === "connecting") {
            $(`#radio${idx} .icon-connect`).removeClass('connected');
            $(`#radio${idx} .icon-connect`).removeClass('disconnected');
            $(`#radio${idx} .icon-connect`).addClass('connecting');
            $(`#radio${idx} .icon-connect`).parent().prop('title','WebRTC peer connecting');
        }
    })

    // This fires when we restart ICE candidates due to exceeding RTT or restarting the mic track
    peer.addEventListener('negotiationneeded', function() {
        console.warn(`WebRTC ICE negotiation needed for radio ${radios[idx].name}`);
        // Create and send a new RTC offer for the radio
        createRtcOffer(idx);
    })

    // Print initial states
    console.log(`new peer iceGatheringState for radio ${radios[idx].name}: ${peer.iceGatheringState}`);
    console.log(`new peer iceConnectionState for radio ${radios[idx].name}: ${peer.iceConnectionState}`);
    console.log(`new peer signallingState for radio ${radios[idx].name}: ${peer.signalingState}`);

    // Connect audio stream from peer to the web audio objects
    peer.addEventListener('track', function(event) {
        if (event.track.kind == 'audio') {
            console.debug(`New ontrack event for radio ${radios[idx].name}:`);
            console.debug(event);
            console.log(`Got new audio track from server for radio ${radios[idx].name}`);

            // Create a new MediaStream from the track we want
            var newStream = new MediaStream( [event.track ]);

            // Create a dummy stream element (chrome bug means the stream won't play if you don't do this)
            var newDummy = new Audio();
            newDummy.muted = true;
            newDummy.srcObject = newStream;
            newDummy.play();
            audio.dummyOutputs.push(newDummy);
            console.debug(`Started dummy audio element for radio ${radios[idx].name}`);

            // If we already created the audiosrc, don't do it again. Just reconnect the new audio
            if (radios[idx].audioSrc) {
                console.log(`Reconnecting audio nodes to audio source for radio ${idx}`);
                // Create the new audio source node
                var newAudioNode = audio.context.createMediaStreamSource(newStream);
                radios[idx].audioSrc.audioNode = newAudioNode;
                // Reconnect it
                radios[idx].audioSrc.audioNode.connect(radios[idx].audioSrc.filterNode);
            // Set up the new audio source
            } else {
                console.log(`Creating new audio source for radio ${idx}`);
                // Create audio source from the track and put it in an object with a local gain node
                var newSource = {
                    audioNode: audio.context.createMediaStreamSource(newStream),
                    filterNode: audio.context.createBiquadFilter(),
                    agcNode: audio.context.createDynamicsCompressor(),
                    makeupNode: audio.context.createGain(),
                    gainNode: audio.context.createGain(),
                    muteNode: audio.context.createGain(),
                    panNode: audio.context.createStereoPanner(),
                    analyzerNode: audio.context.createAnalyser(),
                    leftSpkr: true,
                    rightSpkr: true
                }
                // Create this afterwards because we need the value from the above node
                newSource.analyzerData = new Float32Array(newSource.analyzerNode.fftSize);

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

                // Update radio connections
                newSource.audioNode.connect(newSource.filterNode);
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

            // Update the radio audio
            updateRadioAudio();
        }
    })

    // Return the new peer object
    return peer;
}

/**
 * Creates and sends a new RTC SDP offer to the radio daemon
 * @param {int} idx index of radio
 * @returns true if successful, false otherwise
 */
function createRtcOffer(idx) {
    // Generate the SDP offer and set the local description
    /*radios[idx].rtc.peer.createOffer().then((offer) => {
        radios[idx].rtc.peer.setLocalDescription(offer);
    }).then(() => {
        // Once we've set the local description, wait for ICE gathering to complete
        console.debug("Waiting for icegatheringstate complete");
        return new Promise(function(resolve) {
            if (radios[idx].rtc.peer.iceGatheringState === 'complete') {
                console.debug("...done!")
                resolve();
            } else {
                function checkState() {
                    if (radios[idx].rtc.peer.iceGatheringState === 'complete') {
                        radios[idx].rtc.peer.removeEventListener('icegatheringstatechange', checkState);
                        console.debug("...done!")
                        resolve();
                    }
                }
                radios[idx].rtc.peer.addEventListener('icegatheringstatechange', checkState);
            }
        });
    }).then(() => {
        // Modify the offer for the codec we want to use (basically just filters available RTC codecs for the one we're looking for)
        radios[idx].rtc.peer.localDescription.sdp = sdpFilterCodec('audio', rtcConf.codec, rtcConf.bitrate, radios[idx].rtc.peer.localDescription.sdp);
        // Send the offer to the server via WebSocket
        console.debug("Sending local description to daemon");
        console.debug(radios[idx].rtc.peer.localDescription.sdp);
        radios[idx].wsRtc.send(JSON.stringify(radios[idx].rtc.peer.localDescription));
    }).catch(function(e) {
        console.error(`"Got exception during RTC offer creation: \n${e}`);
        return false;
    });*/
    // ICE candidate handler
    radios[idx].rtc.peer.onicecandidate = (event) => {
        event.candidate && radios[idx].wsRtc.send(JSON.stringify(event.candidate));
    }
    // Return true if nothing bad happened
    return true;
}

function handleRtcWsMsg(event, idx)
{
    console.debug(`Radio ${idx} Got WebRTC WS message: ${event.data}`);
    var obj = JSON.parse(event.data);
    if (obj?.candidate) {
        console.debug("Got WebRTC ICE Candidate");
        radios[idx].rtc.peer.addIceCandidate(obj);
    }
    else if (obj?.sdp) {
        console.debug("Got WebRTC remote description");
        radios[idx].rtc.peer.setRemoteDescription(new RTCSessionDescription(obj));
        radios[idx].rtc.peer.createAnswer()
            .then((answer) => radios[idx].rtc.peer.setLocalDescription(answer))
            .then(() => radios[idx].wsRtc.send(JSON.stringify(radios[idx].rtc.peer.localDescription)));
    }
}

function handleRtcWsError(event, idx)
{
    console.error(`Radio ${idx} WebRTC WS got error!`);
    console.error(event.data);
}

function handleRtcWsClose(event, idx)
{
    console.warn(`Radio ${idx} WebRTC websocket closed!`);
    disconnectRadio(idx);
}

/**
 * Take the SDP response from the server and configure the peer
 * @param {int} idx index of radio in radios[]
 * @param {string} answerType SDP type
 * @param {string} answerSdp SDP
 */
function gotRtcResponse(idx, answerType, answerSdp) {
    console.log("Got WebRTC response from server");
    var answer = {
        type: answerType,
        sdp: answerSdp
    }
    radios[idx].rtc.peer.setRemoteDescription(answer);
}

/**
 * Find an SDP based on the specified codec
 * 
 * This was stolen directly from the aiortc example
 * 
 * @param {string} kind 'audio' or 'video'
 * @param {string} codec specific codec descriptor
 * @param {*} realSdp existing SDP
 * @param {int} bitrate codec bitrate to use
 * @returns new SDP using specified codec
 */
function sdpFilterCodec(kind, codec, bitrate, realSdp) {
    var allowed = []
    var rtxRegex = new RegExp('a=fmtp:(\\d+) apt=(\\d+)\r$');
    var codecRegex = new RegExp('a=rtpmap:([0-9]+) ' + escapeRegExp(codec))
    var videoRegex = new RegExp('(m=' + kind + ' .*?)( ([0-9]+))*\\s*$')
    
    console.debug("Starting with SDP:\n" + realSdp);

    var lines = realSdp.split('\n');

    var isKind = false;
    for (var i = 0; i < lines.length; i++) {
        if (lines[i].startsWith('m=' + kind + ' ')) {
            isKind = true;
        } else if (lines[i].startsWith('m=')) {
            isKind = false;
        }

        if (isKind) {
            var match = lines[i].match(codecRegex);
            if (match) {
                allowed.push(parseInt(match[1]));
            }

            match = lines[i].match(rtxRegex);
            if (match && allowed.includes(parseInt(match[2]))) {
                allowed.push(parseInt(match[1]));
            }
        }
    }

    var skipRegex = 'a=(fmtp|rtcp-fb|rtpmap):([0-9]+)';
    var sdp = '';

    isKind = false;
    for (var i = 0; i < lines.length; i++) {
        if (lines[i].startsWith('m=' + kind + ' ')) {
            isKind = true;
        } else if (lines[i].startsWith('m=')) {
            isKind = false;
        }

        if (isKind) {
            var skipMatch = lines[i].match(skipRegex);
            if (skipMatch && !allowed.includes(parseInt(skipMatch[2]))) {
                continue;
            } else if (lines[i].match(videoRegex)) {
                sdp += lines[i].replace(videoRegex, '$1 ' + allowed.join(' ')) + '\n';
            } else {
                sdp += lines[i] + '\n';
            }
        } else {
            sdp += lines[i] + '\n';
        }
    }

    // Get fmtp line for replacement
    var rx = /a=fmtp:.*/g;
    var fmtpLine = rx.exec(sdp);
    // Append bitrate info to SDP
    if (bitrate != null)
    {
        sdp = sdp.replace(fmtpLine,`${fmtpLine};maxplaybackrate=${bitrate};sprop-maxcapturerate=${bitrate};stereo=0`);
    }
    // Replace FEC with CBR if we want to
    if (rtcConf.cbr) {
        console.debug("CBR enabled, replacing FEC line in SDP");
        sdp = sdp.replace('useinbandfec=1','cbr=1');
    }

    return sdp;
}

/**
 * Check the roundtrip time for the current radio and handle accordingly
 * @param {int} idx radio index
 */
function checkRoundTripTime(idx) {
    if (radios[idx].rtc.peer != undefined && radios[idx].rtc.peer.iceConnectionState === 'connected') {
        // Get the ice transport and the stats
        radios[idx].rtc.peer.getStats(null).then((stats) => {
            // Iterate over each stats looking for the candidate pair stats
            stats.forEach((report) => {
                if (report && report.type === "candidate-pair" && report.state === "succeeded") {
                    //console.debug(`Got new candidate stats for radio ${idx}`, report)
                    // Shift the rtt array with the new rtt value
                    radios[idx].rtc.rttArray.shift();
                    radios[idx].rtc.rttArray.push(report.currentRoundTripTime);
                    // Get the current average of all 10
                    radios[idx].rtc.rttAvg = (radios[idx].rtc.rttArray.reduce((a ,b) => a + b) / rtcConf.rttSize).toFixed(3);
                    // Update the radio latency parameters
                    radios[idx].rtc.txLatency = rtcConf.txBaseLatency + (radios[idx].rtc.rttAvg * 1000);
                    radios[idx].rtc.rxLatency = rtcConf.rxBaseLatency + (radios[idx].rtc.rttAvg * 1000);
                    //console.debug(`Current RTT average for radio ${idx}: ${rttAvg}`);
                    // If we're above the threshold, throw a disconnect warning
                    if (radios[idx].rtc.rttAvg > rtcConf.rttLimit) {
                        console.error(`WebRTC round trip time (${radios[idx].rtc.rttAvg}) exceeded limit (${rtcConf.rttLimit}) for radio ${idx}, restarting ICE`);
                        disconnectRadio(idx);
                        setTimeout(() => {
                            connectRadio(idx);
                        }, 500);
                        //stopWebRtc(idx);
                        //startWebRtc(idx);
                        // restarting ICE didn't seem to fix latency
                        //radios[idx].rtc.peer.restartIce();
                    }
                }
            })
            setTimeout(function() {
                checkRoundTripTime(idx)
            }, 1000);
        })
    } else {
        console.warn(`Peer connection closed, stopping RTT monitoring for radio ${idx}`);
        return;
    }
}

/***********************************************************************************
    Audio Handling Functions
***********************************************************************************/

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
    audio.outputGain.gain.value = 0.75;
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
            // Create a MediaStreamDestination for sending to the WebRTC peer
            audio.inputDest = audio.context.createMediaStreamDestination();
            // Connect input mic stream to gain, and gain to destination and analyzer
            audio.inputStream.connect(audio.inputMicGain);
            audio.inputMicGain.connect(audio.inputDest);
            audio.inputMicGain.connect(audio.inputAnalyzer);
            // Setup DTMF generator once we have our input audio nodes
            audio.dtmf = new DualTone(audio.context, 100, 200);
            // Add the first available mic track to the peer connection, this will call the onnegotiationneeded handler which will send a new SDP offer
            audio.inputTrack = audio.inputDest.stream.getTracks()[0];
            // Add a listener to restart the track when it ends (happens sometimes)
            audio.inputTrack.addEventListener("ended", (event) => {
                console.warn(`Mic input track ended!`);
                restartMicTrack();
            });
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
    //audio.inputMicGain.gain.value = 0;
}

function unmuteMic() {
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

function restartMicTrack() {
    console.warn(`Restarting mic input track...`);
    // Recreate the MediaStreamDestination for sending to the WebRTC peer
    audio.inputDest = audio.context.createMediaStreamDestination();
    // Reconnect input mic stream to gain, and gain to destination and analyzer
    audio.inputStream.connect(audio.inputMicGain);
    audio.inputMicGain.connect(audio.inputDest);
    audio.inputMicGain.connect(audio.inputAnalyzer);
    // Add the first available mic track to the peer connection, this will call the onnegotiationneeded handler which will send a new SDP offer
    audio.inputTrack = audio.inputDest.stream.getTracks()[0];
    // Add a listener to restart the track when it ends (happens sometimes)
    audio.inputTrack.addEventListener("ended", (event) => {
        console.warn(`Mic input track ended!`);
        restartMicTrack();
    });
    console.info("Done!");
}

function reconnectRadioMicTrack(idx) {
    console.log(`Reconnecting audio track for radio ${idx}`);
    const sender = radios[idx].rtc.peer.getSenders()[0];
    if (sender && radios[idx].rtc.peer.connectionState == 'connected') {
        // If the sender still exists, just replace the track
        console.log("Sender still alive, replacing track");
        sender.replaceTrack(audio.inputTrack);
    } else {
        // Add a new track if the sender died
        console.warn("Sender dead, adding new track");
        radios[idx].rtc.peer.addTrack(audio.inputTrack);
    }
}

/**
 * Updates audio meters based on current data. Only called after both mic & speaker are set up and running (otherwise errors)
 */
function audioMeterCallback() {
    // Stop if we're told to
    if (!audio_playing) {
        return;
    }

    // Draw stuff
    radios.forEach((radio, idx) => {
        // Ignore radios with no connected audio
        if (radios[idx].audioSrc == null) {
            if ($(`.radio-card#radio${idx} #rx-bar`).width != 0) {
                $(`.radio-card#radio${idx} #rx-bar`).width(0);
            }
            return
        }
        // Ignore radio that isn't receiving (checking for the class compensates for the rx delay)
        if (!$(`.radio-card#radio${idx}`).hasClass("receiving")) {
            if ($(`.radio-card#radio${idx} #rx-bar`).width != 0) {
                $(`.radio-card#radio${idx} #rx-bar`).width(0);
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
        $(`.radio-card#radio${idx} #rx-bar`).width(newPct);
    });

    // Input meter (only show when PTT)
    if (pttActive) {
        // Get data from mic
        audio.inputAnalyzer.getFloatTimeDomainData(audio.inputPcmData);
        sumSquares = 0.0;
        for (const amplitude of audio.inputPcmData) { sumSquares += amplitude * amplitude; }
        const newPct = String(Math.sqrt(sumSquares / audio.outputPcmData.length).toFixed(3) * 300);
        // Apply to selected radio only
        $(`.radio-card#radio${selectedRadioIdx} #tx-bar`).width(newPct);
    } else {
        $(`.radio-card#radio${selectedRadioIdx} #tx-bar`).width(0);
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
        if ($(`.radio-card#radio${idx}`).hasClass("receiving") || $(`.radio-card#radio${idx}`).hasClass("transmitting"))
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
        $(`.radio-card#radio${idx} #rx-bar`).width(0);
        $(`.radio-card#radio${idx} #tx-bar`).width(0);
    });
}

/**
 *  Change volume of console based on slider
 */
function volumeSlider() {
    // Convert 0-100 to 0-1 for multiplication with audio, using an inverse-square curve for better "logarithmic" volume
    const newVol = Math.pow($("#console-volume").val() / 100, 2);
    // Set gain node to new value
    audio.outputGain.gain.value = newVol;
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
 * Play an HTML-embedded sound object
 * @param {string} soundId id of the HTML embed object
 */
function playSound(soundId) {
    document.getElementById(soundId).play();
}

/**
 * Updates the audio parameters for each radio audio source based on current config and selected radio
 * NOTE: For whatever reason between the python server and this script the track order gets reversed
 * Therefore, we ascend the source list while descending the radio list
 */
 function updateRadioAudio() {
    console.debug("Updating radio sound parameters");
    radios.forEach(function(radio, idx) {
        // Ignore if audio not connected
        if (radios[idx].audioSrc == null) {
            console.debug(`Audio not connected for radio ${radios[idx].name}, skipping`);
            return;
        }
        if (idx == selectedRadioIdx) {
            console.debug(`Radio ${radios[idx].name} is selected. Setting gain to 1`);
            radios[idx].audioSrc.gainNode.gain.setValueAtTime(1, audio.context.currentTime);
        } else {
            console.debug(`Radio ${radios[idx].name} is unselected. Setting gain to ${config.audio.unselectedVol}`);
            radios[idx].audioSrc.gainNode.gain.setValueAtTime(dbToGain(config.audio.unselectedVol), audio.context.currentTime);
        }
        // Set AGC based on user setting
        if (config.audio.rxAgc) {
            console.log(`Enabling AGC for radio ${radios[idx].name}`);
            radios[idx].audioSrc.agcNode.threshold.setValueAtTime(audio.agcThreshold, audio.context.currentTime);
            radios[idx].audioSrc.makeupNode.gain.setValueAtTime(audio.agcMakeup, audio.context.currentTime);
        } else {
            console.log(`Byassing AGC for radio ${radios[idx].name}`);
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
        radios[idx].audioSrc.muteNode.gain.setValueAtTime(0, audio.context.currentTime);
    } else {
        radios[idx].audioSrc.muteNode.gain.setValueAtTime(1, audio.context.currentTime);
    }
}

/**
 * Various audio updates for the specified radio
 */
function updateAudio(idx) {
    console.debug(`Updating audio for radio ${idx}`);
    // Do nothing if audio sources aren't connected
    if (radios[idx].audioSrc == null) { 
        console.debug(`Audio not connected for radio ${radios[idx].name}, skipping`);
        return;
    }
    // Mute if we're muted or not receiving, after the specified delay in rtc.rxLatency
    if (radios[idx].status.muted || (radios[idx].status.State != 'Receiving')) {
        setTimeout(function() {
            console.debug(`Muting audio for radio ${radios[idx].name}`);
            radios[idx].audioSrc.muteNode.gain.setValueAtTime(0, audio.context.currentTime);
        }, radios[idx].rtc.rxLatency);
    } else {
        setTimeout(function() {
            console.debug(`Unmuting audio for radio ${radios[idx].name}`);
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
    console.debug(`Setting new pan for radio ${radios[idx]} to ${newPan}`);
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
    console.debug(`Resetting pan for radio ${radios[idx]}`);
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
    const dialpad = obj.closest('.icon-stack').querySelector('.dtmf-digits');
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
    this.gainNode.gain.value = 0.3;
    this.filter.type = 'lowpass';
    this.filter.frequency = '4000';
    // Connect everything
    this.osc1.connect(this.gainNode);
    this.osc2.connect(this.gainNode);
    this.gainNode.connect(this.filter);
    // Connect to both local speakers (for sidetone) and the mic destination/analyzer
    this.filter.connect(audio.outputGain);
    this.filter.connect(audio.inputAnalyzer);
    this.filter.connect(audio.inputDest);
}

DualTone.prototype.start = function() {
    this.setup();
    this.osc1.start(0);
    this.osc2.start(0);
    this.status = 1;
    this.gainNode.gain.value = 0.3;
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
function connectRadio(idx) {
    // Log
    console.info(`Connecting to radio ${radios[idx].name}`);
    // Update radio connection icon
    $(`#radio${idx} .icon-connect`).removeClass('disconnected');
    $(`#radio${idx} .icon-connect`).addClass('connecting');
    $(`#radio${idx} .icon-connect`).parent().prop('title','Connecting to daemon');
    // Create audio context if we haven't already
    if (audio.context == null) {
        startAudioDevices();
    }
    // Create main daemon websocket
    radios[idx].wsConn = new WebSocket("ws://" + radios[idx].address + ":" + radios[idx].port);
    radios[idx].wsConn.onerror = function(event) { handleSocketError(event, idx) };
    radios[idx].wsConn.onmessage = function(event) { recvSocketMessage(event, idx) };
    radios[idx].wsConn.onclose = function(event) { handleSocketClose(event, idx) };
    // Create WebRTC-specific websocket
    radios[idx].wsRtc = new WebSocket("ws://" + radios[idx].address + ":" + radios[idx].port + "/rtc");
    radios[idx].wsRtc.onerror = function(event) { handleRtcWsError(event, idx) };
    radios[idx].wsRtc.onmessage = function(event) { handleRtcWsMsg(event, idx) };
    radios[idx].wsRtc.onclose = function(event) { handleRtcWsClose(event, idx) };
    // Wait for connections
    waitForWebSockets([radios[idx].wsConn, radios[idx].wsRtc], function() { onConnectWebsocket(idx) });
}

/**
 * Wait for websocket connection to be active
 * @param {WebSocket} websocket 
 * @param {function} callback callback function to execute once connected
 */
function waitForWebSockets(sockets, callback=null) {
    setTimeout(
        function() {
            socketsReady = 0;
            sockets.forEach((socket) => {
                if (socket.readyState === 1)
                {
                    socketsReady++;
                }
            })
            if (socketsReady === sockets.length)
            {
                callback();
            } else {
                waitForWebSockets(sockets, callback);
            }
        },
    5); // 5 ms timeout
}

/**
 * Called once the websocket connection is active
 * @param {int} idx index of radio in radios[]
 */
function onConnectWebsocket(idx) {
    //$("#navbar-status").html("Websocket connected");
    console.log(`Websocket connected for radio ${radios[idx].name}`);
    // Query radio status
    console.log(`Querying radio ${radios[idx].name} status`);
    radios[idx].wsConn.send(JSON.stringify(
        {
            "radio": {
                "command": "query"
            }
        }
    ));
    // Start webrtc
    waitForRadioStatus(idx, function() { startWebRtc(idx) });
}

/**
 * Waits for the radiolist to be populated before calling callback
 * @param {int} idx index of radio in radios[]
 * @param {function} callback 
 */
function waitForRadioStatus(idx, callback) {
    setTimeout(
        function() {
            if (radios[idx].status.State != 'Disconnected') {
                if (callback != null) {
                    callback();
                }
            } else {
                waitForRadioStatus(idx, callback);
            }
        },
    5); // 5 ms timeout
}

/**
 * Disconnect from the websocket server
 * @param {int} idx radio index in radios[]
 */
function disconnectRadio(idx) {
    // Disconnect if we had a connection open
    if (radios[idx].wsConn) {
        if (radios[idx].wsConn.readyState == WebSocket.OPEN) {
            console.log(`Disconnecting from radio WebRTC connection ${radios[idx].name}`);
            stopWebRtc(idx);
            console.log(`Disconnecting from radio websocket ${radios[idx].name}`);
            radios[idx].wsConn.close();
        }
    }
}

/**
 * Callback for a new message from the websocket server and
 * parses the JSON command object. 
 * 
 * This command protocol is specified in `Docs/Websocket JSON Signalling.md`
 * @param {event} event 
 * @param {int} idx index of radio in radios[]
 */
function recvSocketMessage(event, idx) {
    // Convert to JSON
    var msgObj;
    try {
        msgObj = JSON.parse(event.data);
    } catch (e) {
        console.warn(`Got invalid data from radio ${radios[idx].name} websocket: ` + event.data);
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
                console.debug(`Got status update for radio ${radios[idx].name}:`, radioStatus);
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

            // NACK handler
            case "nack":
                console.error("Got NACK from server");
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

    // If no more radios connected, set master connect button to disconnected
    if (!radios.some(e => e.wsConn != null)) {
        $(`#navbar-connect`).removeClass("connected");
        $(`#navbar-connect`).addClass("disconnected");
    }
}

/**
 * Handle connection errors from the server
 * @param {event} event 
 */
function handleSocketError(event, idx) {
    console.error(`Websocket connection error for radio ${radios[idx].name}`);
    console.debug(event);
    //window.alert("Server connection errror: " + event.data);
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
    // Create the connection
    extensionWs = new WebSocket(`ws://${config.extensionAddress}:${config.extensionPort}`);
    // Create websocket
    extensionWs.onerror = function(event) { handleExtensionError(event) };
    extensionWs.onmessage = function(event) { recvExtensionMessage(event) };
    extensionWs.onclose = function(event) { handleExtensionClose(event) };
    // Wait for active
    waitForWebSocket(extensionWs, extensionConnected);
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
                if (selectedRadioIdx != value) {
                    selectRadio(`radio${value}`);
                }
                startPtt(true);
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
 * Converts a power gain value to the equivalent constant multiplier
 * @param {float} db Gain in decibels
 * @returns gain as a factor relative to 1
 */
function dbToGain(db) {
    return Math.pow(10, db/20).toFixed(3);
}