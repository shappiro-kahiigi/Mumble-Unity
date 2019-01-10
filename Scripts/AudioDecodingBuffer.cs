﻿/*
 * AudioDecodingBuffer
 * Buffers up encoded audio packets and provides a constant stream of sound (silence if there is no more audio to decode)
 * This works by having a buffer with N sub-buffers, each of the size of a PCM frame. When Read, this copys the buffer data into the passed array
 * and, if there are no more decoded data, calls Opus to decode the sample
 * 
 * TODO This is decoding audio data on the main thread. We should make decoding happen in a separate thread
 * TODO Use the sequence number in error correcting
 */
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Mumble {
    public class AudioDecodingBuffer : IDisposable
    {
        public long NumPacketsLost { get; private set; }
        public bool HasFilledInitialBuffer { get; private set; }
        /// <summary>
        /// How many samples are currently decoded
        /// </summary>
        private int _decodedCount;
        /// <summary>
        /// Which decode buffer we're currently reading from
        /// </summary>
        private int _bufferToReadFrom;
        /// <summary>
        /// The index of the next sub-buffer to decode into
        /// </summary>
        private int _nextBufferToDecodeInto;
        /// <summary>
        /// The sequence that we expect for the next packet
        /// </summary>
        private long _nextSequenceToDecode;
        /// <summary>
        /// The sequence that we last decoded
        /// </summary>
        private long _lastDecodedSequence;
        /// <summary>
        /// The last sequence we received
        /// </summary>
        private long _lastSequenceReceived;

        private uint _numSamplesDroppedOverflow;
        private uint _numSamplesDroppedConnection;
        private uint _numSamplesRecv;


        private OpusDecoder _decoder;

        /// <summary>
        /// Name of the speaker being decoded
        /// Only used for debugging
        /// </summary>
        private string _name;
        private uint _session;

        /// <summary>
        /// Has this buffer had to drop samples
        /// Because there were too many unprocessed?
        /// Used only for debugging, currently
        /// </summary>
        private bool _hasOverflowed;

        private readonly int _outputSampleRate;
        private readonly int _outputChannelCount;
        private readonly float[][] _decodedBuffer = new float[NumDecodedSubBuffers][];
        private readonly int[] _numSamplesInBuffer = new int[NumDecodedSubBuffers];
        private readonly int[] _readOffsetInBuffer = new int[NumDecodedSubBuffers];
        private readonly Queue<BufferPacket> _encodedBuffer = new Queue<BufferPacket>();
        private readonly object _bufferLock = new object();
        const int NumDecodedSubBuffers = (int)(MumbleConstants.MAX_LATENCY_SECONDS * (MumbleConstants.MAX_SAMPLE_RATE / MumbleConstants.OUTPUT_FRAME_SIZE));
        const int SubBufferSize = MumbleConstants.OUTPUT_FRAME_SIZE * MumbleConstants.MAX_FRAMES_PER_PACKET * MumbleConstants.MAX_CHANNELS;
        /// <summary>
        /// How many packets go missing before we figure they were lost
        /// Due to murmur
        /// </summary>
        const long MaxMissingPackets = 25;

        /// <summary>
        /// How many incoming packets to buffer before audio begins to be played
        /// Higher values increase stability and latency
        /// </summary>
        const int InitialSampleBuffer = 3;

        public AudioDecodingBuffer(int audioRate, int channelCount)
        {
            _outputSampleRate = audioRate;
            _outputChannelCount = channelCount;
        }
        public void Init(string name, uint session)
        {
            Debug.Log("Init decoding buffer for: " + name + " Session #" + session);
            _name = name;
            _session = session;
        }
        public int Read(float[] buffer, int offset, int count)
        {
            // Don't send audio until we've filled our initial buffer of packets
            if (!HasFilledInitialBuffer)
            {
                Array.Clear(buffer, offset, count);
                if(_hasOverflowed)
                    Debug.Log(_name + " waiting on initial audio buffer");
                return 0;
            }

            /*
            lock (_bufferLock)
            {
                Debug.Log("We now have " + _encodedBuffer.Count + " encoded packets");
            }
            */
            //Debug.LogWarning("Will read");

            int readCount = 0;
            while (readCount < count)
            {
                if(_decodedCount > 0)
                    readCount += ReadFromBuffer(buffer, offset + readCount, count - readCount);
                else if (!FillBuffer())
                    break;
            }

            //Return silence if there was no data available
            if (readCount == 0)
            {
                //Debug.LogWarning("Returning silence");
                Array.Clear(buffer, offset, count);
            } else if (readCount < count)
            {
                //Debug.LogWarning("Buffer underrun: " + (count - readCount) + " samples. Asked: " + count + " provided: " + readCount + " numDec: " + _decodedCount);
                Array.Clear(buffer, offset + readCount, count - readCount);
            }
            else
            {
                //Debug.Log(".");
            }

            //if (_hasOverflowed)
                //Debug.Log(_name + " decoding buffer read: " + readCount);
            
            return readCount;
        }

        private BufferPacket GetNextEncodedData()
        {
            BufferPacket packet = null;
            lock (_bufferLock)
            {
                if (_encodedBuffer.Count > 0)
                    packet = _encodedBuffer.Dequeue();
            }
            return packet;
        }

        /// <summary>
        /// Read data that has already been decoded
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="dstOffset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        private int ReadFromBuffer(float[] dst, int dstOffset, int count)
        {
            //Copy as much data as we can from the buffer up to the limit
            int readCount = Math.Min(count, _numSamplesInBuffer[_bufferToReadFrom]);
            /*
            Debug.Log("Reading " + readCount
                + "| starting at " + _readOffsetInBuffer[_bufferToReadFrom]
                + "| current buff is " + _bufferToReadFrom
                + "| into the location " + dstOffset
                + "| with in curr buff " + _numSamplesInBuffer[_bufferToReadFrom]
                + "| out of " + _decodedBuffer[_bufferToReadFrom].Length
                + "| with " + _decodedCount);
            */

            Array.Copy(_decodedBuffer[_bufferToReadFrom], _readOffsetInBuffer[_bufferToReadFrom], dst, dstOffset, readCount);
            _decodedCount -= readCount;
            _readOffsetInBuffer[_bufferToReadFrom] += readCount;
            _numSamplesInBuffer[_bufferToReadFrom] -= readCount;

            // If we hit the end of the buffer, move over
            // But only if the next buffer contains data
            // Otherwise, we might miss some data
            int nextBuffer = (_bufferToReadFrom + 1) % NumDecodedSubBuffers;
            if (_numSamplesInBuffer[_bufferToReadFrom] == 0
                && _numSamplesInBuffer[nextBuffer] > 0)
            {
                _bufferToReadFrom = nextBuffer;
            }
            return readCount;
        }

        /// <summary>
        /// Decoded data into the buffer
        /// </summary>
        /// <returns></returns>
        private bool FillBuffer()
        {
            var packet = GetNextEncodedData();
            if (packet == null)
            {
                //Debug.Log("empty");
                return false;
            }
            // Don't make the decoder unless we know that we'll have to
            if(_decoder == null)
                _decoder = new OpusDecoder(_outputSampleRate, _outputChannelCount);

            if (_decodedBuffer[_nextBufferToDecodeInto] == null)
                _decodedBuffer[_nextBufferToDecodeInto] = new float[SubBufferSize];

            if (_numSamplesInBuffer[_nextBufferToDecodeInto] != 0)
                Debug.LogWarning("Overwriting existing samples!");

            //Debug.Log("decoding " + packet.Value.Sequence + "  expected=" + _nextSequenceToDecode + " last=" + _lastReceivedSequence + " len=" + packet.Value.Data.Length);
            if (_nextSequenceToDecode != 0)
            {
                long seqDiff = packet.Sequence - _nextSequenceToDecode;

                // If new packet is VERY late, then the sequence number has probably reset
                if(seqDiff < -MaxMissingPackets)
                {
                    Debug.Log("Sequence has possibly reset diff = " + seqDiff);
                    _decoder.ResetState();
                }
                // If the packet came before we were expecting it to, but after the last packet, the sampling has probably changed
                // unless the packet is a last packet (in which case the sequence may have only increased by 1)
                else if (packet.Sequence > _lastDecodedSequence && seqDiff < 0 && !packet.IsLast)
                {
                    Debug.Log("Mumble sample rate may have changed");
                }
                // If the sequence number changes abruptly (which happens with push to talk)
                else if (seqDiff > MaxMissingPackets)
                {
                    Debug.Log("Mumble packet sequence changed abruptly pkt: " + packet.Sequence + " last: " + _lastDecodedSequence);
                }
                // If the packet is a bit late, drop it
                else if (seqDiff < 0 && !packet.IsLast)
                {
                    Debug.LogWarning("Received old packet " + packet.Sequence + " expecting " + _nextSequenceToDecode);
                    return false;
                }
                // If we missed a packet, add a null packet to tell the decoder what happened
                else if (seqDiff > 0)
                {
                    NumPacketsLost += packet.Sequence - _nextSequenceToDecode;
                    int emptySampleNumRead =_decoder.Decode(null, _decodedBuffer[_nextBufferToDecodeInto]);

                    // This is expected for two cases:
                    // 1) Packets were lost from the network being UDP
                    // 2) Ping momentarily spiked, so we can a period w/o any packets, then a period 
                    //  with too many packets
                    Debug.LogWarning("Session #" + _session
                        + " dropped packet, recv: " + packet.Sequence + ", expected " + _nextSequenceToDecode
                        + " total dropped from connection: " + _numSamplesDroppedConnection
                        + " total dropped from overflow: " + _numSamplesDroppedOverflow
                        + " total recv: " + _numSamplesRecv
                        + " empty decode: " + emptySampleNumRead
                        + " pkt lost before this one: " + packet.PrecededLostPkt);

                    // If a packet was lost due to an overflow, then we shouldn't use the extra
                    // decoded data. Otherwise, we'll keep having extra decoded audio and will
                    // keep dropping data
                    // So we only decode a null for network-based lost packets
                    if (packet.PrecededLostPkt)
                    {
                        //Debug.Log("Added empty read to decoded buffer");
                        _decodedCount += emptySampleNumRead;
                        _numSamplesInBuffer[_nextBufferToDecodeInto] = emptySampleNumRead;
                        _readOffsetInBuffer[_nextBufferToDecodeInto] = 0;
                        _nextSequenceToDecode = packet.Sequence + emptySampleNumRead / ((_outputSampleRate / 100) * _outputChannelCount);
                        // Switch to the next decode buffer
                        if (emptySampleNumRead > 0)
                            _nextBufferToDecodeInto = (_nextBufferToDecodeInto + 1) % NumDecodedSubBuffers;
                        else
                            Debug.LogWarning("Failed reading null sample");
                        if (_decodedBuffer[_nextBufferToDecodeInto] == null)
                            _decodedBuffer[_nextBufferToDecodeInto] = new float[SubBufferSize];
                    }
                    //Debug.Log("Null read returned: " + emptySampleNumRead + " samples");
                }
            }

            int numRead = 0;
            if (packet.Data.Length != 0)
                numRead = _decoder.Decode(packet.Data, _decodedBuffer[_nextBufferToDecodeInto]);
            else
            {
                //Debug.Log("empty packet data?");
                // This is expected when people enter/leave mute
                // we return true because, even though nothing was read
                // there may still be packets remaining
                return true;
            }

            if (numRead <= 0)
            {
                Debug.LogError("num read is: " + numRead);
                return false;
            }

            _decodedCount += numRead;
            _numSamplesInBuffer[_nextBufferToDecodeInto] = numRead;
            _readOffsetInBuffer[_nextBufferToDecodeInto] = 0;
            //Debug.Log("numRead = " + numRead);
            _lastDecodedSequence = packet.Sequence;
            if (!packet.IsLast)
                _nextSequenceToDecode = packet.Sequence + numRead / ((_outputSampleRate / 100) * _outputChannelCount);
            else
            {
                Debug.Log("Resetting " + _name + "'s decoder");
                _nextSequenceToDecode = 0;
                // Re-evaluate whether we need to fill up a buffer of audio before playing
                lock (_bufferLock)
                {
                    HasFilledInitialBuffer = (_encodedBuffer.Count + 1 >= InitialSampleBuffer);
                }
                _decoder.ResetState();
            }
            // Switch to the next decode buffer
            if (numRead > 0)
                _nextBufferToDecodeInto = (_nextBufferToDecodeInto + 1) % NumDecodedSubBuffers;

            return true;
        }
        /// <summary>
        /// Add a new packet of encoded data
        /// </summary>
        /// <param name="sequence">Sequence number of this packet</param>
        /// <param name="data">The encoded audio packet</param>
        /// <param name="codec">The codec to use to decode this packet</param>
        public void AddEncodedPacket(long sequence, byte[] data, bool isLast)
        {
            /* TODO this messes up when we hit configure in the desktop mumble app. The sequence number drops to 0
            //If the next seq we expect to decode comes after this packet we've already missed our opportunity!
            if (_nextSequenceToDecode > sequence)
            {
                Debug.LogWarning("Dropping packet number: " + sequence + " we're decoding number " + _nextSequenceToDecode);
                return;
            }
            */
            _numSamplesRecv++;
            bool precededLostPkt = false;
            if(_lastSequenceReceived >= sequence)
            {
                Debug.LogWarning("Non-increasing sequence, " + _lastSequenceReceived + "->" + sequence);
            }
            else if(sequence - _lastSequenceReceived != 2
                && _lastSequenceReceived != 0)
            {
                Debug.LogWarning("Jump sequence, " + _lastSequenceReceived + "->" + sequence);
                _numSamplesDroppedConnection++;
                precededLostPkt = true;
            }
            _lastSequenceReceived = sequence;

            BufferPacket packet = new BufferPacket
            {
                Data = data,
                Sequence = sequence,
                IsLast = isLast,
                PrecededLostPkt = precededLostPkt
            };

            //Debug.Log("Adding #" + sequence);
            lock (_bufferLock)
            {
                int count = _encodedBuffer.Count;
                if (count > MumbleConstants.RECEIVED_PACKET_BUFFER_SIZE)
                {
                    //Debug.LogWarning("Max recv buffer size reached, dropping for user " + _name + " seq: " + sequence + " session#" + _session + " has filled init buffer: " + HasFilledInitialBuffer);

                    _hasOverflowed = true;
                    _numSamplesDroppedOverflow++;
                    // Dequeue from the front so that we play the most
                    // up to date audio
                    _encodedBuffer.Dequeue();
                }

                _encodedBuffer.Enqueue(packet);
                if (!HasFilledInitialBuffer && (count + 1 >= InitialSampleBuffer))
                    HasFilledInitialBuffer = true;
                //Debug.Log("Count is now: " + _encodedBuffer.Count);
            }
        }

        public void Reset()
        {
            lock (_bufferLock)
            {
                Debug.Log("Resetting decoding buffer for: " + _name);
                NumPacketsLost = 0;
                HasFilledInitialBuffer = false;
                _decodedCount = 0;
                _bufferToReadFrom = 0;
                _nextBufferToDecodeInto = 0;
                _nextSequenceToDecode = 0;
                _lastDecodedSequence = 0;
                _lastSequenceReceived = 0;
                if (_decoder != null)
                    _decoder.ResetState();
                _name = null;
                _hasOverflowed = false;
                Array.Clear(_numSamplesInBuffer, 0, _numSamplesInBuffer.Length);
                Array.Clear(_readOffsetInBuffer, 0, _readOffsetInBuffer.Length);
                _encodedBuffer.Clear();
                // _decodedBuffer is allowed to be dirty, so no need to clear it
            }
        }
        public void Dispose()
        {
            if(_decoder != null)
            {
                _decoder.Dispose();
                _decoder = null;
            }
        }

        private class BufferPacket
        {
            public byte[] Data;
            public long Sequence;
            public bool IsLast;
            /// <summary>
            /// Whether or not we lost a packet(s) before
            /// receiving this one.
            /// We track this so that we know whether or
            /// not to fill in lost data.
            /// If data was lost from network, we should
            /// replace it, but if it was lost b/c of
            /// an overflow, then we should not decode
            /// extra data
            /// </summary>
            public bool PrecededLostPkt;
        }
    }
}