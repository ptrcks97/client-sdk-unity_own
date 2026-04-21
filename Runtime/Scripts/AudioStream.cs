using System;
using UnityEngine;
using LiveKit.Internal;
using LiveKit.Proto;
using System.Runtime.InteropServices;
using LiveKit.Internal.FFIClients.Requests;

namespace LiveKit
{
    /// <summary>
    /// An audio stream from a remote participant, attached to an <see cref="AudioSource"/>
    /// in the scene.
    /// </summary>
    public sealed class AudioStream : IDisposable
    {
        internal readonly FfiHandle Handle;
        private readonly AudioSource _audioSource;
        private RingBuffer _buffer;
        private short[] _tempBuffer;
        private uint _numChannels;
        private uint _sampleRate;

        private AudioResampler _resampler = new AudioResampler();
        private object _lock = new object();
        private bool _disposed = false;

        private uint _targetChannels;

        private uint _targetSampleRate;

        private AudioClip _streamClip;

        /// <summary>
        /// Creates a new audio stream from a remote audio track, attaching it to the
        /// given <see cref="AudioSource"/> in the scene.
        /// </summary>
        /// <param name="audioTrack">The remote audio track to stream.</param>
        /// <param name="source">The audio source to play the stream on.</param>
        /// <exception cref="InvalidOperationException">Thrown if the audio track's room or
        /// participant is invalid.</exception>
        public AudioStream(RemoteAudioTrack audioTrack, AudioSource source)
        {
            if (!audioTrack.Room.TryGetTarget(out var room))
                throw new InvalidOperationException("audiotrack's room is invalid");

            if (!audioTrack.Participant.TryGetTarget(out var participant))
                throw new InvalidOperationException("audiotrack's participant is invalid");

            using var request = FFIBridge.Instance.NewRequest<NewAudioStreamRequest>();
            var newAudioStream = request.request;
            newAudioStream.TrackHandle = (ulong)(audioTrack as ITrack).TrackHandle.DangerousGetHandle();
            newAudioStream.Type = AudioStreamType.AudioStreamNative;

            using var response = request.Send();
            FfiResponse res = response;
            Handle = FfiHandle.FromOwnedHandle(res.NewAudioStream.Stream.Handle);
            FfiClient.Instance.AudioStreamEventReceived += OnAudioStreamEvent;

            //Eigene Implementation
            _audioSource = source;
            //Es wird immer die SampleRate von Unity benutzt
            _targetSampleRate=(uint)AudioSettings.outputSampleRate;
            _targetChannels = 2;

            _numChannels = _targetChannels;
            _sampleRate = _targetSampleRate;

            int ringBufferSizeBytes = (int)(_targetChannels * _targetSampleRate * 0.7f * sizeof(short));
            _buffer = new RingBuffer(ringBufferSizeBytes);

            int clipLengthSamples = (int)_targetSampleRate;
            
            _streamClip = AudioClip.Create(
                name: "LiveKitStream",
                lengthSamples: clipLengthSamples,
                channels: (int)_targetChannels,
                frequency: (int)_targetSampleRate,
                stream: true,
                pcmreadercallback: OnClipRead
            );
            _audioSource.clip = _streamClip;
            _audioSource.loop = true;
            _audioSource.Play();
        }

        private void OnClipRead(float[] data)
        {
            lock (_lock)
            {
                if(_buffer == null)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                if(_tempBuffer == null || _tempBuffer.Length != data.Length)
                {
                    _tempBuffer = new short[data.Length];
                }
                var tempBytes = MemoryMarshal.Cast<short, byte>(_tempBuffer.AsSpan().Slice(0, data.Length));
                int readBytes = _buffer.Read(tempBytes);
                int readSamples = readBytes / sizeof(short);
                Array.Clear(data, 0, data.Length);
                for(int i = 0; i<readSamples; i++)
                {
                    data[i] = _tempBuffer[i] / 32768f;
                }
            }
        }

        // Called on Unity audio thread
        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            lock (_lock)
            {
                if (_buffer == null || channels != _numChannels || sampleRate != _sampleRate || data.Length != _tempBuffer.Length)
                {
                    int size = (int)(channels * sampleRate * 0.2);
                    _buffer?.Dispose();
                    _buffer = new RingBuffer(size * sizeof(short));
                    _tempBuffer = new short[data.Length];
                    _numChannels = (uint)channels;
                    _sampleRate = (uint)sampleRate;
                }

                static float S16ToFloat(short v)
                {
                    return v / 32768f;
                }

                // "Send" the data to Unity
                var temp = MemoryMarshal.Cast<short, byte>(_tempBuffer.AsSpan().Slice(0, data.Length));
                int read = _buffer.Read(temp);

                Array.Clear(data, 0, data.Length);
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = S16ToFloat(_tempBuffer[i]);
                }
            }
        }

        private void OnAudioStreamEvent(AudioStreamEvent e)
        {
            if ((ulong)Handle.DangerousGetHandle() != e.StreamHandle)
                return;

            if (e.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived)
                return;

            var frame = new AudioFrame(e.FrameReceived.Frame);

            lock (_lock)
            {
                if (_numChannels == 0)
                    return;

                unsafe
                {
                    var uFrame = _resampler.RemixAndResample(frame, _numChannels, _sampleRate);
                    if (uFrame != null)
                    {
                        var data = new Span<byte>(uFrame.Data.ToPointer(), uFrame.Length);
                        _buffer?.Write(data);
                    }

                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _audioSource.Stop();
                UnityEngine.Object.Destroy(_audioSource.GetComponent<AudioProbe>());
            }
            _disposed = true;
        }

        ~AudioStream()
        {
            Dispose(false);
        }
    }
}
