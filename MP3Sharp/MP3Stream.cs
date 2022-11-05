// /***************************************************************************
//  * MP3Stream.cs
//  * Copyright (c) 2015, 2021 The Authors.
//  * 
//  * All rights reserved. This program and the accompanying materials
//  * are made available under the terms of the GNU Lesser General Public License
//  * (LGPL) version 3 which accompanies this distribution, and is available at
//  * https://www.gnu.org/licenses/lgpl-3.0.en.html
//  *
//  * This library is distributed in the hope that it will be useful,
//  * but WITHOUT ANY WARRANTY; without even the implied warranty of
//  * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  * Lesser General Public License for more details.
//  *
//  ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MP3Sharp.Decoding;

namespace MP3Sharp {
    /// <summary>
    /// Provides a view of the sequence of bytes that are produced during the conversion of an MP3 stream
    /// into a 16-bit PCM-encoded ("WAV" format) stream.
    /// </summary>
    public class MP3Stream : Stream {
        // Used to interface with JavaZoom code.
        private readonly Bitstream _BitStream;

        private readonly Decoder _Decoder = new Decoder(Decoder.DefaultParams);

        // local variables.
        private readonly Buffer16BitStereo _Buffer;
        private readonly Stream _SourceStream;
        private const int BACK_STREAM_BYTE_COUNT_REP = 0;
        private short _ChannelCountRep = -1;
        private readonly SoundFormat FormatRep;
        private int _FrequencyRep = -1;
        private long _DecodeOffset = 0; // PCM offset
        private List<long> _SortedKnownFrameOffsets = new List<long>(); // sorted PCM offsets, can be used for seeking
        private Dictionary<long, long> _FrameOffsets = new Dictionary<long, long>(); // key: PCM offset, value: sourcestream offset

        public bool IsEOF { get; protected set; }

        /// <summary>
        /// Creates a new stream instance using the provided filename, and the default chunk size of 4096 bytes.
        /// </summary>
        public MP3Stream(string fileName)
            : this(new FileStream(fileName, FileMode.Open, FileAccess.Read)) { }

        /// <summary>
        /// Creates a new stream instance using the provided filename and chunk size.
        /// </summary>
        public MP3Stream(string fileName, int chunkSize)
            : this(new FileStream(fileName, FileMode.Open, FileAccess.Read), chunkSize) { }

        /// <summary>
        /// Creates a new stream instance using the provided stream as a source, and the default chunk size of 4096 bytes.
        /// </summary>
        public MP3Stream(Stream sourceStream) : this(sourceStream, 4096) { }

        /// <summary>
        /// Creates a new stream instance using the provided stream as a source.
        /// Will also read the first frame of the MP3 into the internal buffer.
        /// </summary>
        public MP3Stream(Stream sourceStream, int chunkSize) {
            IsEOF = false;
            _SourceStream = sourceStream;
            _BitStream = new Bitstream(new PushbackStream(_SourceStream, chunkSize));
            _Buffer = new Buffer16BitStereo();
            _Decoder.OutputBuffer = _Buffer;
            // read the first frame. This will fill the initial buffer with data, and get our frequency!
            IsEOF |= !ReadFrame();
            switch (_ChannelCountRep) {
                case 1:
                    FormatRep = SoundFormat.Pcm16BitMono;
                    break;
                case 2:
                    FormatRep = SoundFormat.Pcm16BitStereo;
                    break;
                default:
                    throw new MP3SharpException($"Unhandled channel count rep: {_ChannelCountRep} (allowed values are 1-mono and 2-stereo).");
            }
            if (FormatRep == SoundFormat.Pcm16BitMono) {
                _Buffer.DoubleMonoToStereo = true;
            }
        }

        /// <summary>
        /// Gets the chunk size.
        /// </summary>
        internal int ChunkSize => BACK_STREAM_BYTE_COUNT_REP;

        /// <summary>
        /// Gets a value indicating whether the current stream supports reading.
        /// </summary>
        public override bool CanRead => _SourceStream.CanRead;

        /// <summary>
        /// Gets a value indicating whether the current stream supports seeking.
        /// </summary>
        public override bool CanSeek => _SourceStream.CanSeek;

        /// <summary>
        /// Gets a value indicating whether the current stream supports writing.
        /// </summary>
        public override bool CanWrite => _SourceStream.CanWrite;

        /// <summary>
        /// Gets the length in bytes of the stream.
        /// </summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>
        /// Gets or sets the position of the source stream.
        /// This is relative to the total number of PCM bytes contained in the Mp3Stream's output.
        /// </summary>
        public override long Position {
            get => _DecodeOffset;
            set {
                Seek(value, SeekOrigin.Begin);
            }
        }

        /// <summary>
        /// Gets the frequency of the audio being decoded. Updated every call to Read() or DecodeFrames(),
        /// to reflect the most recent header information from the MP3 Stream.
        /// </summary>
        public int Frequency => _FrequencyRep;

        /// <summary>
        /// Gets the number of channels available in the audio being decoded. Updated every call to Read() or DecodeFrames(),
        /// to reflect the most recent header information from the MP3 Stream.
        /// </summary>
        internal short ChannelCount => _ChannelCountRep;

        /// <summary>
        /// Gets the PCM output format of this stream.
        /// </summary>
        internal SoundFormat Format => FormatRep;

        /// <summary>
        /// Clears all buffers for this stream and causes any buffered data to be written to the underlying device.
        /// </summary>
        public override void Flush() {
            _SourceStream.Flush();
        }

        /// <summary>
        /// Sets the position of the source stream.
        /// </summary>
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = Position + offset; break;
                case SeekOrigin.End: throw new NotSupportedException();

                default: throw new ArgumentException("Invalid SeekOrigin", nameof(origin));
            };

            if (target < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Attempted to Seek before the beginning of the stream");
            }

            _DecodeOffset = FindNearestFloor(target);

            _SourceStream.Position = _FrameOffsets[_DecodeOffset];
            IsEOF = false;
            IsEOF |= !ReadFrame();

            var difference = target - _DecodeOffset;
            if (difference > 0)
            {
                var b = new byte[difference];
                Read(b, 0, (int)difference);
            }

            return target;
        }

        private long FindNearestFloor(long value)
        {
            var i = 0;
            var j = _SortedKnownFrameOffsets.Count - 1;
            while (true)
            {
                var d = j - i;
                if (d < 2)
                {
                    return _SortedKnownFrameOffsets[j] > value
                        ? _SortedKnownFrameOffsets[i]
                        : _SortedKnownFrameOffsets[j];
                }

                var m = i + (d >> 1);
                var v = _SortedKnownFrameOffsets[m];
                if (v < value)
                {
                    i = m;
                }
                else if (v > value)
                {
                    j = m - 1;
                }
                else
                {
                    return value;
                }
            }
        }

        /// <summary>
        /// This method is not valid for an Mp3Stream.
        /// </summary>
        public override void SetLength(long value) {
            throw new InvalidOperationException();
        }

        /// <summary>
        /// This method is not valid for an Mp3Stream.
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            throw new InvalidOperationException();
        }

        /// <summary>
        /// Decodes the requested number of frames from the MP3 stream and caches their PCM-encoded bytes.
        /// These can subsequently be obtained using the Read method.
        /// Returns the number of frames that were successfully decoded.
        /// </summary>
        internal int DecodeFrames(int frameCount) {
            int framesDecoded = 0;
            bool aFrameWasRead = true;
            while (framesDecoded < frameCount && aFrameWasRead) {
                aFrameWasRead = ReadFrame();
                if (aFrameWasRead) framesDecoded++;
            }
            return framesDecoded;
        }

        /// <summary>
        /// Reads the MP3 stream as PCM-encoded bytes.  Decodes a portion of the stream if necessary.
        /// Returns the number of bytes read.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            // Copy from queue buffers, reading new ones as necessary,
            // until we can't read more or we have read "count" bytes
            if (IsEOF)
                return 0;

            int bytesRead = 0;
            while (true) {
                if (_Buffer.BytesLeft <= 0) {
                    if (!ReadFrame()) // out of frames or end of stream?
                    {
                        IsEOF = true;
                        _BitStream.CloseFrame();
                        break;
                    }
                }

                // Copy as much as we can from the current buffer:
                var read = _Buffer.Read(buffer,
                    offset + bytesRead,
                    count - bytesRead);
                bytesRead += read;
                _DecodeOffset += read;

                if (bytesRead >= count)
                    break;
            }
            return bytesRead;
        }

        /// <summary>
        /// Closes the source stream and releases any associated resources.
        /// If you don't call this, you may be leaking file descriptors.
        /// </summary>
        public override void Close() {
            _BitStream.Close(); // This should close SourceStream as well.
        }

        /// <summary>
        /// Reads a frame from the MP3 stream.  Returns whether the operation was successful.  If it wasn't,
        /// the source stream is probably at its end.
        /// </summary>
        private bool ReadFrame() {
            // Read a frame from the bitstream.
            var decodeOffset = _DecodeOffset;
            var position = _SourceStream.Position;
            Header header = _BitStream.ReadFrame();
            if (header == null)
                return false;

            try {
                // Set the channel count and frequency values for the stream.
                if (header.Mode() == Header.SINGLE_CHANNEL)
                    _ChannelCountRep = 1;
                else
                    _ChannelCountRep = 2;
                _FrequencyRep = header.Frequency();
                // Decode the frame.
                ABuffer decoderOutput = _Decoder.DecodeFrame(header, _BitStream);
                if (decoderOutput != _Buffer) {
                    throw new ApplicationException("Output buffers are different.");
                }
            }
            finally {
                // No resource leaks please!
                _BitStream.CloseFrame();
            }
            if (!_FrameOffsets.ContainsKey(decodeOffset))
            {
                _FrameOffsets.Add(decodeOffset, position);
                _SortedKnownFrameOffsets.Add(decodeOffset);
                _SortedKnownFrameOffsets.Sort();
            }
            return true;
        }
    }
}