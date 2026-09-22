import { RadioState, RadioCommandType } from "../generated/RC2Proto";

// Types for storing functions fired when a request is resolved or rejected
type ResolveFn = () => void;
type RejectFn = (reason: string) => void;

/**
 * A pending request consists of the command that was sent, a timeout handle,
 * and the resolved & rejected functions
 */
interface PendingRequest {
    command: RadioCommandType;
    timeoutHandle: ReturnType<typeof setTimeout>;
    resolve: ResolveFn;
    reject: RejectFn;
}

/**
 * Convenience set that allows us to check for any Radio state which would cancel a request
 */
const CANCEL_ON_STATE = new Set<RadioState>([
    RadioState.ERROR,
    RadioState.DISCONNECTED,
    RadioState.DISCONNECTING,
]);

// Default timeout for commands
const DEFAULT_TIMEOUT_MS = 1000;

/**
 * Main RequestTracker class, used to keep track of all pending radio command requests
 */
export class RequestTracker {
    // List of currently pending requests
    private pending = new Map<number, PendingRequest>();

    // The request ID to assign to the next request
    private nextRequestId = 1;

    /**
     * Registers a new command as pending, retrieves a request ID, and sets up the timeout and resolved/rejected handlers
     * @param command the Radio Command to send
     * @param timeoutMs the timeout in ms
     * @returns the request ID for the command and a promise that resolves upon timeout or completion
     */
    register(command: RadioCommandType,timeoutMs = DEFAULT_TIMEOUT_MS): { requestId: number; promise: Promise<void> } {
        // Get a request ID
        const requestId = this.nextRequestId++;

        // Create a promise that resolves when the request either resolves or times out
        const promise = new Promise<void>((resolve, reject) => {
            // Set up a new timeout that will delete the pending request and call the reject() handler
            const timeoutHandle = setTimeout(() => {
                this.pending.delete(requestId);
                reject(`Timed out waiting for response to ${RadioCommandType[command]}`);
            }, timeoutMs);
            // Add the pending request to the tracker
            this.pending.set(requestId, { command, timeoutHandle, resolve, reject });
        });
        // Return the request ID and the promise
        return { requestId, promise };
    }

    /**
     * Handler called when the request is ACKed by the remote radio
     * @param requestId the request ID for the pending command
     */
    resolve(requestId: number): void {
        const entry = this.pending.get(requestId);
        if (!entry) return; // already settled (timed out / cancelled) — ignore late duplicate
        clearTimeout(entry.timeoutHandle);
        this.pending.delete(requestId);
        entry.resolve();
    }

    /**
     * Handler called when the request is NACKed by the remote radio
     * @param requestId the request ID for the pending command
     * @param reason the reason the command was NACKed
     */
    reject(requestId: number, reason: string): void {
        const entry = this.pending.get(requestId);
        if (!entry) return;
        clearTimeout(entry.timeoutHandle);
        this.pending.delete(requestId);
        entry.reject(reason);
    }

    /**
     * Handler called every time a new Radio Status arrives, will cancel all pending requests if
     * we enter one of the CANCEL_ON_STATEs
     * @param state the new radio state
     */
    onStatus(state: RadioState): void {
        // If radio is OK, do nothing
        if (!CANCEL_ON_STATE.has(state)) return;

        // Store the reason
        const reason = `Radio entered ${RadioState[state]} before responding`;
        // Reject every pending request
        for (const entry of this.pending.values()) {
            clearTimeout(entry.timeoutHandle);
            entry.reject(reason);
        }
        // Clear the pending queue
        this.pending.clear();
    }

    /**
     * Cancels all active requests, with an optional reason
     * @param reason the reason all requests were cancelled
     */
    cancelAll(reason: string): void {
        for (const entry of this.pending.values()) {
            clearTimeout(entry.timeoutHandle);
            entry.reject(reason);
        }
        this.pending.clear();
    }
}