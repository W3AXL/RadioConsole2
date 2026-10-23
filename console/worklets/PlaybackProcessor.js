// playback-processor.js
//
// AudioWorkletProcessor that plays back decoded PCM pushed to it via its
// message port. This replaces the remote MediaStreamTrack that used to
// arrive in WebRTC's `peer.ontrack` handler — everything downstream
// (filterNode, agcNode, makeupNode, muteNode, gainNode, panNode,
// analyzerNode) connects to this node's output exactly as it connected
// to the old MediaStreamAudioSourceNode. No changes needed there.

class PlaybackProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const {
            channels = 1,
            maxBufferSeconds = 0.5,         // hard cap of audio buffer
            targetBufferSeconds = 0.15,     // desired length of audio buffer
            forceTrimBufferSeconds = 0.35,  // Buffer length above which we'll start trimming
            quietPeak = 0.02                // Sample amplitude below which we count as silence
        } = options.processorOptions ?? {};

        // Store config
        this.channels = channels;
        this.capacity = Math.ceil(sampleRate * maxBufferSeconds);
        this.targetDepth = Math.ceil(sampleRate * targetBufferSeconds);
        this.forceTrimDepth = Math.min(this.capacity, Math.ceil(sampleRate * forceTrimBufferSeconds));
        this.quietPeak = quietPeak;

        // One ring buffer per channel.
        this.ring = Array.from({ length: channels }, () => new Float32Array(this.capacity));
        this.writeIdx = 0;
        this.readIdx = 0;
        this.available = 0; // samples currently buffered (per channel)

        this.underrun = false; // true while we're waiting for the buffer to refill

        // Stats counters for the adaptive buffer length stuff
        this.stats = {
            underruns: 0,       // number of underruns that have occurred
            trimmedQuiet: 0,    // number of samples skipped by silence fast-forwards
            trimmedForced: 0,   // samples discarded by the forced trim fallback
            forcedEvents: 0,    // number of forced trims
            overflowDropped: 0  // number of samples dropped due to buffer overflow
        }

        // Periodic buffer stats print
        this.reportCounter = 0;
        this.REPORT_INTERVAL = 50;

        this.port.onmessage = (event) => {
            const { type } = event.data;
            if (type === "push") {
                this._push(event.data.channelData); // Float32Array[], one per channel
            } else if (type === "reset") {
                // Called when the radio errors/disconnects — see RadioAudioReceiver.reset()
                this.writeIdx = 0;
                this.readIdx = 0;
                this.available = 0;
                this.underrun = false;
            }
        };
    }

    _push(channelData) {
        // Get sample length
        const n = channelData[0].length;
        
        // If we don't have enough room in the buffer, drop the samples as an overflow
        if (this.available + n > this.capacity) {
            const overflow = this.available + n - this.capacity;
            this.readIdx = (this.readIdx + overflow) % this.capacity;
            this.available -= overflow;
            this.stats.overflowDropped += overflow;
        }

        // Push the data
        for (let ch = 0; ch < this.channels; ch++) {
            const src = channelData[Math.min(ch, channelData.length - 1)];
            for (let i = 0; i < n; i++) {
                this.ring[ch][(this.writeIdx + i) % this.capacity] = src[i];
            }
        }

        // Update counters
        this.writeIdx = (this.writeIdx + n) % this.capacity;
        this.available += n;
    }

    /**
     * Returns true if the first n samples in the buffer are all within the silence threshold
     * @param {number} n the number of samples to check
     */
    _headIsQuiet(n) {
        const src = this.ring[0];
        for (let i = 0; i < n; i++)
        {
            const v = src[(this.readIdx + i) % this.capacity];
            if (v > this.quietPeak || v < -this.quietPeak) {
                return false;
            }
        }
        return true;
    }

    /**
     * Steers the buffer back towards the target buffer size.
     * 
     * First, we try to skip ahead whenever enough silence is detected
     * If the buffer exceeds the trim treshold, we start hard-dropping samples
     * 
     * @param {number} frames the number of frames we need to advance 
     */
    _controlDepth(frames) {
        // If we're above our trim treshold, hard-drop samples
        if (this.available > this.forceTrimDepth) {
            const excess = this.available - this.targetDepth;
            this.readIdx = (this.readIdx + excess) % this.capacity;
            this.available -= excess;
            this.stats.trimmedForced += excess;
            this.stats.forcedEvents++;
            return;
        }
        // If we're below the trim threshold, try to skip ahead by dropping silent samples
        while (this.available - frames >= this.targetDepth + frames && this._headIsQuiet(frames)) {
            this.readIdx = (this.readIdx + frames) % this.capacity;
            this.available -= frames;
            this.stats.trimmedQuiet += frames;
        }
    }

    process(inputs, outputs) {
        const output = outputs[0];
        const frames = output[0].length; // 128 in practice

        // Periodic stats report
        if (++this.reportCounter >= this.REPORT_INTERVAL) {
            this.reportCounter = 0;
            this.port.postMessage({
                type: "stats",
                sampleRate,
                available: this.available,
                ...this.stats
            });
        }

        if (this.available < frames) {
            // Report underrun to the main process if we haven't already
            if (!this.underrun) {
                this.underrun = true;
                this.stats.underruns++;
                this.port.postMessage({ type: "underrun" });
            }
            // Fill silence
            for (let ch = 0; ch < output.length; ch++) output[ch].fill(0);
            return true;
        }

        // Clear underrun flag
        if (this.underrun) {
            this.underrun = false;
            this.port.postMessage({ type: "recovered" });
        }

        // Perform depth control routine
        this._controlDepth(frames);

        // Copy samples
        for (let ch = 0; ch < output.length; ch++) {
            const src = this.ring[Math.min(ch, this.channels - 1)];
            const out = output[ch];
            for (let i = 0; i < frames; i++) {
                out[i] = src[(this.readIdx + i) % this.capacity];
            }
        }
        this.readIdx = (this.readIdx + frames) % this.capacity;
        this.available -= frames;

        return true; // keep processor alive
    }
}

registerProcessor("playback-processor", PlaybackProcessor);