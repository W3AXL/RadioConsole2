/**
 * DTMF generator class, for generating dual-tone digits
 */
export class DtmfGenerator {
    
    // Start at DTMF 0 (941/1336 Hz)
    private _freq1: number = 941;
    private _freq2: number = 1336;

    // Default gain
    private _gain: number = 0.4;

    get freq1(): number {
        return this._freq1;
    }
    set freq1(value: number) {
        this._freq1 = value;
    }

    get freq2(): number {
        return this._freq2;
    }
    set freq2(value: number) {
        this._freq2 = value;
    }

    get gain(): number {
        return this._gain;
    }
    set gain(value: number) {
        this._gain = value;
    }

    // The audio nodes
    private osc1: OscillatorNode;
    private osc2: OscillatorNode;
    private gainNode: GainNode;
    private filter: BiquadFilterNode;

    // Whether we're actively generating tones
    private _active: boolean = false;
    get active(): boolean {
        return this._active;
    }

    /**
     * Create a new instance of a DTMF generator
     * @param ctx the AudioContext this generator will exist in
     */
    constructor(ctx: AudioContext) {
        // Setup audio
        this.osc1 = ctx.createOscillator();
        this.osc2 = ctx.createOscillator();
        this.gainNode = ctx.createGain();
        this.filter = ctx.createBiquadFilter();
        // Set initial values
        this.osc1.frequency.value = this._freq1;
        this.osc2.frequency.value = this._freq2;
        this.gainNode.gain.value = 0; // we start muted
        this.filter.type = 'lowpass';
        this.filter.frequency.value = 4000;
        // Connect internal nodes
        this.osc1.connect(this.gainNode);
        this.osc2.connect(this.gainNode);
        this.gainNode.connect(this.filter);
    }

    /**
     * Connect the output of this DTMF generator to an audio node
     * @param node the node to connect to
     */
    connect(node: AudioNode) {
        this.filter.connect(node);
    }

    /**
     * Start generating tones
     */
    start() {
        // Set oscillator frequencies
        this.osc1.frequency.value = this._freq1;
        this.osc2.frequency.value = this._freq2;

        // Start
        this.osc1.start();
        this.osc2.start();

        // Turn gain on
        this.gainNode.gain.value = this._gain;

        // We're going
        this._active = true;
    }

    /**
     * Stop generating tones
     */
    stop() {
        this.osc1.stop(0);
        this.osc2.stop(0);
        this.gainNode.gain.value = 0;
        this._active = false;
    }
}

/**
 * Alert tone modes
 */
export enum AlertToneMode {
    NONE,
    CONTINUOUS,
    ALTERNATING,
    PULSED
}

enum AlertToneFreq {
    LOW_800 = 800,
    MID_1000 = 1000,
    HIGH_1500 = 1500
}

export class AlertToneGenerator {

    private _mode: AlertToneMode = AlertToneMode.CONTINUOUS;

    get mode(): AlertToneMode {
        return this._mode;
    }
    set mode(value: AlertToneMode) {
        this._mode = value;
    }

    private _gain: number = 0.4;

    get gain(): number {
        return this._gain;
    }
    set gain(value: number) {
        this._gain = value;
    }

    private _period: number = 500;
    get period(): number {
        return this._period;
    }
    set period(value: number) {
        this._period = value;
    }

    // Oscillator, filter, and gain nodes
    private osc : OscillatorNode;
    private volume: GainNode;
    private filter: BiquadFilterNode;

    // Whether we're generating a tone or not
    private _active: boolean = false;
    get active(): boolean {
        return this._active;
    }

    // The timeout for tone changes
    private timeout? : NodeJS.Timeout;

    /**
     * Create a new Alert Tone generator
     * @param ctx the audio context to use
     */
    constructor(ctx: AudioContext) {
        // Setup audio nodes
        this.osc = ctx.createOscillator();
        this.volume = ctx.createGain();
        this.filter = ctx.createBiquadFilter();
        // Set initial values
        this.volume.gain.value = 0; // start muted
        this.filter.type = 'lowpass';
        this.filter.frequency.value = 4000;
        // Connect
        this.osc.connect(this.volume);
        this.volume.connect(this.filter);
    }

    /**
     * Connect the output of this Alert Tone generator to an audio node
     * @param node the node to connect to
     */
    connect(node: AudioNode) {
        this.filter.connect(node);
    }

    /**
     * Callback fired at the end of the timer to change/stop the tones
     */
    private timerCallback() {
        // Switch based on tone mode
        switch (this.mode) {
            case AlertToneMode.CONTINUOUS:
                // Do nothing with a continuous tone
                break;
            case AlertToneMode.ALTERNATING:
                // Get the current tone
                const currentFreq = this.osc.frequency.value as AlertToneFreq;
                // Switch based on where we are now
                if (currentFreq == AlertToneFreq.LOW_800) {
                    this.osc.frequency.value = AlertToneFreq.HIGH_1500;
                } else {
                    this.osc.frequency.value = AlertToneFreq.LOW_800;
                }
                break;
            case AlertToneMode.PULSED:
                // Get current volume
                const vol = this.volume.gain.value;
                // Mute or unmute
                if (vol == 0) {
                    this.volume.gain.value = this._gain;
                } else {
                    this.volume.gain.value = 0;
                }
                break;
        }
        // Set the timeout again
        this.timeout = setTimeout(() => {
            this.timerCallback();
        }, Math.floor(this._period / 2));
    }

    /**
     * Start tone generation
     */
    start() {
        // Get starting tone based on mode
        switch (this._mode) {
            case AlertToneMode.CONTINUOUS:
            case AlertToneMode.PULSED:
                this.osc.frequency.value = AlertToneFreq.MID_1000;
                break;
            case AlertToneMode.ALTERNATING:
                this.osc.frequency.value = AlertToneFreq.HIGH_1500;
        }
        // Start osc
        this.osc.start();
        // Unmute
        this.volume.gain.value = this._gain;
        // We're running
        this._active = true;
        // Set timer
        this.timeout = setTimeout(() => {
            this.timerCallback();
        }, Math.floor(this._period / 2));
    }

    /**
     * Stop tone generation
     */
    stop() {
        // Stop osc
        this.osc.stop();
        // Clear timeout
        if (this.timeout) {
            clearTimeout(this.timeout);
        }
        // Mute
        this.volume.gain.value = 0;
        // No longer active
        this._active = false;
    }
}