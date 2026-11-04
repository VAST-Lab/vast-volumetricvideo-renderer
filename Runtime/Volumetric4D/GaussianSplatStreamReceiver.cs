// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.WebSockets;
using System.Threading.Tasks;
#endif

namespace GaussianSplatting.Runtime
{
    [ExecuteInEditMode]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianSplatStreamReceiver : MonoBehaviour
    {
        public const uint kStreamMagic = 0x54534753; // "SGST" (Splat Gaussian Stream) in little-endian

        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Error
        }

        public struct Packet
        {
            public uint frameIndex;
            public float timestamp;
            public uint payloadType;
            public byte[] payload;
        }

        [Header("Connection")]
        [Tooltip("WebSocket streaming server URL (e.g. ws://localhost:8080 or wss://...)")]
        public string m_ServerUrl = "ws://localhost:8080";
        public bool m_ConnectOnStart = true;
        [Range(1, 60)]
        [Tooltip("Target playback frame rate")]
        public int m_TargetFPS = 30;

        [Header("Jitter Buffer")]
        [Range(0, 10)]
        [Tooltip("Number of frames to buffer before starting playback")]
        public int m_JitterBufferFrames = 2;

        public ConnectionState state => m_State;
        public int bufferedFrames => m_PacketQueue.Count;
        public uint lastReceivedFrame => m_LastReceivedFrameIndex;

        GaussianSplatRenderer m_Renderer;
        ConnectionState m_State = ConnectionState.Disconnected;
        readonly ConcurrentQueue<Packet> m_PacketQueue = new();
        uint m_LastReceivedFrameIndex = 0;
        bool m_HasStartedPlayback = false;
        float m_FrameInterval;
        float m_TimeSinceLastFrame = 0.0f;

#if !UNITY_WEBGL || UNITY_EDITOR
        ClientWebSocket m_WebSocket;
        CancellationTokenSource m_Cts;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        delegate void OnFrameDelegate(IntPtr dataPtr, int length);
        delegate void OnStateDelegate(int state);

        [DllImport("__Internal")]
        static extern void GS_WebSocketConnect(string url, OnFrameDelegate onFrame, OnStateDelegate onState);
        [DllImport("__Internal")]
        static extern void GS_WebSocketClose();
        [DllImport("__Internal")]
        static extern int GS_WebSocketGetState();

        static GaussianSplatStreamReceiver s_ActiveInstance;
#endif

        void Awake()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
        }

        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
            m_FrameInterval = 1.0f / Mathf.Max(1, m_TargetFPS);
            if (Application.isPlaying && m_ConnectOnStart)
                Connect();
        }

        void OnDisable()
        {
            Disconnect();
        }

        public void Connect()
        {
            if (m_State == ConnectionState.Connected || m_State == ConnectionState.Connecting)
                return;

            m_Renderer.m_SourceType = GaussianSplatSourceType.ExternalStreamOr4D;
            m_State = ConnectionState.Connecting;
            m_HasStartedPlayback = false;

#if UNITY_WEBGL && !UNITY_EDITOR
            s_ActiveInstance = this;
            GS_WebSocketConnect(m_ServerUrl, OnWebGlFrameReceived, OnWebGlStateChanged);
#else
            m_Cts = new CancellationTokenSource();
            Task.Run(() => ConnectDesktopAsync(m_ServerUrl, m_Cts.Token));
#endif
        }

        public void Disconnect()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (s_ActiveInstance == this)
            {
                GS_WebSocketClose();
                s_ActiveInstance = null;
            }
#else
            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = null;

            if (m_WebSocket != null)
            {
                try { m_WebSocket.Dispose(); } catch {}
                m_WebSocket = null;
            }
#endif
            m_State = ConnectionState.Disconnected;
            m_HasStartedPlayback = false;
            while (m_PacketQueue.TryDequeue(out _)) {}
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        async Task ConnectDesktopAsync(string url, CancellationToken ct)
        {
            try
            {
                m_WebSocket = new ClientWebSocket();
                await m_WebSocket.ConnectAsync(new Uri(url), ct);
                m_State = ConnectionState.Connected;

                byte[] buffer = new byte[256 * 1024];
                using var ms = new MemoryStream();

                while (!ct.IsCancellationRequested && m_WebSocket.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await m_WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await m_WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                            m_State = ConnectionState.Disconnected;
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        ProcessRawPacketBytes(ms.ToArray());
                    }
                }
            }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested)
                {
                    Debug.LogWarning($"[GaussianSplatStreamReceiver] Connection error: {e.Message}");
                    m_State = ConnectionState.Error;
                }
            }
        }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        [AOT.MonoPInvokeCallback(typeof(OnFrameDelegate))]
        static void OnWebGlFrameReceived(IntPtr dataPtr, int length)
        {
            if (s_ActiveInstance == null || dataPtr == IntPtr.Zero || length < 16) return;
            byte[] bytes = new byte[length];
            Marshal.Copy(dataPtr, bytes, 0, length);
            s_ActiveInstance.ProcessRawPacketBytes(bytes);
        }

        [AOT.MonoPInvokeCallback(typeof(OnStateDelegate))]
        static void OnWebGlStateChanged(int stateCode)
        {
            if (s_ActiveInstance == null) return;
            s_ActiveInstance.m_State = stateCode switch
            {
                1 => ConnectionState.Connected,
                2 => ConnectionState.Disconnected,
                _ => ConnectionState.Error
            };
        }
#endif

        void ProcessRawPacketBytes(byte[] raw)
        {
            if (raw == null || raw.Length < 16) return;

            uint magic = BitConverter.ToUInt32(raw, 0);
            if (magic != kStreamMagic)
                return;

            uint frameIdx = BitConverter.ToUInt32(raw, 4);
            float timestamp = BitConverter.ToSingle(raw, 8);
            uint payloadType = BitConverter.ToUInt32(raw, 12);

            int payloadLen = raw.Length - 16;
            byte[] payload = new byte[payloadLen];
            Buffer.BlockCopy(raw, 16, payload, 0, payloadLen);

            m_PacketQueue.Enqueue(new Packet
            {
                frameIndex = frameIdx,
                timestamp = timestamp,
                payloadType = payloadType,
                payload = payload
            });
            m_LastReceivedFrameIndex = frameIdx;
        }

        void Update()
        {
            if (m_PacketQueue.IsEmpty) return;

            if (!m_HasStartedPlayback)
            {
                if (m_PacketQueue.Count >= m_JitterBufferFrames)
                    m_HasStartedPlayback = true;
                else
                    return;
            }

            m_TimeSinceLastFrame += Time.deltaTime;
            if (m_TimeSinceLastFrame >= m_FrameInterval)
            {
                m_TimeSinceLastFrame = 0.0f;
                if (m_PacketQueue.TryDequeue(out var packet))
                {
                    ApplyFramePacket(packet);
                }
            }
        }

        void ApplyFramePacket(Packet packet)
        {
            if (packet.payload == null || packet.payload.Length == 0) return;

            try
            {
                // Keyframe SPZ packet
                using var op = new GaussianSplatLoadOperation(packet.payload);
                while (!op.isDone) op.Update();
                using var data = op.TakeData();
                if (data != null)
                {
                    if (m_Renderer.gpuPosData == null || m_Renderer.splatCount != data.count)
                    {
                        m_Renderer.AllocateDynamicBuffers(data.count, data.shDegree);
                    }

                    m_Renderer.UpdateDynamicFrame(
                        data.count,
                        data.bounds,
                        data.positions,
                        data.geometry,
                        data.colors,
                        data.sh,
                        updateSH: true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GaussianSplatStreamReceiver] Failed to decode packet {packet.frameIndex}: {e.Message}", this);
            }
        }
    }
}
