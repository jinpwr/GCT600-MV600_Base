using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Receives MediaPipe Pose frames from GCT600_Server/server_pose.py (one JSON object per line on TCP 5050)
// for use on the headset. Unlike StreamClient it never blocks the main thread, keeps retrying until the
// PC is reachable, and draws nothing. pose.txt (an IP or host name) in Application.persistentDataPath
// overrides the address, so a new network at the venue does not need a new build.
// The server serves one client at a time.
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public sealed class WebcamPoseReceiver : MonoBehaviour
{
    public const string AddressFileName = "pose.txt";

    [Tooltip("IP of the PC running server_pose.py. pose.txt overrides it.")]
    public string serverAddress = "127.0.0.1";
    public int serverPort = 5050;
    [Tooltip("Frames older than this count as lost.")]
    public float staleAfter = 0.5f;

    [Header("Runtime status")]
    [SerializeField] private string status = "idle";
    [SerializeField] private string addressSource;
    [SerializeField] private int framesReceived;

    /// <summary>Latest frame; null until one arrives. Replaced (never mutated) on each new frame.</summary>
    public PoseData Latest { get; private set; }
    /// <summary>Time.time when Latest arrived.</summary>
    public float LatestTime { get; private set; } = -999f;
    public bool IsFresh => Latest != null && Time.time - LatestTime <= staleAfter;

    private Thread thread;
    private volatile bool running;
    private volatile string pendingLine;
    private volatile string threadStatus = "idle";
    private string address;

    private void Awake()
    {
        address = ReadAddressFile(out addressSource);
    }

    private void OnEnable()
    {
        running = true;
        thread = new Thread(Run) { IsBackground = true, Name = "WebcamPoseReceiver" };
        thread.Start();
    }

    private void OnDisable()
    {
        running = false;
        thread?.Join(200);
        thread = null;
    }

    private void Update()
    {
        status = threadStatus;
        string line = pendingLine;
        if (line == null) return;
        pendingLine = null;
        try
        {
            PoseData pose = JsonUtility.FromJson<PoseData>(line);
            if (pose?.world_landmarks != null && pose.world_landmarks.Count >= 33)
            {
                Latest = pose;
                LatestTime = Time.time;
                framesReceived++;
            }
        }
        catch (Exception) { /* a malformed line; the next one replaces it */ }
    }

    private void Run()
    {
        var buffer = new byte[65536];
        var text = new StringBuilder();
        while (running)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    threadStatus = $"connecting to {address}:{serverPort}";
                    IAsyncResult ar = client.BeginConnect(address, serverPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(2000) || !client.Connected)
                    {
                        threadStatus = $"no server at {address}:{serverPort}, retrying";
                        Sleep(2000);
                        continue;
                    }
                    client.EndConnect(ar);
                    client.ReceiveTimeout = 3000;
                    threadStatus = $"connected to {address}:{serverPort}";
                    NetworkStream stream = client.GetStream();
                    text.Clear();
                    while (running)
                    {
                        int n = stream.Read(buffer, 0, buffer.Length); // throws on timeout
                        if (n <= 0) break;
                        text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                        // Keep only the newest complete line; drop older ones.
                        string s = text.ToString();
                        int last = s.LastIndexOf('\n');
                        if (last < 0) continue;
                        string complete = s.Substring(0, last);
                        string line = complete.Substring(complete.LastIndexOf('\n') + 1).Trim();
                        if (line.Length > 0) pendingLine = line;
                        text.Remove(0, last + 1);
                    }
                }
                threadStatus = "disconnected, retrying";
            }
            catch (Exception e)
            {
                threadStatus = $"error ({e.GetType().Name}), retrying";
            }
            Sleep(1000);
        }
        threadStatus = "stopped";
    }

    private void Sleep(int ms)
    {
        for (int t = 0; t < ms && running; t += 100) Thread.Sleep(100);
    }

    private string ReadAddressFile(out string source)
    {
        string path = Path.Combine(Application.persistentDataPath, AddressFileName);
        try
        {
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path).Trim();
                if (text.Length > 0)
                {
                    source = path;
                    return text;
                }
            }
        }
        catch (Exception) { /* fall through to the Inspector value */ }
        source = "Inspector";
        return serverAddress;
    }
}
