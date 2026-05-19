using rc2_core;
using Serilog;
using SIPSorceryMedia.Abstractions;
using System.Net;

namespace daemon
{
    /// <summary>
    /// Config object used to parse YML config for VOX control.
    /// </summary>
    public class VoxConfig
    {
        /// <summary>
        /// Static zone name shown in the console for VOX radios.
        /// </summary>
        public string ZoneName = "VOX";
        /// <summary>
        /// Static channel name shown in the console for VOX radios.
        /// </summary>
        public string ChannelName = "Audio";
        /// <summary>
        /// RX audio level, in dBFS, required to mark the radio receiving.
        /// </summary>
        public double RxThresholdDb = -45.0;
        /// <summary>
        /// TX audio level, in dBFS, required to mark the radio transmitting.
        /// </summary>
        public double TxThresholdDb = -45.0;
        /// <summary>
        /// Audio must remain above threshold for this many milliseconds before the gate opens.
        /// </summary>
        public int AttackMs = 80;
        /// <summary>
        /// The gate remains open this many milliseconds after audio drops below threshold.
        /// </summary>
        public int HangMs = 800;
        /// <summary>
        /// Audio samples are ignored for this many milliseconds after startup to let devices settle.
        /// </summary>
        public int StartupDelayMs = 1000;
        /// <summary>
        /// VOX opens only when audio is this many dB above the learned noise floor.
        /// </summary>
        public double NoiseMarginDb = 8.0;
        /// <summary>
        /// After startup or reconnect, require one quiet sample before the gate can open.
        /// </summary>
        public bool RequireQuietAfterReset = true;
    }

    /// <summary>
    /// Radio implementation for audio-only installations where TX/RX state is inferred from audio levels.
    /// </summary>
    internal sealed class VoxRadio : rc2_core.Radio
    {
        private readonly object stateLock = new object();
        private readonly VoxConfig voxConfig;
        private readonly VoxGate rxGate;
        private readonly VoxGate txGate;
        private readonly bool txDisabled;
        private readonly System.Timers.Timer hangTimer;
        private readonly int startupDelayMs;
        private long ignoreAudioUntilMs;
        private bool calibrationPending;
        private bool started;

        public VoxRadio(
            string name, string desc, bool rxOnly,
            IPAddress listenAddress, int listenPort,
            VoxConfig voxConfig,
            Action<short[]> txAudioCallback, int txAudioSampleRate, Action<AudioFormat> rtcFormatCallback,
            List<SoftkeyName> softkeys,
            List<TextLookup> zoneLookups = null, List<TextLookup> chanLookups = null
            ) : base(name, desc, rxOnly, listenAddress, listenPort, softkeys, zoneLookups, chanLookups, txAudioCallback, txAudioSampleRate, rtcFormatCallback)
        {
            this.voxConfig = voxConfig ?? new VoxConfig();
            txDisabled = rxOnly;
            RxOnly = rxOnly;
            startupDelayMs = Math.Max(0, this.voxConfig.StartupDelayMs);

            rxGate = new VoxGate(
                this.voxConfig.RxThresholdDb,
                this.voxConfig.NoiseMarginDb,
                this.voxConfig.RequireQuietAfterReset,
                Math.Max(0, this.voxConfig.AttackMs),
                Math.Max(1, this.voxConfig.HangMs));
            txGate = new VoxGate(
                this.voxConfig.TxThresholdDb,
                this.voxConfig.NoiseMarginDb,
                this.voxConfig.RequireQuietAfterReset,
                Math.Max(0, this.voxConfig.AttackMs),
                Math.Max(1, this.voxConfig.HangMs));

            Status.ZoneName = this.voxConfig.ZoneName;
            Status.ChannelName = this.voxConfig.ChannelName;

            hangTimer = new System.Timers.Timer(Math.Clamp(this.voxConfig.HangMs / 4, 50, 250));
            hangTimer.AutoReset = true;
            hangTimer.Elapsed += (sender, args) => ExpireGates();
        }

        public override void Start(bool reset = false)
        {
            Log.Information("Starting new VOX radio instance");
            base.Start(reset);

            lock (stateLock)
            {
                started = true;
                rxGate.Reset();
                txGate.Reset();
                Status.ZoneName = voxConfig.ZoneName;
                Status.ChannelName = voxConfig.ChannelName;
                Status.State = RadioState.Idle;
                ArmWarmup(Environment.TickCount64);
            }

            if (startupDelayMs > 0)
            {
                Log.Debug("VOX startup delay active for {delay} ms", startupDelayMs);
            }

            hangTimer.Start();
            RadioStatusCallback();
        }

        public void ReArmStartupDelay(string reason)
        {
            bool statusChanged = false;

            lock (stateLock)
            {
                ArmWarmup(Environment.TickCount64);

                if (started && Status.State != RadioState.Idle)
                {
                    Status.State = RadioState.Idle;
                    statusChanged = true;
                }
            }

            if (startupDelayMs > 0)
            {
                Log.Debug("VOX startup delay re-armed for {delay} ms ({reason})", startupDelayMs, reason);
            }
            else
            {
                Log.Debug("VOX gates reset ({reason})", reason);
            }

            if (statusChanged)
            {
                RadioStatusCallback();
            }
        }

        public override void Stop()
        {
            hangTimer.Stop();

            lock (stateLock)
            {
                started = false;
                Status.State = RadioState.Disconnected;
            }

            RadioStatusCallback();
            base.Stop();
        }

        public override bool SetTransmit(bool tx)
        {
            if (tx && txDisabled)
            {
                Log.Warning("Ignoring VOX transmit request because this radio is RX-only");
                return false;
            }

            Log.Debug("Acknowledging VOX transmit {state}; radio state will follow TX audio level", tx ? "start" : "stop");
            return true;
        }

        public override bool ChangeChannel(bool down)
        {
            Log.Debug("Ignoring channel change request because VOX control has no channel interface");
            return false;
        }

        public override bool PressButton(SoftkeyName name)
        {
            Log.Debug("Ignoring button press {name} because VOX control has no button interface", name);
            return false;
        }

        public override bool ReleaseButton(SoftkeyName name)
        {
            Log.Debug("Ignoring button release {name} because VOX control has no button interface", name);
            return false;
        }

        public void HandleRxAudioSamples(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] samples)
        {
            ProcessSamples(rxGate, samples, "RX");
        }

        public void HandleTxAudioSamples(short[] samples)
        {
            if (txDisabled)
            {
                return;
            }

            ProcessSamples(txGate, samples, "TX");
        }

        public void HandleRxEncodedSamples(uint durationRtpUnits, byte[] encodedSamples)
        {
            bool shouldForward;

            lock (stateLock)
            {
                shouldForward = started && Status.State == RadioState.Receiving;
            }

            if (!shouldForward)
            {
                return;
            }

            RxSendEncodedSamples(durationRtpUnits, encodedSamples);
        }

        private void ProcessSamples(VoxGate gate, short[] samples, string direction)
        {
            if (samples == null || samples.Length == 0)
            {
                return;
            }

            long nowMs = Environment.TickCount64;
            double levelDb = CalculateRmsDb(samples);
            RadioState nextState = RadioState.Idle;
            bool statusChanged = false;

            lock (stateLock)
            {
                if (!started)
                {
                    return;
                }

                if (nowMs < ignoreAudioUntilMs)
                {
                    gate.Calibrate(levelDb);
                    if (Status.State != RadioState.Idle)
                    {
                        Status.State = RadioState.Idle;
                        statusChanged = true;
                    }
                }
            }

            if (nowMs < ignoreAudioUntilMs)
            {
                if (statusChanged)
                {
                    RadioStatusCallback();
                }

                return;
            }

            lock (stateLock)
            {
                if (!started)
                {
                    return;
                }

                CompleteWarmup();
                gate.Process(levelDb, nowMs);
                nextState = GetVoxState();
                if (Status.State != nextState)
                {
                    Status.State = nextState;
                    statusChanged = true;
                }
            }

            if (statusChanged)
            {
                Log.Debug("VOX {direction} level {level:0.0} dBFS changed radio state to {state}", direction, levelDb, nextState);
                RadioStatusCallback();
            }
        }

        private void ExpireGates()
        {
            RadioState nextState = RadioState.Idle;
            bool statusChanged = false;

            lock (stateLock)
            {
                if (!started)
                {
                    return;
                }

                long nowMs = Environment.TickCount64;

                if (nowMs < ignoreAudioUntilMs)
                {
                    if (Status.State != RadioState.Idle)
                    {
                        Status.State = RadioState.Idle;
                        statusChanged = true;
                    }

                    nextState = RadioState.Idle;
                }
                else
                {
                    CompleteWarmup();
                    rxGate.Expire(nowMs);
                    txGate.Expire(nowMs);

                    nextState = GetVoxState();
                    if (Status.State != nextState)
                    {
                        Status.State = nextState;
                        statusChanged = true;
                    }
                }
            }

            if (statusChanged)
            {
                Log.Debug("VOX hang timer changed radio state to {state}", nextState);
                RadioStatusCallback();
            }
        }

        private RadioState GetVoxState()
        {
            if (!txDisabled && txGate.Active)
            {
                return RadioState.Transmitting;
            }

            if (rxGate.Active)
            {
                return RadioState.Receiving;
            }

            return RadioState.Idle;
        }

        private void ArmWarmup(long nowMs)
        {
            rxGate.Reset();
            txGate.Reset();
            ignoreAudioUntilMs = nowMs + startupDelayMs;
            calibrationPending = true;
        }

        private void CompleteWarmup()
        {
            if (!calibrationPending)
            {
                return;
            }

            calibrationPending = false;
            rxGate.CompleteCalibration();
            txGate.CompleteCalibration();
            Log.Debug(
                "VOX calibration complete: RX floor {rxFloor:0.0} dBFS, RX effective threshold {rxThreshold:0.0} dBFS; TX floor {txFloor:0.0} dBFS, TX effective threshold {txThreshold:0.0} dBFS",
                rxGate.NoiseFloorDb,
                rxGate.EffectiveThresholdDb,
                txGate.NoiseFloorDb,
                txGate.EffectiveThresholdDb);
        }

        private static double CalculateRmsDb(short[] samples)
        {
            double sumSquares = 0.0;

            foreach (short sample in samples)
            {
                double normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
            }

            double rms = Math.Sqrt(sumSquares / samples.Length);
            if (rms <= 0.0)
            {
                return double.NegativeInfinity;
            }

            return 20.0 * Math.Log10(rms);
        }

        private sealed class VoxGate
        {
            private readonly double thresholdDb;
            private readonly double noiseMarginDb;
            private readonly bool requireQuietAfterReset;
            private readonly int attackMs;
            private readonly int hangMs;
            private long? aboveThresholdSinceMs;
            private long lastAboveThresholdMs = long.MinValue;
            private double calibrationSumDb;
            private int calibrationCount;
            private bool waitingForQuiet;

            public bool Active { get; private set; }
            public double NoiseFloorDb { get; private set; } = double.NegativeInfinity;
            public double EffectiveThresholdDb
            {
                get
                {
                    if (double.IsNegativeInfinity(NoiseFloorDb))
                    {
                        return thresholdDb;
                    }

                    return Math.Max(thresholdDb, NoiseFloorDb + noiseMarginDb);
                }
            }

            public VoxGate(double thresholdDb, double noiseMarginDb, bool requireQuietAfterReset, int attackMs, int hangMs)
            {
                this.thresholdDb = thresholdDb;
                this.noiseMarginDb = noiseMarginDb;
                this.requireQuietAfterReset = requireQuietAfterReset;
                this.attackMs = attackMs;
                this.hangMs = hangMs;
            }

            public void Calibrate(double levelDb)
            {
                if (double.IsNegativeInfinity(levelDb) || double.IsNaN(levelDb))
                {
                    return;
                }

                calibrationSumDb += levelDb;
                calibrationCount++;
            }

            public void CompleteCalibration()
            {
                if (calibrationCount > 0)
                {
                    NoiseFloorDb = calibrationSumDb / calibrationCount;
                }
            }

            public void Process(double levelDb, long nowMs)
            {
                double effectiveThresholdDb = EffectiveThresholdDb;

                if (waitingForQuiet)
                {
                    if (levelDb < effectiveThresholdDb)
                    {
                        waitingForQuiet = false;
                    }

                    return;
                }

                if (levelDb >= effectiveThresholdDb)
                {
                    lastAboveThresholdMs = nowMs;
                    aboveThresholdSinceMs ??= nowMs;

                    if (!Active && nowMs - aboveThresholdSinceMs.Value >= attackMs)
                    {
                        Active = true;
                    }

                    return;
                }

                aboveThresholdSinceMs = null;
                Expire(nowMs);
            }

            public void Expire(long nowMs)
            {
                if (Active && lastAboveThresholdMs != long.MinValue && nowMs - lastAboveThresholdMs >= hangMs)
                {
                    Active = false;
                    aboveThresholdSinceMs = null;
                }
            }

            public void Reset()
            {
                Active = false;
                aboveThresholdSinceMs = null;
                lastAboveThresholdMs = long.MinValue;
                calibrationSumDb = 0.0;
                calibrationCount = 0;
                waitingForQuiet = requireQuietAfterReset;
            }
        }
    }
}
