const ConfigVersion = 1;

const defaultConfig = {
    version: ConfigVersion,
    radios: [],
    autoConnect: false,
    clockFormat: "UTC",
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
};

module.exports = { ConfigVersion, defaultConfig };