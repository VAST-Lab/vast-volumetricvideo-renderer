// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public class PlySequenceStreamServerWindow : EditorWindow
    {
        string m_SequenceFolder = "";
        string m_FourDGSPath = "";
        int m_Port = 8080;
        float m_BroadcastFPS = 30.0f;

        HttpListener m_HttpListener;
        CancellationTokenSource m_Cts;
        readonly List<WebSocket> m_Clients = new();
        bool m_IsRunning = false;
        string m_Status = "Server Stopped";

        [MenuItem("Tools/Gaussian Splats/4DGS Stream Server")]
        public static void Open()
        {
            var win = GetWindow<PlySequenceStreamServerWindow>("Stream Server");
            win.minSize = new Vector2(400, 320);
        }

        void OnDisable()
        {
            StopServer();
        }

        void OnGUI()
        {
            GUILayout.Label("4DGS WebSocket Stream Server", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            m_Port = EditorGUILayout.IntField("Port", m_Port);
            m_BroadcastFPS = EditorGUILayout.Slider("Broadcast FPS", m_BroadcastFPS, 1.0f, 60.0f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Content Source", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            m_SequenceFolder = EditorGUILayout.TextField("Sequence Folder", m_SequenceFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string sel = EditorUtility.OpenFolderPanel("Select SPZ/PLY Folder", m_SequenceFolder, "");
                if (!string.IsNullOrEmpty(sel)) m_SequenceFolder = sel;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            m_FourDGSPath = EditorGUILayout.TextField("Or .4dgs File", m_FourDGSPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string sel = EditorUtility.OpenFilePanel("Select .4dgs File", m_FourDGSPath, "4dgs");
                if (!string.IsNullOrEmpty(sel)) m_FourDGSPath = sel;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(15);
            if (!m_IsRunning)
            {
                if (GUILayout.Button("Start Server", GUILayout.Height(36)))
                {
                    StartServer();
                }
            }
            else
            {
                if (GUILayout.Button("Stop Server", GUILayout.Height(36)))
                {
                    StopServer();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox($"Status: {m_Status}\nConnected Clients: {m_Clients.Count}\nURL: ws://localhost:{m_Port}", MessageType.Info);
        }

        void StartServer()
        {
            StopServer();
            try
            {
                m_HttpListener = new HttpListener();
                m_HttpListener.Prefixes.Add($"http://*:{m_Port}/");
                m_HttpListener.Start();

                m_Cts = new CancellationTokenSource();
                m_IsRunning = true;
                m_Status = "Running (listening for clients...)";

                Task.Run(() => AcceptLoop(m_Cts.Token));
                Task.Run(() => BroadcastLoop(m_Cts.Token));
            }
            catch (Exception e)
            {
                m_Status = $"Failed to start: {e.Message}";
                Debug.LogError($"[StreamServer] {e}");
            }
        }

        void StopServer()
        {
            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = null;

            lock (m_Clients)
            {
                foreach (var client in m_Clients)
                {
                    try { client.Dispose(); } catch {}
                }
                m_Clients.Clear();
            }

            if (m_HttpListener != null && m_HttpListener.IsListening)
            {
                try { m_HttpListener.Stop(); } catch {}
                m_HttpListener = null;
            }

            m_IsRunning = false;
            m_Status = "Server Stopped";
            Repaint();
        }

        async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && m_HttpListener != null && m_HttpListener.IsListening)
            {
                try
                {
                    var ctx = await m_HttpListener.GetContextAsync();
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        var wsContext = await ctx.AcceptWebSocketAsync(null);
                        lock (m_Clients)
                        {
                            m_Clients.Add(wsContext.WebSocket);
                        }
                    }
                    else
                    {
                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close();
                    }
                }
                catch when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StreamServer] Accept error: {e.Message}");
                }
            }
        }

        async Task BroadcastLoop(CancellationToken ct)
        {
            var frames = new List<byte[]>();

            // Load either .4dgs chunks or SPZ files
            if (!string.IsNullOrWhiteSpace(m_FourDGSPath) && File.Exists(m_FourDGSPath))
            {
                using var fs = new FileStream(m_FourDGSPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = FourDGSFormat.ReadHeader(fs);
                var table = FourDGSFormat.ReadIndexTable(fs, header);
                for (int i = 0; i < table.Length; ++i)
                {
                    fs.Position = (long)table[i].frameOffset;
                    byte[] chunk = new byte[table[i].compressedSize];
                    fs.Read(chunk, 0, chunk.Length);
                    frames.Add(chunk);
                }
            }
            else if (!string.IsNullOrWhiteSpace(m_SequenceFolder) && Directory.Exists(m_SequenceFolder))
            {
                var files = Directory.GetFiles(m_SequenceFolder, "*.spz");
                Array.Sort(files);
                foreach (var file in files)
                    frames.Add(File.ReadAllBytes(file));
            }

            if (frames.Count == 0)
                return;

            uint frameIdx = 0;
            int delayMs = (int)(1000.0f / Mathf.Max(1.0f, m_BroadcastFPS));

            while (!ct.IsCancellationRequested)
            {
                byte[] rawFrame = frames[(int)(frameIdx % frames.Count)];
                byte[] packet = BuildPacket(frameIdx, frameIdx / m_BroadcastFPS, 0, rawFrame);

                lock (m_Clients)
                {
                    for (int i = m_Clients.Count - 1; i >= 0; --i)
                    {
                        var client = m_Clients[i];
                        if (client.State == WebSocketState.Open)
                        {
                            try
                            {
                                client.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None);
                            }
                            catch
                            {
                                m_Clients.RemoveAt(i);
                            }
                        }
                        else
                        {
                            m_Clients.RemoveAt(i);
                        }
                    }
                }

                frameIdx++;
                await Task.Delay(delayMs, ct);
            }
        }

        static byte[] BuildPacket(uint frameIdx, float timestamp, uint payloadType, byte[] payload)
        {
            byte[] packet = new byte[16 + payload.Length];
            BitConverter.GetBytes(GaussianSplatStreamReceiver.kStreamMagic).CopyTo(packet, 0);
            BitConverter.GetBytes(frameIdx).CopyTo(packet, 4);
            BitConverter.GetBytes(timestamp).CopyTo(packet, 8);
            BitConverter.GetBytes(payloadType).CopyTo(packet, 12);
            Buffer.BlockCopy(payload, 0, packet, 16, payload.Length);
            return packet;
        }
    }
}
