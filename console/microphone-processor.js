/**
* Microphone AudioWorklet for handling microphone data
* @extends AudioWorkletProcessor
*/
registerProcessor('microphone-processor', class extends AudioWorkletProcessor {

    // constructor
    constructor(options) {
        super();
    }

    // process function
    process(inputs, outputs, parameters) {
        // get first channel of first input (ignore the others, if any, since we only want mono mic audio)
        const inputData = inputs[0][0];
        // Send this data back to the main script via the port
        this.port.postMessage(inputData);
        return true;
    }

});