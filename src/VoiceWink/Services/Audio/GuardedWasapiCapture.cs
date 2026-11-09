// Adapted from NAudio 2.2.1 NAudio.Wasapi/WasapiCapture.cs (https://github.com/naudio/NAudio).
//
// Copyright 2020 Mark Heath
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
// associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute,
// sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
// NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
// OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace VoiceWink.Services.Audio;

/// <summary>NAudio 2.2.1's shared-mode WasapiCapture, except nothing on its capture thread can end the process (AUD-42).</summary>
// shortcut: vendored NAudio 2.2.1 WasapiCapture, delete when NAudio moves to 3.x (guarded upstream since v3.0.1).
internal sealed class GuardedWasapiCapture : IWaveIn
{
    private const long ReftimesPerSec = 10000000;
    private const long ReftimesPerMillisec = 10000;
    private const int AudioBufferMillisecondsLength = 100;

    private volatile CaptureState _captureState;
    private byte[] _recordBuffer = [];
    private Thread? _captureThread;
    private AudioClient? _audioClient;
    private int _bytesPerFrame;
    private WaveFormat _waveFormat;
    private bool _initialized;
    private readonly SynchronizationContext? _syncContext;

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public GuardedWasapiCapture()
        : this(GetDefaultCaptureDevice())
    {
    }

    public GuardedWasapiCapture(MMDevice captureDevice)
    {
        _syncContext = SynchronizationContext.Current;
        _audioClient = captureDevice.AudioClient;
        _waveFormat = _audioClient.MixFormat;
    }

    public WaveFormat WaveFormat
    {
        get => _waveFormat.AsStandardWaveFormat();
        set => _waveFormat = value;
    }

    private static MMDevice GetDefaultCaptureDevice()
    {
        var devices = new MMDeviceEnumerator();
        return devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    private void InitializeCaptureDevice(AudioClient client)
    {
        if (_initialized)
            return;

        client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            ReftimesPerMillisec * AudioBufferMillisecondsLength,
            0,
            _waveFormat,
            Guid.Empty);

        int bufferFrameCount = client.BufferSize;
        _bytesPerFrame = _waveFormat.Channels * _waveFormat.BitsPerSample / 8;
        _recordBuffer = new byte[bufferFrameCount * _bytesPerFrame];
        _initialized = true;
    }

    public void StartRecording()
    {
        if (_captureState != CaptureState.Stopped)
            throw new InvalidOperationException("Previous recording still in progress");

        var client = _audioClient ?? throw new ObjectDisposedException(nameof(GuardedWasapiCapture));
        _captureState = CaptureState.Starting;
        InitializeCaptureDevice(client);
        _captureThread = new Thread(() => CaptureThread(client))
        {
            IsBackground = true,
        };
        _captureThread.Start();
    }

    public void StopRecording()
    {
        if (_captureState != CaptureState.Stopped)
            _captureState = CaptureState.Stopping;
    }

    private void CaptureThread(AudioClient client) =>
        RunCaptureThread(
            () => DoRecording(client),
            client.Stop,
            exception =>
            {
                // Cleared before the raise so a handler's Dispose never joins the thread it runs on.
                _captureThread = null;
                _captureState = CaptureState.Stopped;
                RaiseRecordingStopped(exception);
            });

    /// <summary>The capture thread's body: record, always stop, then report; never throws (AUD-42).</summary>
    internal static void RunCaptureThread(Action record, Action stop, Action<Exception?> raiseStopped)
    {
        Exception? exception = null;
        try
        {
            record();
        }
        catch (Exception e)
        {
            exception = e;
        }
        finally
        {
            // AUDCLNT_E_DEVICE_INVALIDATED lands here when the endpoint is gone.
            try { stop(); }
            catch (Exception e) { exception ??= e; }
        }

        try
        {
            raiseStopped(exception);
        }
        catch (Exception e)
        {
            Log.ForContext<GuardedWasapiCapture>().Error(e, "Capture RecordingStopped handler threw on the capture thread (AUD-42)");
        }
    }

    private void DoRecording(AudioClient client)
    {
        int bufferFrameCount = client.BufferSize;
        long actualDuration = (long)((double)ReftimesPerSec * bufferFrameCount / _waveFormat.SampleRate);
        int sleepMilliseconds = (int)(actualDuration / ReftimesPerMillisec / 2);

        var capture = client.AudioCaptureClient;
        client.Start();
        if (_captureState == CaptureState.Starting)
            _captureState = CaptureState.Capturing;

        while (_captureState == CaptureState.Capturing)
        {
            Thread.Sleep(sleepMilliseconds);
            if (_captureState != CaptureState.Capturing)
                break;

            ReadNextPacket(capture);
        }
    }

    private void RaiseRecordingStopped(Exception? e)
    {
        var handler = RecordingStopped;
        if (handler == null)
            return;

        if (_syncContext == null)
            handler(this, new StoppedEventArgs(e));
        else
            _syncContext.Post(_ => handler(this, new StoppedEventArgs(e)), null);
    }

    private void ReadNextPacket(AudioCaptureClient capture)
    {
        int packetSize = capture.GetNextPacketSize();
        int recordBufferOffset = 0;

        while (packetSize != 0)
        {
            IntPtr buffer = capture.GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags);
            int bytesAvailable = framesAvailable * _bytesPerFrame;

            int spaceRemaining = Math.Max(0, _recordBuffer.Length - recordBufferOffset);
            if (spaceRemaining < bytesAvailable && recordBufferOffset > 0)
            {
                DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, recordBufferOffset));
                recordBufferOffset = 0;
            }

            if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent)
                Marshal.Copy(buffer, _recordBuffer, recordBufferOffset, bytesAvailable);
            else
                Array.Clear(_recordBuffer, recordBufferOffset, bytesAvailable);

            recordBufferOffset += bytesAvailable;
            capture.ReleaseBuffer(framesAvailable);
            packetSize = capture.GetNextPacketSize();
        }

        DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, recordBufferOffset));
    }

    public void Dispose()
    {
        StopRecording();
        // One read: the capture thread nulls the field as it finishes, so a re-read could be null (AUD-42).
        var thread = _captureThread;
        if (thread != null)
        {
            thread.Join();
            _captureThread = null;
        }

        if (_audioClient != null)
        {
            _audioClient.Dispose();
            _audioClient = null;
        }
    }
}
