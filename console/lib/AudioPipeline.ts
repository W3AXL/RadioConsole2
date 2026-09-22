// audio-pipeline.ts
// Bridges the protobuf AudioFrame stream (see radio_console.proto) to the existing Web Audio graph

import { AudioFrame, AudioCodec, AudioSource } from "../generated/RC2Proto";

const OPUS_FRAME_SAMPLES = 960; // 20ms @ 48kHz, must match the radio daemon's encoder config
const MAX_BUFFER_SECONDS = 0.5; // hard ceiling before the playback ringbuffer starts dropping old audio

// ---------------------------------------------------------------
//
//  RX audio path (daemon -> console)
//
// ---------------------------------------------------------------

export class RadioAudioReceiver {
    readonly node: AudioWorkletNode;
    private decoder: AudioDecoder | null = null;
    private lastSequence: number | null = null;

    /**
     * Create a new instance of a RadioAudioReceiver, which handles RX audio from a radio daemon
     * @param ctx the Web Audio context
     * @param radioName the name of the radio
     * @param codec the codec being used (always OPUS for now)
     * @param sampleRateHz the samplerate for RX audio
     * @param channels the number of audio channels (should always be 1)
     */
    constructor(private ctx: AudioContext, private radioName: string, codec: AudioCodec, private sampleRateHz: number, channels: number) {
        // Create a new audio node for connecting to the rest of the console chain
        this.node = new AudioWorkletNode(ctx, "playback-processor", {
            outputChannelCount: [channels],
            processorOptions: { channels, maxBufferSeconds: MAX_BUFFER_SECONDS },
        });
        // Handle any underruns
        this.node.port.onmessage = (e) => {
            if (e.data.type === "underrun") {
                console.warn(`[${radioName}]: audio playback underrun`);
            }
        };
        // Right now we only handle OPUS, but could handle others in the future
        if (codec === AudioCodec.OPUS) {
            // Crete an OPUS decoder
            this.decoder = new AudioDecoder({
                output: (audioData) => this._onDecoded(audioData),
                error: (err) => console.error(`[${radioName}]: AudioDecoder error`, err),
            });
            this.decoder.configure({ codec: "opus", sampleRate: sampleRateHz, numberOfChannels: channels });
        }
        // Future codecs could go here
    }

    /**
     * Feed a frame of audio from the daemon into the decoder and RX ring buffer
     * @param frame the audio frame received from the daemon
     */
    handleFrame(frame: AudioFrame): void {
        this._checkSequenceAndFillGaps(frame.sequence);

        switch (frame.codec) {
            case AudioCodec.OPUS: {
                const chunk = new EncodedAudioChunk({
                    type: "key", // Opus packets don't reference each other the way video keyframes/deltas do
                    timestamp: (frame.sequence * OPUS_FRAME_SAMPLES * 1_000_000) / this.sampleRateHz,
                    data: frame.data,
                });
                this.decoder!.decode(chunk);
                break;
            }
            case AudioCodec.PCM_S16LE:
                this._pushRawSamples([pcmS16leToFloat32(frame.data)]);
                break;
            case AudioCodec.ULAW:
                this._pushRawSamples([ulawToFloat32(frame.data)]);
                break;
            default:
                console.warn(`[${this.radioName}]: unsupported audio codec ${AudioCodec[frame.codec]}`);
        }
    }

    /**
     * Check the current audio frame sequence and fill gaps with silence if we missed any frames
     * @param seq the latest sequence number received from the daemon
     */
    private _checkSequenceAndFillGaps(seq: number): void {
        if (this.lastSequence !== null) {
            // We use >>> 0 to ensure the number always stays positive and valid
            const expected = (this.lastSequence + 1) >>> 0;
            if (seq !== expected) {
                // Count the number of missed frames (and clamp to a positive number with the same trick)
                const missing = (seq - expected) >>> 0;
                // Throw a warning
                console.warn(
                    `[${this.radioName}]: dropped ${missing} audio frame(s) (seq ${expected}..${seq - 1})`
                );
                // Fill the gap with silence sized to the missing frames
                // TODO: In the future, we could use OPUS packet-loss concealment techniques
                this._pushRawSamples([new Float32Array(missing * OPUS_FRAME_SAMPLES)]);
            }
        }
        this.lastSequence = seq;
    }

    /**
     * Called when audio samples are decoded from the OPUS decoder
     * @param audioData the audio data decoded from the decoder
     */
    private _onDecoded(audioData: AudioData): void {
        // Float array for holding the samples inside the AudioData object
        const channelData: Float32Array[] = [];
        // Copy the data to the new float array
        for (let ch = 0; ch < audioData.numberOfChannels; ch++) {
            const buf = new Float32Array(audioData.numberOfFrames);
            audioData.copyTo(buf, { planeIndex: ch });
            channelData.push(buf);
        }
        // Close out the data
        audioData.close();
        // Push the samples to the node
        this._pushRawSamples(channelData);
    }

    /**
     * Push raw float samples to the RX audio buffer
     * @param channelData the array of float samples to push
     */
    private _pushRawSamples(channelData: Float32Array[]): void {
        this.node.port.postMessage(
            { type: "push", channelData },
            channelData.map((c) => c.buffer)
        );
    }

    /**
     * Called on a radio error, sends a reset to the audio node
     */
    reset(): void {
        this.lastSequence = null;
        this.node.port.postMessage({ type: "reset" });
    }

    /**
     * Called when the radio disconnects, closes the audio node
     */
    close(): void {
        this.decoder?.close();
        this.node.disconnect();
    }
}

/**
 * Helper function to convert signed PCM16 little-endian samples to Float32
 * @param data the 8-bit PCM data array, where every 2 bytes represent a signed little-endian PCM16 sample
 * @returns a float32 array of converted samples
 */
function pcmS16leToFloat32(data: Uint8Array): Float32Array {
    const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    const out = new Float32Array(data.length / 2);
    for (let i = 0; i < out.length; i++) out[i] = view.getInt16(i * 2, true) / 32768;
    return out;
}

/**
 * Convert u-Law samples to float samples
 * @param data the u-law encoded samples
 * @returns a float32 array of converted samples
 */
function ulawToFloat32(data: Uint8Array): Float32Array {
    const out = new Float32Array(data.length);
    for (let i = 0; i < data.length; i++) {
        let byte = ~data[i] & 0xff;
        const sign = byte & 0x80;
        const exponent = (byte >> 4) & 0x07;
        const mantissa = byte & 0x0f;
        let sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        out[i] = (sign ? -sample : sample) / 32768;
    }
    return out;
}

// ---------------------------------------------------------------
//
//  Capture path (console -> daemon)
//
// ---------------------------------------------------------------

/**
 * An interface representing an audio destination for sending audio frames to
 */
export interface AudioFrameSink {
    sendAudioFrame(frame: AudioFrame): void;
}

/**
 * Class responsible for distributing out mic audio to the various connected radio daemons
 */
export class MicCaptureManager {
    // The audio node for capturing mic audio
    private node: AudioWorkletNode | null = null;
    // The audio encoder for encoding audio before sending to radio daemons
    private encoder: AudioEncoder | null = null;
    // The outgoing audio frame sequence number
    private sequence = 0;
    // The list of audio endpoints to send outgoing mic audio to
    private targets = new Map<string, AudioFrameSink>(); // radioId -> connection

    /**
     * Instantiate a new mic capture manager to manage outgoing mic audio
     * @param ctx the Web Audio context
     * @param micSourceNode the audio node providing mic audio to the radios
     */
    constructor(private ctx: AudioContext, private micSourceNode: AudioNode) { }

    /**
     * Ensure that the audio processes and encoders are set up and running
     */
    private _ensureStarted(): void {
        // If the audio node is already created, return
        if (this.node) return;

        // Create a new audio node to connect the incoming mic audio to
        this.node = new AudioWorkletNode(this.ctx, "capture-processor", {
            processorOptions: { frameSize: OPUS_FRAME_SAMPLES },
        });
        this.micSourceNode.connect(this.node);

        // Create a new audio encoder and set up its encoded data and error callbacks
        this.encoder = new AudioEncoder({
            output: (chunk) => this._onEncoded(chunk),
            error: (err) => console.error("MicCaptureManager: AudioEncoder error", err),
        });
        // Configure for OPUS
        // TODO: if we support other codecs in the future, we'll have to handle that here
        this.encoder.configure({
            codec: "opus",
            sampleRate: this.ctx.sampleRate,
            numberOfChannels: 1,
            bitrate: 24000, // fine for voice; tune down further if bandwidth is a concern
        });
        // Handle incoming audio frames
        this.node.port.onmessage = (e) => {
            if (e.data.type === "frame") this._encodeFrame(e.data.samples as Float32Array);
        };
    }

    /**
     * Encode an incoming mic audio frame
     * @param samples the incoming float32 audio samples
     */
    private _encodeFrame(samples: Float32Array): void {
        // Create a new audio data object
        const audioData = new AudioData({
            format: "f32-planar",
            sampleRate: this.ctx.sampleRate,
            numberOfChannels: 1,
            numberOfFrames: samples.length,
            timestamp: (this.sequence * OPUS_FRAME_SAMPLES * 1_000_000) / this.ctx.sampleRate,
            data: new Float32Array(samples),    // we have to convert the shared buffer to a standard buffer here
        });
        // Encode the audio data
        this.encoder!.encode(audioData);
        // Close the audio data frame
        audioData.close();
    }

    /**
     * Callback fired when new encoded audio samples are ready
     * @param chunk the encoded audio chunk
     */
    private _onEncoded(chunk: EncodedAudioChunk): void {
        // Convert the chunk to a raw byte array
        const data = new Uint8Array(chunk.byteLength);
        chunk.copyTo(data);

        // Create a new audio frame from the encoded data
        const frame: AudioFrame = {
            source: AudioSource.MIC,
            codec: AudioCodec.OPUS,
            sequence: this.sequence++,
            sampleRateHz: this.ctx.sampleRate,
            channels: 1,
            data,
        };
        // Send the audio data to each daemon in our list
        for (const sink of this.targets.values()) sink.sendAudioFrame(frame);
    }

    /**
     * Add a new daemon endpoint to this mic handler
     * @param radioId the unique ID identifying the radio
     * @param sink the audio sink for this radio
     */
    addTarget(radioId: string, sink: AudioFrameSink): void {
        this._ensureStarted();
        this.targets.set(radioId, sink);
    }

    /**
     * Remove a radio from the list of targets
     * @param radioId the unique ID identifying the radio
     */
    removeTarget(radioId: string): void {
        this.targets.delete(radioId);
        // if we have no more targets, teardown the audio processes and nodes
        if (this.targets.size === 0) this._teardown();
    }

    /**
     * Teardown the audio encoder and node
     */
    private _teardown(): void {
        this.encoder?.close();
        this.encoder = null;
        this.node?.disconnect();
        this.node = null;
        this.sequence = 0;
    }
}