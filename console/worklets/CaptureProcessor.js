// CaptureProcessor.js
//
// AudioWorkletProcessor that sits on the mic input graph (tapped from
// audio.inputMicGain) and batches Float32 samples into fixed-size frames
// (960 samples = 20ms @ 48kHz, matching OPUS_FRAME_SAMPLES in
// AudioPipeline.ts) before handing them to the main thread for encoding.
//
// Registered as "capture-processor" - must match the name string passed
// to `new AudioWorkletNode(ctx, "capture-processor", ...)` in
// AudioPipeline.ts exactly.

class CaptureProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const { frameSize = 960 } = options.processorOptions ?? {};
    this.frameSize = frameSize;
    this.buffer = new Float32Array(frameSize);
    this.filled = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const channel = input[0]; // mono capture

    let i = 0;
    while (i < channel.length) {
      const toCopy = Math.min(this.frameSize - this.filled, channel.length - i);
      this.buffer.set(channel.subarray(i, i + toCopy), this.filled);
      this.filled += toCopy;
      i += toCopy;

      if (this.filled === this.frameSize) {
        // .slice(0) copies - `this.buffer` keeps filling for the next
        // frame, so we can't transfer its underlying storage directly.
        this.port.postMessage({ type: "frame", samples: this.buffer.slice(0) });
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor("capture-processor", CaptureProcessor);