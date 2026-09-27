// This file allows for proper typescript checking of the preload IPC commands

export interface ElectronAPI {
    // Config
    readConfig(defaultConfig?: unknown): Promise<any>;
    saveConfig(configJson: string): Promise<boolean | string>;

    // Version string (event-listener style - fires once when main process sends it)
    getVersion(callback: (event: any, data: string) => void): void;

    // Peripheral config window
    showPeriphConfig(periphConfig: unknown): Promise<any>;
    savePeriphConfig(callback: (event: any, data: any) => void): void;

    // Serial port
    openSerialPort(path: string): Promise<any>;
    closeSerialPort(): Promise<any>;
    serialPortStatus(callback: (event: any, status: any) => void): void;

    // Midi handlers
    openMidiPort(port: number): Promise<any>;
    gotMidiMessage(callback: (event: any, message: any) => void): void;

    // Radio config dialog
    showRadioConfig(radioConfig: unknown): Promise<any>;
    saveRadioConfig(callback: (event: any, radioConfig: any) => void): void;
    cancelRadioConfig(callback: (event: any, data: any) => void): void;
}

declare global {
    interface Window {
        electronAPI: ElectronAPI;
    }
}