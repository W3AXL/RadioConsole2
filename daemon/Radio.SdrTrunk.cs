using NAudio.Wave;
using rc2_core;
using Serilog;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace daemon
{
    /// <summary>
    /// Config object used to parse YML config for direct sdrtrunk stream ingest.
    /// </summary>
    public class SdrTrunkConfig
    {
        /// <summary>
        /// Address for the Icecast-compatible source listener that sdrtrunk connects to.
        /// </summary>
        public IPAddress ListenAddress = IPAddress.Parse("127.0.0.1");
        /// <summary>
        /// Port for the Icecast-compatible source listener that sdrtrunk connects to.
        /// </summary>
        public int ListenPort = 8000;
        /// <summary>
        /// Mount point that sdrtrunk should publish to.
        /// </summary>
        public string Mount = "/sdrtrunk";
        /// <summary>
        /// Optional Icecast source password. Leave empty to accept local unauthenticated sources.
        /// </summary>
        public string SourcePassword = "";
        /// <summary>
        /// Static zone name shown in the console for sdrtrunk streams.
        /// </summary>
        public string ZoneName = "SDRTrunk";
        /// <summary>
        /// Static channel name shown in the console for sdrtrunk streams.
        /// </summary>
        public string ChannelName = "Stream";
        /// <summary>
        /// Audio level, in dBFS, required to mark the stream receiving.
        /// </summary>
        public double RxThresholdDb = -45.0;
        /// <summary>
        /// Audio must remain above threshold for this many milliseconds before the gate opens.
        /// </summary>
        public int AttackMs = 0;
        /// <summary>
        /// The gate remains open this many milliseconds after audio drops below threshold.
        /// </summary>
        public int HangMs = 1000;
        /// <summary>
        /// Sample rate to forward to RC2 before final WebRTC encoding.
        /// </summary>
        public int OutputSampleRate = 16000;
        /// <summary>
        /// Restart the sdrtrunk source connection if it stops producing decodable audio for this many milliseconds.
        /// </summary>
        public int SourceNoAudioRestartMs = 10000;
    }

    /// <summary>
    /// RX-only radio fed by a direct Icecast-compatible source connection from sdrtrunk.
    /// </summary>
    internal sealed class SdrTrunkRadio : rc2_core.Radio
    {
        private readonly object stateLock = new object();
        private readonly SdrTrunkConfig streamConfig;
        private readonly IcecastSourceServer sourceServer;
        private readonly System.Timers.Timer hangTimer;
        private long? aboveThresholdSinceMs;
        private long lastAboveThresholdMs = long.MinValue;
        private long lastMetadataActiveMs = long.MinValue;
        private long lastAudioStatsMs = long.MinValue;
        private long lastSourceStatsMs = long.MinValue;
        private long sourceConnectedAtMs = long.MinValue;
        private long lastSourceByteMs = long.MinValue;
        private long lastDecodedFrameMs = long.MinValue;
        private long decodedFrameCount;
        private long forwardedSampleCount;
        private long sourceBytesRead;
        private long lastSourceStatsBytes;
        private byte[] sourcePrefix = Array.Empty<byte>();
        private string lastMetadataTitle = "";
        private bool gateActive;
        private bool metadataActive;
        private bool sourceActive;
        private bool sourceRestartRequested;
        private bool started;

        public SdrTrunkRadio(
            string name, string desc,
            IPAddress listenAddress, int listenPort,
            SdrTrunkConfig streamConfig,
            List<SoftkeyName> softkeys,
            List<TextLookup> zoneLookups = null, List<TextLookup> chanLookups = null
            ) : base(name, desc, true, listenAddress, listenPort, softkeys, zoneLookups, chanLookups, null, 8000, null)
        {
            this.streamConfig = streamConfig ?? new SdrTrunkConfig();
            RxOnly = true;

            Status.ZoneName = this.streamConfig.ZoneName;
            Status.ChannelName = this.streamConfig.ChannelName;

            sourceServer = new IcecastSourceServer(
                this.streamConfig.ListenAddress,
                this.streamConfig.ListenPort,
                this.streamConfig.Mount,
                this.streamConfig.SourcePassword,
                HandleSource,
                HandleMetadata);

            hangTimer = new System.Timers.Timer(Math.Clamp(this.streamConfig.HangMs / 4, 50, 250));
            hangTimer.AutoReset = true;
            hangTimer.Elapsed += (sender, args) => ExpireGate();
        }

        public override void Start(bool reset = false)
        {
            Log.Information("Starting new sdrtrunk stream radio instance");
            base.Start(reset);

            lock (stateLock)
            {
                started = true;
                ResetGate();
                Status.ZoneName = streamConfig.ZoneName;
                Status.ChannelName = streamConfig.ChannelName;
                Status.CallerId = "";
                Status.State = RadioState.Idle;
            }

            sourceServer.Start();
            hangTimer.Start();
            RadioStatusCallback();
        }

        public override void Stop()
        {
            hangTimer.Stop();

            lock (stateLock)
            {
                started = false;
                ResetGate();
                Status.State = RadioState.Disconnected;
            }

            RadioStatusCallback();
            sourceServer.Stop().GetAwaiter().GetResult();
            base.Stop();
        }

        public override bool SetTransmit(bool tx)
        {
            Log.Debug("Ignoring transmit request because sdrtrunk stream radios are RX-only");
            return false;
        }

        public override bool ChangeChannel(bool down)
        {
            Log.Debug("Ignoring channel change request because sdrtrunk streams have no channel interface");
            return false;
        }

        public override bool PressButton(SoftkeyName name)
        {
            Log.Debug("Ignoring button press {name} because sdrtrunk streams have no button interface", name);
            return false;
        }

        public override bool ReleaseButton(SoftkeyName name)
        {
            Log.Debug("Ignoring button release {name} because sdrtrunk streams have no button interface", name);
            return false;
        }

        private async Task HandleSource(IcecastSourceRequest request, CancellationToken token)
        {
            if (request.Headers.TryGetValue("content-type", out string contentType) &&
                !contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("mp3", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("sdrtrunk source content type {contentType} is not MP3; attempting MP3 decode anyway", contentType);
            }

            if (request.Headers.TryGetValue("audio-info", out string audioInfo))
            {
                Log.Debug("sdrtrunk source audio-info: {audioInfo}", audioInfo);
            }

            lock (stateLock)
            {
                long nowMs = Environment.TickCount64;
                sourceActive = true;
                sourceRestartRequested = false;
                sourceConnectedAtMs = nowMs;
                lastSourceByteMs = long.MinValue;
                lastDecodedFrameMs = long.MinValue;
                sourceBytesRead = 0;
                lastSourceStatsBytes = 0;
                sourcePrefix = Array.Empty<byte>();
                decodedFrameCount = 0;
                forwardedSampleCount = 0;
                lastSourceStatsMs = nowMs;
            }

            Stream audioStream = request.Body;

            if (TryGetInlineMetadataInterval(request.Headers, out int inlineMetadataInterval))
            {
                Log.Information("sdrtrunk source is using inline metadata every {interval} byte(s)", inlineMetadataInterval);
                audioStream = new InlineMetadataReadStream(request.Body, inlineMetadataInterval, HandleInlineMetadata);
            }

            try
            {
                await Task.Run(() => DecodeMp3Stream(audioStream, token), token);
            }
            finally
            {
                lock (stateLock)
                {
                    sourceActive = false;
                }

                Log.Debug(
                    "sdrtrunk source finished: bytesRead={bytes}, decodedFrames={frames}, forwardedSamples={samples}, firstBytes={firstBytes}",
                    sourceBytesRead,
                    decodedFrameCount,
                    forwardedSampleCount,
                    FormatBytes(sourcePrefix));
            }
        }

        private void DecodeMp3Stream(Stream sourceStream, CancellationToken token)
        {
            AcmMp3FrameDecompressor decompressor = null;
            byte[] buffer = null;
            WaveFormat waveFormat = null;

            try
            {
                using PositionTrackingReadStream mp3Stream = new PositionTrackingReadStream(sourceStream, OnSourceBytesRead);

                while (!token.IsCancellationRequested)
                {
                    long bytesBeforeFrame = Volatile.Read(ref sourceBytesRead);
                    Mp3Frame frame = Mp3Frame.LoadFromStream(mp3Stream);
                    if (frame == null)
                    {
                        long bytesAfterFrame = Volatile.Read(ref sourceBytesRead);
                        if (bytesAfterFrame > bytesBeforeFrame)
                        {
                            Log.Warning(
                                "sdrtrunk source skipped {bytes} non-MP3 byte(s) while looking for a frame; totalBytes={totalBytes}, firstBytes={firstBytes}",
                                bytesAfterFrame - bytesBeforeFrame,
                                bytesAfterFrame,
                                FormatBytes(sourcePrefix));
                            continue;
                        }

                        Log.Warning(
                            "sdrtrunk source ended before another MP3 frame was available; totalBytes={totalBytes}, decodedFrames={frames}, firstBytes={firstBytes}",
                            bytesAfterFrame,
                            decodedFrameCount,
                            FormatBytes(sourcePrefix));
                        break;
                    }

                    if (decompressor == null)
                    {
                        int channels = frame.ChannelMode == ChannelMode.Mono ? 1 : 2;
                        WaveFormat mp3Format = new Mp3WaveFormat(frame.SampleRate, channels, frame.FrameLength, frame.BitRate);
                        decompressor = new AcmMp3FrameDecompressor(mp3Format);
                        waveFormat = decompressor.OutputFormat;
                        buffer = new byte[waveFormat.AverageBytesPerSecond];

                        Log.Information(
                            "Decoding sdrtrunk MP3 source as {sampleRate} Hz, {channels} channel(s)",
                            waveFormat.SampleRate,
                            waveFormat.Channels);
                    }

                    int read = decompressor.DecompressFrame(frame, buffer, 0);
                    if (read <= 0)
                    {
                        continue;
                    }

                    decodedFrameCount++;
                    lastDecodedFrameMs = Environment.TickCount64;
                    short[] monoSamples = ConvertPcm16ToMono(buffer, read, waveFormat);
                    short[] outputSamples = ResampleLinear(monoSamples, waveFormat.SampleRate, streamConfig.OutputSampleRate);
                    ProcessSamples(outputSamples);
                }
            }
            finally
            {
                decompressor?.Dispose();
            }
        }

        private void ProcessSamples(short[] samples)
        {
            if (samples == null || samples.Length == 0)
            {
                return;
            }

            long nowMs = Environment.TickCount64;
            double levelDb = CalculateRmsDb(samples);
            bool shouldForward;
            bool statusChanged = false;
            bool displayChanged = false;
            bool becameActive = false;
            RadioState nextState;

            lock (stateLock)
            {
                if (!started)
                {
                    return;
                }

                bool wasReceiving = IsReceiving();
                statusChanged = UpdateGate(levelDb, nowMs, out displayChanged);
                bool receiving = IsReceiving();
                nextState = receiving ? RadioState.Receiving : RadioState.Idle;
                becameActive = !wasReceiving && nextState == RadioState.Receiving;

                if (Status.State != nextState)
                {
                    Status.State = nextState;
                    statusChanged = true;
                }

                shouldForward = receiving;
            }

            if (statusChanged || displayChanged)
            {
                if (statusChanged)
                {
                    Log.Debug("sdrtrunk stream level {level:0.0} dBFS changed radio state to {state}", levelDb, nextState);
                }

                RadioStatusCallback();
            }

            if (shouldForward)
            {
                if (becameActive)
                {
                    Log.Debug("sdrtrunk stream started forwarding audio at {level:0.0} dBFS", levelDb);
                }

                RxSendPCM16Samples(samples, (uint)streamConfig.OutputSampleRate);
                forwardedSampleCount += samples.Length;
            }

            LogAudioStats(levelDb, nowMs);
        }

        private void HandleMetadata(IcecastSourceRequest request)
        {
            string song = GetQueryValue(request.Mount, "song");
            ApplyMetadataTitle(song, "metadata");
        }

        private void HandleInlineMetadata(string metadata)
        {
            string song = GetInlineMetadataTitle(metadata);
            if (string.IsNullOrWhiteSpace(song))
            {
                Log.Debug("sdrtrunk inline metadata received without StreamTitle: {metadata}", metadata);
                return;
            }

            ApplyMetadataTitle(song, "inline metadata");
        }

        private void ApplyMetadataTitle(string song, string source)
        {
            bool active = !string.IsNullOrWhiteSpace(song) &&
                !song.Equals("Scanning...", StringComparison.OrdinalIgnoreCase);

            bool statusChanged = false;
            bool displayChanged = false;
            RadioState nextState;
            long nowMs = Environment.TickCount64;

            lock (stateLock)
            {
                if (active)
                {
                    string callerId = ExtractCallerId(song);
                    bool titleChanged = !string.Equals(song, lastMetadataTitle, StringComparison.Ordinal);
                    bool audioRecent = HasRecentAudioActivity(nowMs);

                    if (titleChanged || audioRecent)
                    {
                        metadataActive = true;
                        lastMetadataActiveMs = nowMs;
                        lastMetadataTitle = song;
                        displayChanged =
                            !string.Equals(Status.ChannelName, song, StringComparison.Ordinal) ||
                            !string.Equals(Status.CallerId, callerId, StringComparison.Ordinal);
                        Status.ChannelName = song;
                        Status.CallerId = callerId;
                    }

                    if (!sourceActive && (titleChanged || audioRecent))
                    {
                        Log.Warning("sdrtrunk metadata indicates an active call but no SOURCE audio stream is connected");
                    }
                }
                else
                {
                    // Keep the displayed talkgroup while queued audio drains, but allow the
                    // next real call on the same talkgroup to be accepted as fresh metadata.
                    if (metadataActive && lastMetadataActiveMs != long.MinValue)
                    {
                        lastMetadataActiveMs = Math.Min(lastMetadataActiveMs, nowMs - Math.Max(1, streamConfig.HangMs));
                    }

                    lastMetadataTitle = "";

                    statusChanged = ExpireGate(nowMs, out bool expiredDisplayChanged);
                    displayChanged |= expiredDisplayChanged;
                }

                nextState = IsReceiving() ? RadioState.Receiving : RadioState.Idle;
                if (started && Status.State != nextState)
                {
                    Status.State = nextState;
                    statusChanged = true;
                }
            }

            Log.Debug("sdrtrunk {source} changed active={active}: {song}", source, active, string.IsNullOrWhiteSpace(song) ? "(empty)" : song);

            if (statusChanged || displayChanged)
            {
                RadioStatusCallback();
            }
        }

        private bool UpdateGate(double levelDb, long nowMs, out bool displayChanged)
        {
            displayChanged = false;

            if (levelDb >= streamConfig.RxThresholdDb)
            {
                lastAboveThresholdMs = nowMs;
                aboveThresholdSinceMs ??= nowMs;

                if (!gateActive && nowMs - aboveThresholdSinceMs.Value >= Math.Max(0, streamConfig.AttackMs))
                {
                    gateActive = true;
                }

                return false;
            }

            aboveThresholdSinceMs = null;
            return ExpireGate(nowMs, out displayChanged);
        }

        private void ExpireGate()
        {
            bool statusChanged = false;
            bool callbackNeeded = false;

            lock (stateLock)
            {
                if (!started)
                {
                    return;
                }

                long nowMs = Environment.TickCount64;
                statusChanged = ExpireGate(nowMs, out bool displayChanged);
                LogSourceStats(nowMs);
                CheckSourceWatchdog(nowMs);
                callbackNeeded = statusChanged || displayChanged;
            }

            if (callbackNeeded)
            {
                if (statusChanged)
                {
                    Log.Debug("sdrtrunk stream hang timer changed radio state to Idle");
                }

                RadioStatusCallback();
            }
        }

        private bool ExpireGate(long nowMs)
        {
            return ExpireGate(nowMs, out _);
        }

        private bool ExpireGate(long nowMs, out bool displayChanged)
        {
            displayChanged = false;

            if (gateActive && lastAboveThresholdMs != long.MinValue && nowMs - lastAboveThresholdMs >= Math.Max(1, streamConfig.HangMs))
            {
                gateActive = false;
                aboveThresholdSinceMs = null;
            }

            if (metadataActive &&
                !gateActive &&
                lastMetadataActiveMs != long.MinValue &&
                nowMs - lastMetadataActiveMs >= Math.Max(1, streamConfig.HangMs))
            {
                metadataActive = false;
                displayChanged =
                    !string.Equals(Status.ChannelName, streamConfig.ChannelName, StringComparison.Ordinal) ||
                    !string.IsNullOrEmpty(Status.CallerId);
                Status.ChannelName = streamConfig.ChannelName;
                Status.CallerId = "";
            }

            RadioState nextState = IsReceiving() ? RadioState.Receiving : RadioState.Idle;
            if (Status.State != nextState)
            {
                Status.State = nextState;
                return true;
            }

            return false;
        }

        private void ResetGate()
        {
            gateActive = false;
            metadataActive = false;
            aboveThresholdSinceMs = null;
            lastAboveThresholdMs = long.MinValue;
            lastMetadataActiveMs = long.MinValue;
            lastMetadataTitle = "";
        }

        private bool IsReceiving()
        {
            return gateActive;
        }

        private bool HasRecentAudioActivity(long nowMs)
        {
            return gateActive ||
                (lastAboveThresholdMs != long.MinValue &&
                nowMs - lastAboveThresholdMs < Math.Max(1, streamConfig.HangMs));
        }

        private void LogAudioStats(double levelDb, long nowMs)
        {
            if (lastAudioStatsMs != long.MinValue && nowMs - lastAudioStatsMs < 2000)
            {
                return;
            }

            lastAudioStatsMs = nowMs;
            Log.Debug(
                "sdrtrunk audio stats: level {level:0.0} dBFS, receiving={receiving}, decodedFrames={frames}, forwardedSamples={samples}",
                levelDb,
                IsReceiving(),
                decodedFrameCount,
                forwardedSampleCount);
        }

        private void LogSourceStats(long nowMs)
        {
            if (!sourceActive || nowMs - lastSourceStatsMs < 5000)
            {
                return;
            }

            long bytesSinceLast = sourceBytesRead - lastSourceStatsBytes;
            lastSourceStatsBytes = sourceBytesRead;
            lastSourceStatsMs = nowMs;

            Log.Debug(
                "sdrtrunk source stats: bytesRead={bytes}, bytesLast5s={bytesLast5s}, decodedFrames={frames}, forwardedSamples={samples}",
                sourceBytesRead,
                bytesSinceLast,
                decodedFrameCount,
                forwardedSampleCount);
        }

        private void OnSourceBytesRead(byte[] buffer, int offset, int bytesRead)
        {
            if (bytesRead <= 0)
            {
                return;
            }

            Interlocked.Add(ref sourceBytesRead, bytesRead);
            Interlocked.Exchange(ref lastSourceByteMs, Environment.TickCount64);

            lock (stateLock)
            {
                if (sourcePrefix.Length >= 64)
                {
                    return;
                }

                int copyLength = Math.Min(bytesRead, 64 - sourcePrefix.Length);
                byte[] nextPrefix = new byte[sourcePrefix.Length + copyLength];
                Buffer.BlockCopy(sourcePrefix, 0, nextPrefix, 0, sourcePrefix.Length);
                Buffer.BlockCopy(buffer, offset, nextPrefix, sourcePrefix.Length, copyLength);
                sourcePrefix = nextPrefix;
            }
        }

        private void CheckSourceWatchdog(long nowMs)
        {
            if (!sourceActive || sourceRestartRequested || streamConfig.SourceNoAudioRestartMs <= 0)
            {
                return;
            }

            long thresholdMs = Math.Max(1000, streamConfig.SourceNoAudioRestartMs);
            bool noDecodedFrames = decodedFrameCount == 0 && sourceConnectedAtMs != long.MinValue && nowMs - sourceConnectedAtMs >= thresholdMs;
            bool decodedFramesStalled = decodedFrameCount > 0 && lastDecodedFrameMs != long.MinValue && nowMs - lastDecodedFrameMs >= thresholdMs;

            if (!noDecodedFrames && !decodedFramesStalled)
            {
                return;
            }

            sourceRestartRequested = true;
            string reason = noDecodedFrames
                ? $"no decodable MP3 frames after {thresholdMs} ms; bytesRead={sourceBytesRead}, lastByteMs={lastSourceByteMs}, firstBytes={FormatBytes(sourcePrefix)}"
                : $"no decodable MP3 frames for {thresholdMs} ms; bytesRead={sourceBytesRead}, decodedFrames={decodedFrameCount}";

            Log.Warning("sdrtrunk source watchdog restarting stale source: {reason}", reason);
            sourceServer.CloseActiveSource(reason);
        }

        private static string FormatBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return "(none)";
            }

            return BitConverter.ToString(bytes);
        }

        private static string GetQueryValue(string pathAndQuery, string key)
        {
            int queryStart = pathAndQuery.IndexOf('?');
            if (queryStart < 0 || queryStart == pathAndQuery.Length - 1)
            {
                return "";
            }

            string query = pathAndQuery.Substring(queryStart + 1);
            string[] parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                int separator = part.IndexOf('=');
                string partKey = separator >= 0 ? part.Substring(0, separator) : part;

                if (!partKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = separator >= 0 ? part.Substring(separator + 1) : "";
                return WebUtility.UrlDecode(value.Replace("+", " "));
            }

            return "";
        }

        private static string ExtractCallerId(string song)
        {
            Match match = Regex.Match(song, @"FROM:([^ ]+)");
            return match.Success ? match.Groups[1].Value : "";
        }

        private static bool TryGetInlineMetadataInterval(Dictionary<string, string> headers, out int interval)
        {
            interval = 0;

            if (!headers.TryGetValue("icy-metaint", out string value))
            {
                return false;
            }

            return int.TryParse(value, out interval) && interval > 0;
        }

        private static string GetInlineMetadataTitle(string metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata))
            {
                return "";
            }

            Match match = Regex.Match(metadata, @"StreamTitle='(?<title>[^']*)';", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["title"].Value : "";
        }

        private static short[] ConvertPcm16ToMono(byte[] buffer, int byteCount, WaveFormat waveFormat)
        {
            int channels = Math.Max(1, waveFormat.Channels);
            int frameSize = channels * sizeof(short);
            int frameCount = byteCount / frameSize;
            short[] samples = new short[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                int sum = 0;
                int frameOffset = frame * frameSize;

                for (int channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToInt16(buffer, frameOffset + (channel * sizeof(short)));
                }

                samples[frame] = (short)(sum / channels);
            }

            return samples;
        }

        private static short[] ResampleLinear(short[] samples, int inputRate, int outputRate)
        {
            if (samples.Length == 0 || inputRate == outputRate)
            {
                return samples;
            }

            int outputLength = Math.Max(1, (int)Math.Round(samples.Length * (double)outputRate / inputRate));
            short[] output = new short[outputLength];
            double ratio = (double)inputRate / outputRate;

            for (int i = 0; i < outputLength; i++)
            {
                double sourceIndex = i * ratio;
                int index = (int)sourceIndex;
                double fraction = sourceIndex - index;

                if (index >= samples.Length - 1)
                {
                    output[i] = samples[samples.Length - 1];
                    continue;
                }

                double sample = samples[index] + ((samples[index + 1] - samples[index]) * fraction);
                output[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
            }

            return output;
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

        private sealed class PositionTrackingReadStream : Stream
        {
            private readonly Stream inner;
            private readonly Action<byte[], int, int> bytesReadCallback;

            public PositionTrackingReadStream(Stream inner, Action<byte[], int, int> bytesReadCallback)
            {
                this.inner = inner;
                this.bytesReadCallback = bytesReadCallback;
            }

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get; set; }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = inner.Read(buffer, offset, count);
                Position += read;
                bytesReadCallback?.Invoke(buffer, offset, read);
                return read;
            }

            public override int ReadByte()
            {
                int value = inner.ReadByte();
                if (value >= 0)
                {
                    Position++;
                    byte[] buffer = new byte[] { (byte)value };
                    bytesReadCallback?.Invoke(buffer, 0, 1);
                }

                return value;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class InlineMetadataReadStream : Stream
        {
            private readonly Stream inner;
            private readonly int interval;
            private readonly Action<string> metadataCallback;
            private int audioBytesUntilMetadata;

            public InlineMetadataReadStream(Stream inner, int interval, Action<string> metadataCallback)
            {
                this.inner = inner;
                this.interval = interval;
                this.metadataCallback = metadataCallback;
                audioBytesUntilMetadata = interval;
            }

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get; set; }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int totalRead = 0;

                while (totalRead < count)
                {
                    if (audioBytesUntilMetadata == 0)
                    {
                        ReadMetadataBlock();
                        audioBytesUntilMetadata = interval;
                        continue;
                    }

                    int readTarget = Math.Min(count - totalRead, audioBytesUntilMetadata);
                    int read = inner.Read(buffer, offset + totalRead, readTarget);
                    if (read <= 0)
                    {
                        return totalRead > 0 ? totalRead : read;
                    }

                    totalRead += read;
                    Position += read;
                    audioBytesUntilMetadata -= read;
                }

                return totalRead;
            }

            public override int ReadByte()
            {
                if (audioBytesUntilMetadata == 0)
                {
                    ReadMetadataBlock();
                    audioBytesUntilMetadata = interval;
                }

                int value = inner.ReadByte();
                if (value >= 0)
                {
                    Position++;
                    audioBytesUntilMetadata--;
                }

                return value;
            }

            private void ReadMetadataBlock()
            {
                int lengthByte = inner.ReadByte();
                if (lengthByte < 0)
                {
                    throw new EndOfStreamException();
                }

                int metadataLength = lengthByte * 16;
                if (metadataLength == 0)
                {
                    return;
                }

                byte[] metadataBytes = new byte[metadataLength];
                int offset = 0;

                while (offset < metadataLength)
                {
                    int read = inner.Read(metadataBytes, offset, metadataLength - offset);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException();
                    }

                    offset += read;
                }

                string metadata = Encoding.UTF8.GetString(metadataBytes).TrimEnd('\0');
                metadataCallback?.Invoke(metadata);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
