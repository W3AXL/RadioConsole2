## JSON Command Spec

Used in websocket communication between client & radio daemon. Most command exchanges begin on the client and are responded to by the daemon. The daemon does not query the client at any point in time.

### Radio Status Query

Used to query the daemon about the radio status. Normally only used at startup of the console client.

```json
{
    "radio": {
        "command": "query"
    }
}
```

### Radio Status Report

Used to inform the client on the status of the radio. **(server-to-client only)**

```json
{
    "status": {
        "name": "",
        "desc": "",
        "zone": "",
        "chan": "",
        "state": "Disconnected",
        "scan": "NotScanning",
        "softkeys": [
            {
                "text": "KEY1",
                "desc": "Softkey 1",
                "state": "Off",
            },
            ...
        ]
    }
}
```

This status JSON is created in the `radioClass.py` `encodeClientStatus()` function

### Radio Control Commands

These are sent from the client to the daemon

```json
{
    "radio": {
        "command": "startTx",
        "options": null
    }
}
```

`command` can be any of the following:
- `startTx` / `stopTx` are used to start and stop PTT
- `chanUp` / `chanDn` are used to change channels
- `chanGoto` is used to goto a specific channel index **(not yet implemented)**
   - requires a number in the `options` field
- `button` indicates a button press. `options` specifies the button, valid options are:
   - `softkey1` through `softkey5` requisite softkey (dynamic for O5 control head or static for user-defined buttons)
   - `left` and `right` for paging through softkeys

### Audio Control Commands

These are used to control various audio functions

```json
{
    "audio": {
        "command": "mute",
    }
}
```

`command` can be any of the following:
 - `startAudio` is used to initialize the server-side audio handling routines. `index` should be `null` in this case
 - `mute` / `unmute` are used to mute/unmute a specific radio. `index` must be specified
 

### Legacy Audio Data Command

This is used for sending audio data arrays between the client and server

```json
{
    "audioData": {
        "source": "mic",
        "data": "csv mic samples"
    }
}
```

`source` can be either `mic` or `speaker`

`data` contains the array of mu-law encoded audio samples (as a comma-separated string)

### WebRTC Signalling Commands

These are sent both ways, and contain signalling messages for WebRTC configuration

```json
{
    "webRtcOffer": {
        "type": {},
        "sdp": {}
    }
}
```

This is the initial WebRTC offer from the client to the server.

`type` contains the SDP type
`sdp` contains the SDP data

```json
{
    "webRtcAnswer": {
        "type": {},
        "sdp": {}
    }
}
```

This is the WebRTC SDP answer from the server to the client

`type` contains the SDP type
`sdp` contains the SDP data

### NACK Message

```json
{
    "nack": {}
}
```