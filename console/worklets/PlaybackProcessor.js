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
    const { channels = 1, maxBufferSeconds = 0.5 } = options.processorOptions ?? {};
    this.channels = channels;
    this.capacity = Math.ceil(sampleRate * maxBufferSeconds);

    // One ring buffer per channel.
    this.ring = Array.from({ length: channels }, () => new Float32Array(this.capacity));
    this.writeIdx = 0;
    this.readIdx = 0;
    this.available = 0; // samples currently buffered (per channel)

    this.underrun = false; // true while we're waiting for the buffer to refill

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
    const n = channelData[0].length;
    // If the ring is already full, drop the OLDEST audio rather than the
    // newest — better to slightly clip old buffered content than to keep
    // growing latency unboundedly. Should only trigger if playback stalls
    // for a while (e.g. tab backgrounded, main thread blocked).
    if (this.available + n > this.capacity) {
      const overflow = this.available + n - this.capacity;
      this.readIdx = (this.readIdx + overflow) % this.capacity;
      this.available -= overflow;
    }
    for (let ch = 0; ch < this.channels; ch++) {
      const src = channelData[Math.min(ch, channelData.length - 1)];
      for (let i = 0; i < n; i++) {
        this.ring[ch][(this.writeIdx + i) % this.capacity] = src[i];
      }
    }
    this.writeIdx = (this.writeIdx + n) % this.capacity;
    this.available += n;
  }

  process(inputs, outputs) {
    const output = outputs[0];
    const frames = output[0].length; // 128 in practice

    if (this.available < frames) {
      // Underrun — output silence rather than stale/garbage samples, and
      // tell the main thread so it can surface a UI indicator if this is
      // happening often (a sign TARGET_BUFFER_MS is too small for current
      // network conditions).
      if (!this.underrun) {
        this.underrun = true;
        this.port.postMessage({ type: "underrun" });
      }
      for (let ch = 0; ch < output.length; ch++) output[ch].fill(0);
      return true;
    }

    if (this.underrun) {
      this.underrun = false;
      this.port.postMessage({ type: "recovered" });
    }

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