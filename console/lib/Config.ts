/**
 * Valid radio card colors
 */
export enum RadioCardColor {
    Red = "red",
    Amber = "amber",
    Yellow = "yellow",
    Green = "green",
    Teal = "teal",
    Blue = "blue",
    Purple = "purple",
    Pink = "pink",
    Brown = "brown",
    Black = "black",
    Grey = "grey",
    White = "white"
}

/**
 * Parse a string to a RadioCardColor, returns null on invalid
 * @param colorName the name of the color to parse
 */
export function parseCardColor(colorName: string) : RadioCardColor | undefined {
    // Convert to lower case
    const searcStr = colorName.toLowerCase();
    
    // Find the right key in the enum
    const matched = Object.keys(RadioCardColor).find(
        (key) => key.toLowerCase() === searcStr
    ) as keyof typeof RadioCardColor | undefined;

    // Return
    return matched ? RadioCardColor[matched] : undefined
}

/**
 * An interface representing a single configured radio in the console
 */
export interface Radio {
    // Radio daemon address
    address: string,
    // Radio daemon port
    port: number,
    // Radio name
    name: string,
    // Radio pan
    pan: number,
    // Radio muted
    muted: boolean,
    // Radio card color
    color: RadioCardColor
    // Midi CC for PTT on this radio
    midiPttCC?: MidiCC,
    // Midi CC for volume control on this radio
    midiVolumeCC?: MidiCC
}

/**
 * Console clock format options
 */
export enum ClockFormat {
    UTC = "UTC",
    Local = "Local"
}

/**
 * Audio configuration interface for the console
 */
export interface AudioConfig {
    // Volume offset for unselected radios
    unselectedVolume: number,
    // Volume for console tones and UI sounds
    toneVolume: number,
    // Whether button sounds are enabled or not
    buttonSounds: boolean,
    // Whether to use AGC for radio RX audio
    useAGC: boolean
}

/**
 * Interface representing the extension configuration for the console
 */
export interface ExtensionConfig {
    // Whether extensions are enabled
    enabled: boolean,
    // Extension address
    address: string,
    // Extension port
    port: number
}

/**
 * An interface representing a single midi CC (channel + number)
 */
export interface MidiCC {
    channel: number,
    number: number
}

/**
 * Interface representing the console's global midi config
 */
export interface MidiConfig {
    enabled: boolean,
    port: number,
    masterPtt?: MidiCC,
    masterVol?: MidiCC
}

/**
 * Valid control line inputs on a standard serial port
 */
export enum SerialControlInput {
    RI,
    CTS,
    DSR,
    DCD,
}

/**
 * Interface representing the console's serial peripheral config
 */
export interface SerialConfig {
    enabled: boolean,
    port: string, 
    pttLine?: SerialControlInput
}

/**
 * Interface representing the overall console peripheral config (Midi + Serial)
 */
export interface PeripheralConfig {
    midi: MidiConfig,
    serial: SerialConfig
}

export const ConfigVersion = 1;

/**
 * The master configuration interface
 */
export interface Configuration {
    // Version of the configuration
    version: number, 

    // Configured radio daemon endpoints
    radios: Radio[],
    
    // Whether the radios should autoconnect on console launch
    autoConnect: boolean,

    // Clock format, UTC vs Local time
    clockFormat: ClockFormat,

    // Audio configuration for the console
    audio: AudioConfig,

    // Extension configuration for the console
    extension: ExtensionConfig,

    // Peripheral config
    peripherals: PeripheralConfig,
}