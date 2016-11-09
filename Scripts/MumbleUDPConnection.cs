﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Timers;
using UnityEngine;
using System.IO;

namespace Mumble
{
    public class MumbleUdpConnection
    {
        private readonly IPEndPoint _host;
        private readonly UdpClient _udpClient;
        private readonly MumbleClient _mumbleClient;
        private CryptState _cryptState;
        private Timer _udpTimer;
        private bool _isConnected = false;
        internal volatile bool _isSending = false;
        internal volatile int NumPacketsSent = 0;
        internal volatile int NumPacketsRecv = 0;

        internal MumbleUdpConnection(IPEndPoint host, MumbleClient mc)
        {
            _host = host;
            _udpClient = new UdpClient();
            _mumbleClient = mc;
        }

        internal void UpdateOcbServerNonce(byte[] serverNonce)
        {
            if(serverNonce != null)
                _cryptState.CryptSetup.server_nonce = serverNonce;
        }

        internal void Connect()
        {
            Debug.Log("Establishing UDP connection");
            _cryptState = new CryptState();
            _cryptState.CryptSetup = _mumbleClient.CryptSetup;
            _udpClient.Connect(_host);
            _isConnected = true;

            _udpTimer = new Timer(MumbleConstants.PING_INTERVAL);
            _udpTimer.Elapsed += RunPing;
            _udpTimer.Enabled = true;

            SendPing();
            _udpClient.BeginReceive(ReceiveUdpMessage, null);
        }

        private void RunPing(object sender, ElapsedEventArgs elapsedEventArgs)
        {
             SendPing();
        }
        private void ReceiveUdpMessage(byte[] encrypted)
        {
            NumPacketsRecv++;
            ProcessUdpMessage(encrypted);
            _udpClient.BeginReceive(ReceiveUdpMessage, null);
        }
        private void ReceiveUdpMessage(IAsyncResult res)
        {
            //Debug.Log("Received message");
            IPEndPoint remoteIpEndPoint = _host;
            byte[] encrypted = _udpClient.EndReceive(res, ref remoteIpEndPoint);
            ReceiveUdpMessage(encrypted);
        }
        internal void ProcessUdpMessage(byte[] encrypted)
        {
            byte[] message = _cryptState.Decrypt(encrypted, encrypted.Length);

            // figure out type of message
            int type = message[0] >> 5 & 0x7;
            //Debug.Log("UDP response received: " + Convert.ToString(message[0], 2).PadLeft(8, '0'));
            //Debug.Log("UDP response type: " + (UDPType)type);
            //Debug.Log("UDP length: " + message.Length);

            //If we get an OPUS audio packet, de-encode it
            switch ((UDPType)type)
            {
                case UDPType.Opus:
                    UnpackOpusVoicePacket(message);
                    break;
                case UDPType.Ping:
                    OnPing(message);
                    break;
                default:
                    Debug.LogError("Not implemented: " + ((UDPType)type));
                    break;
            }
        }
        internal void OnPing(byte[] message)
        {
            //Debug.Log("Would process ping");
        }
        internal void UnpackOpusVoicePacket(byte[] plainTextMessage)
        {
            byte typeByte = plainTextMessage[0];
            int target = typeByte & 31;

            using (var reader = new UdpPacketReader(new MemoryStream(plainTextMessage, 1, plainTextMessage.Length - 1)))
            {
                UInt32 session;
                if(!_mumbleClient.UseLocalLoopBack)
                    session = (uint)reader.ReadVarInt64();
                Int64 sequence = reader.ReadVarInt64();

                //We assume we mean OPUS
                int size = (int)reader.ReadVarInt64();
                //Debug.Log("Size " + size);
                bool isLast = (size & 8192) == 8192;
                if (isLast)
                    Debug.Log("Found last byte in seq");

                //Apply a bitmask to remove the bit that marks if this is the last packet
                size &= 0x1fff;

                //Debug.Log("Received sess: " + session);
                Debug.Log(" seq: " + sequence + " size = " + size + " packetLen: " + plainTextMessage.Length);

                if (size == 0)
                    return;

                byte[] data = reader.ReadBytes(size);

                if (data == null || data.Length != size)
                {
                    Debug.LogError("empty or wrong sized packet");
                    return;
                }

                _mumbleClient.ReceiveEncodedVoice(data, sequence);
            }
        }
        internal void SendPing()
        {
            ulong unixTimeStamp = (ulong) (DateTime.UtcNow.Ticks - DateTime.Parse("01/01/1970 00:00:00").Ticks);
            byte[] timeBytes = BitConverter.GetBytes(unixTimeStamp);
            var dgram = new byte[9];
            timeBytes.CopyTo(dgram, 1);
            dgram[0] = (1 << 5);
            var encryptedData = _cryptState.Encrypt(dgram, timeBytes.Length + 1);

            if (!_isConnected)
            {
                Debug.LogError("Not yet connected");
                return;
            }

            while (_isSending)
                System.Threading.Thread.Sleep(1);
            _isSending = true;
            _udpClient.BeginSend(encryptedData, encryptedData.Length, new AsyncCallback(OnSent), null);
        }

        internal void Close()
        {
            _udpClient.Close();
            if(_udpTimer != null)
                _udpTimer.Close();
        }
        internal void SendVoicePacket(byte[] voicePacket)
        {
            if (!_isConnected)
            {
                Debug.LogError("Not yet connected");
                return;
            }
            try
            {
                if (_mumbleClient.UseLocalLoopBack)
                    UnpackOpusVoicePacket(voicePacket);
                //Debug.Log("Sending UDP packet! Length = " + voicePacket.Length);
                byte[] encrypted = _cryptState.Encrypt(voicePacket, voicePacket.Length);

                lock (_udpClient)
                {
                    _isSending = true;
                    _udpClient.BeginSend(encrypted, encrypted.Length, new AsyncCallback(OnSent), null);
                }
                NumPacketsSent++;
            }catch(Exception e)
            {
                Debug.LogError("Error sending packet: " + e);
            }
        }
        void OnSent(IAsyncResult result)
        {
            _isSending = false;
        }
        internal byte[] GetLatestClientNonce()
        {
            return _cryptState.CryptSetup.client_nonce;
        }
    }
}
