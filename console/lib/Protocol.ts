import { Envelope, Hello, HelloAck, AudioCodec } from "../generated/RC2Proto";

// This has to match the daemon-side protocol version
const SUPPORTED_PROTOCOL_VERSION = 1;

// Samplerate used for the shared mic capture in AudioPipeline.ts
const SHARED_MIC_SAMPLE_RATE_HZ = 48000;

/**
 * An interface for storing the result of a handshake sequence
 */
export interface HandshakeResult {
    accepted: boolean;
    reason?: string;
    rxSampleRateHz: number;
    frameDurationMs: number;
}

/**
 * Get the current unix epoch in microseconds
 */
export function nowMicros(): bigint {
  return BigInt(Date.now()) * 1000n;
}

/**
 * Handles an incoming Hello from the radio daemon and validates it before returning a HelloAck and a result
 * @param hello the incoming Hello message
 * @returns a HandshakeResult showing acceptance or rejection of the Hello, and the HelloAck to send if successful
 */
export function handleHello(hello: Hello): { result: HandshakeResult; ack: HelloAck } {
    // Validate protocol version
    if (hello.protocolVersion !== SUPPORTED_PROTOCOL_VERSION) {
        return reject(
            `Protocol version mismatch: daemon=${hello.protocolVersion} console=${SUPPORTED_PROTOCOL_VERSION}`
        );
    }

    // Validate codec
    if (hello.audioCodec !== AudioCodec.OPUS) {
        // Right now we only support OPUS, but could support more codecs in the future if we wanted to
        return reject(`Unsupported audio codec: ${AudioCodec[hello.audioCodec]}`);
    }

    // Validate samplerate matches what we're configured for
    if (hello.rxSampleRateHz !== 0 && hello.rxSampleRateHz !== SHARED_MIC_SAMPLE_RATE_HZ) {
        return reject(
            `Daemon requested mic sample rate ${hello.rxSampleRateHz}Hz, but this console's ` +
            `shared mic encoder is fixed at ${SHARED_MIC_SAMPLE_RATE_HZ}Hz`
        );
    }

    // If everything is successful, return the result and a HelloAck
    return {
        result: { accepted: true, rxSampleRateHz: hello.rxSampleRateHz, frameDurationMs: hello.frameDurationMs },
        ack: { accepted: true, reason: "" },
    };
}

/**
 * Called when a Hello from the radio daemon is rejected
 * @param reason the reason for Hello rejection
 * @returns the rejected result and HelloAck
 */
function reject(reason: string): { result: HandshakeResult; ack: HelloAck } {
    return {
        result: { accepted: false, reason, rxSampleRateHz: 0, frameDurationMs: 0 },
        ack: { accepted: false, reason },
    };
}