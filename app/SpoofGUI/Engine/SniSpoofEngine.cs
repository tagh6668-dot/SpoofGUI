using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SpoofGUI.Core;
using SpoofGUI.Models;

namespace SpoofGUI.Engine;

internal sealed class SniSpoofEngine : IDisposable
{
    private readonly record struct ConnKey(uint SrcIp, ushort SrcPort, uint DstIp, ushort DstPort);

    private sealed class TrackedConnection
    {
        public ConnKey Key;
        public volatile bool Monitor = true;
        public long SynSeq = -1;
        public long SynAckSeq = -1;
        public bool SchFakeSent;
        public bool FakeSent;
        public byte[] FakeData = [];
        public Socket Outgoing = null!;
        public Socket Incoming = null!;
        public readonly object Lock = new();
        public readonly TaskCompletionSource<string> Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<ConnKey, TrackedConnection> _connections = new();
    private IntPtr _divert;
    private Socket? _listener;
    private Thread? _divertThread;
    private CancellationTokenSource? _cts;
    private uint _interfaceIp;
    private uint _connectIpValue;
    private string _interfaceIpText = "";
    private string _connectIp = "";
    private int _connectPort;
    private bool _fastMode;
    private byte[] _fakeSniBytes = [];

    public bool IsRunning { get; private set; }

    public void Start(SpoofProfile profile, bool fastMode)
    {
        if (IsRunning) return;

        WinDivert.EnsureLoadable();
        AppLog.Info("engine step: windivert dll present");

        _connectIp = profile.ConnectIp;
        _connectPort = profile.ConnectPort;
        _fastMode = fastMode;
        _fakeSniBytes = Encoding.ASCII.GetBytes(profile.FakeSni);

        _interfaceIpText = GetDefaultInterfaceIPv4(profile.ConnectIp, profile.ConnectPort);
        if (string.IsNullOrEmpty(_interfaceIpText))
            throw new InvalidOperationException("no route to target; default interface IPv4 not found");
        _interfaceIp = IpToUint(IPAddress.Parse(_interfaceIpText));
        _connectIpValue = IpToUint(IPAddress.Parse(_connectIp));
        AppLog.Info($"engine step: interface {_interfaceIpText} -> target {_connectIp}:{_connectPort}");

        var filter =
            $"tcp and ((ip.SrcAddr == {_interfaceIpText} and ip.DstAddr == {_connectIp}) or " +
            $"(ip.SrcAddr == {_connectIp} and ip.DstAddr == {_interfaceIpText}))";
        AppLog.Info($"engine step: opening windivert; filter={filter}");
        _divert = WinDivert.Open(filter);
        AppLog.Info("engine step: windivert opened");

        _cts = new CancellationTokenSource();
        _divertThread = new Thread(DivertLoop) { IsBackground = true, Name = "sni-windivert" };
        _divertThread.Start();
        AppLog.Info("engine step: capture thread started");

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var bindIp = (profile.ListenHost == "127.0.0.1" || profile.ListenHost == "0.0.0.0")
            ? IPAddress.Any
            : IPAddress.Parse(profile.ListenHost);
        _listener.Bind(new IPEndPoint(bindIp, profile.ListenPort));
        _listener.Listen(128);
        _ = AcceptLoopAsync(_cts.Token);

        IsRunning = true;
        AppLog.Info($"SNI engine started: {_interfaceIpText} -> {_connectIp}:{_connectPort}; fake_sni {profile.FakeSni}; fast_mode {fastMode}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _cts?.Cancel();
        try { _listener?.Close(); } catch { }
        _listener = null;

        WinDivert.Close(_divert);
        _divert = IntPtr.Zero;

        if (_divertThread is { IsAlive: true } thread) thread.Join(1000);
        _divertThread = null;

        foreach (var c in _connections.Values)
        {
            c.Monitor = false;
            Close(c.Outgoing);
            Close(c.Incoming);
        }
        _connections.Clear();

        _cts?.Dispose();
        _cts = null;
        AppLog.Info("SNI engine stopped");
    }

    private void DivertLoop()
    {
        var buffer = new byte[65535];
        while (true)
        {
            var addr = new WinDivertAddress();
            if (!WinDivert.Recv(_divert, buffer, out var received, ref addr)) break;
            try { Inject(buffer, received, ref addr); }
            catch (Exception e) { AppLog.Warn($"engine inject: {e.Message}"); }
        }
    }

    private void Inject(byte[] buf, uint received, ref WinDivertAddress addr)
    {
        if (!IpTcp.IsTcp(buf, received))
        {
            WinDivert.Send(_divert, buf, received, ref addr);
            return;
        }

        var ipHl = IpTcp.IpHeaderLen(buf);
        var srcIp = IpTcp.SrcIp(buf);
        var dstIp = IpTcp.DstIp(buf);
        var srcPort = IpTcp.SrcPort(buf, ipHl);
        var dstPort = IpTcp.DstPort(buf, ipHl);

        if (addr.Outbound)
        {
            var key = new ConnKey(srcIp, srcPort, dstIp, dstPort);
            if (!_connections.TryGetValue(key, out var conn)) { WinDivert.Send(_divert, buf, received, ref addr); return; }
            lock (conn.Lock)
            {
                if (!conn.Monitor) { WinDivert.Send(_divert, buf, received, ref addr); return; }
                OnOutbound(buf, received, ipHl, conn, ref addr);
            }
        }
        else
        {
            var key = new ConnKey(dstIp, dstPort, srcIp, srcPort);
            if (!_connections.TryGetValue(key, out var conn)) { WinDivert.Send(_divert, buf, received, ref addr); return; }
            lock (conn.Lock)
            {
                if (!conn.Monitor) { WinDivert.Send(_divert, buf, received, ref addr); return; }
                OnInbound(buf, received, ipHl, conn, ref addr);
            }
        }
    }

    private void OnOutbound(byte[] buf, uint received, int ipHl, TrackedConnection conn, ref WinDivertAddress addr)
    {
        if (conn.SchFakeSent) { Unexpected(buf, received, conn, ref addr); return; }

        var flags = IpTcp.Flags(buf, ipHl);
        var payload = IpTcp.PayloadLen(buf);
        var seq = IpTcp.SeqNum(buf, ipHl);
        var ack = IpTcp.AckNum(buf, ipHl);

        var synOnly = IpTcp.HasFlag(flags, IpTcp.FlagSyn) && !IpTcp.HasFlag(flags, IpTcp.FlagAck)
            && !IpTcp.HasFlag(flags, IpTcp.FlagRst) && !IpTcp.HasFlag(flags, IpTcp.FlagFin) && payload == 0;
        var ackOnly = IpTcp.HasFlag(flags, IpTcp.FlagAck) && !IpTcp.HasFlag(flags, IpTcp.FlagSyn)
            && !IpTcp.HasFlag(flags, IpTcp.FlagRst) && !IpTcp.HasFlag(flags, IpTcp.FlagFin) && payload == 0;

        if (synOnly)
        {
            if (ack != 0) { Unexpected(buf, received, conn, ref addr); return; }
            if (conn.SynSeq != -1 && conn.SynSeq != seq) { Unexpected(buf, received, conn, ref addr); return; }
            conn.SynSeq = seq;
            WinDivert.Send(_divert, buf, received, ref addr);
            return;
        }

        if (ackOnly)
        {
            if (conn.SynSeq == -1 || ((conn.SynSeq + 1) & 0xFFFFFFFF) != seq) { Unexpected(buf, received, conn, ref addr); return; }
            if (conn.SynAckSeq == -1 || ack != ((conn.SynAckSeq + 1) & 0xFFFFFFFF)) { Unexpected(buf, received, conn, ref addr); return; }

            WinDivert.Send(_divert, buf, received, ref addr);
            conn.SchFakeSent = true;
            var snapshot = new byte[received];
            Array.Copy(buf, 0, snapshot, 0, received);
            var addrCopy = addr;
            new Thread(() => FakeSend(snapshot, conn, addrCopy)) { IsBackground = true }.Start();
            return;
        }

        Unexpected(buf, received, conn, ref addr);
    }

    private void OnInbound(byte[] buf, uint received, int ipHl, TrackedConnection conn, ref WinDivertAddress addr)
    {
        if (conn.SynSeq == -1) { Unexpected(buf, received, conn, ref addr); return; }

        var flags = IpTcp.Flags(buf, ipHl);
        var payload = IpTcp.PayloadLen(buf);
        var seq = IpTcp.SeqNum(buf, ipHl);
        var ack = IpTcp.AckNum(buf, ipHl);

        var synAck = IpTcp.HasFlag(flags, IpTcp.FlagAck) && IpTcp.HasFlag(flags, IpTcp.FlagSyn)
            && !IpTcp.HasFlag(flags, IpTcp.FlagRst) && !IpTcp.HasFlag(flags, IpTcp.FlagFin) && payload == 0;
        var ackOnly = IpTcp.HasFlag(flags, IpTcp.FlagAck) && !IpTcp.HasFlag(flags, IpTcp.FlagSyn)
            && !IpTcp.HasFlag(flags, IpTcp.FlagRst) && !IpTcp.HasFlag(flags, IpTcp.FlagFin) && payload == 0;

        if (synAck)
        {
            if (conn.SynAckSeq != -1 && conn.SynAckSeq != seq) { Unexpected(buf, received, conn, ref addr); return; }
            if (ack != ((conn.SynSeq + 1) & 0xFFFFFFFF)) { Unexpected(buf, received, conn, ref addr); return; }
            conn.SynAckSeq = seq;
            WinDivert.Send(_divert, buf, received, ref addr);
            return;
        }

        if (ackOnly && conn.FakeSent)
        {
            if (conn.SynAckSeq == -1 || ((conn.SynAckSeq + 1) & 0xFFFFFFFF) != seq) { Unexpected(buf, received, conn, ref addr); return; }
            if (ack != ((conn.SynSeq + 1) & 0xFFFFFFFF)) { Unexpected(buf, received, conn, ref addr); return; }

            conn.Monitor = false;
            conn.Signal.TrySetResult("fake_data_ack_recv");
            return;
        }

        Unexpected(buf, received, conn, ref addr);
    }

    private void FakeSend(byte[] snapshot, TrackedConnection conn, WinDivertAddress addr)
    {
        Thread.Sleep(1);
        lock (conn.Lock)
        {
            if (!conn.Monitor) return;
            var seq = (uint)(((conn.SynSeq + 1) - conn.FakeData.Length) & 0xFFFFFFFF);
            var packet = IpTcp.BuildFakePayloadPacket(snapshot, conn.FakeData, seq);
            conn.FakeSent = true;
            WinDivert.CalcChecksums(packet, (uint)packet.Length, ref addr);
            WinDivert.Send(_divert, packet, (uint)packet.Length, ref addr);
        }
    }

    private void Unexpected(byte[] buf, uint received, TrackedConnection conn, ref WinDivertAddress addr)
    {
        Close(conn.Outgoing);
        Close(conn.Incoming);
        conn.Monitor = false;
        conn.Signal.TrySetResult("unexpected_close");
        WinDivert.Send(_divert, buf, received, ref addr);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            Socket incoming;
            try { incoming = await listener.AcceptAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e) { AppLog.Warn($"engine accept: {e.Message}"); break; }
            _ = HandleAsync(incoming, ct);
        }
    }

    private async Task HandleAsync(Socket incoming, CancellationToken ct)
    {
        Socket? outgoing = null;
        TrackedConnection? conn = null;
        try
        {
            ConfigureSocket(incoming);

            var fake = TlsClientHello.Build(_fakeSniBytes);
            outgoing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            outgoing.Bind(new IPEndPoint(IPAddress.Parse(_interfaceIpText), 0));
            ConfigureSocket(outgoing);

            var srcPort = (ushort)((IPEndPoint)outgoing.LocalEndPoint!).Port;
            var key = new ConnKey(_interfaceIp, srcPort, _connectIpValue, (ushort)_connectPort);
            conn = new TrackedConnection { Key = key, FakeData = fake, Outgoing = outgoing, Incoming = incoming };
            _connections[key] = conn;

            try { await outgoing.ConnectAsync(IPAddress.Parse(_connectIp), _connectPort, ct); }
            catch
            {
                conn.Monitor = false;
                _connections.TryRemove(key, out _);
                Close(outgoing);
                Close(incoming);
                return;
            }

            var signal = await WaitSignalAsync(conn, TimeSpan.FromSeconds(2));
            if (signal != "fake_data_ack_recv")
            {
                conn.Monitor = false;
                _connections.TryRemove(key, out _);
                Close(outgoing);
                Close(incoming);
                return;
            }

            conn.Monitor = false;
            _connections.TryRemove(key, out _);

            await RelayAsync(incoming, outgoing);
        }
        catch (Exception e)
        {
            AppLog.Warn($"engine handle: {e.Message}");
            if (conn is not null)
            {
                conn.Monitor = false;
                _connections.TryRemove(conn.Key, out _);
            }
            Close(outgoing);
            Close(incoming);
        }
    }

    private static async Task<string> WaitSignalAsync(TrackedConnection conn, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(conn.Signal.Task, Task.Delay(timeout));
        return completed == conn.Signal.Task ? conn.Signal.Task.Result : "timeout";
    }

    private static async Task RelayAsync(Socket a, Socket b)
    {
        await Task.WhenAll(ForwardAsync(a, b), ForwardAsync(b, a));
        Close(a);
        Close(b);
    }

    private static async Task ForwardAsync(Socket src, Socket dst)
    {
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                var read = await src.ReceiveAsync(buffer.AsMemory(), SocketFlags.None);
                if (read == 0) break;
                var sent = 0;
                while (sent < read)
                    sent += await dst.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None);
            }
        }
        catch { }
        finally
        {
            try { src.Shutdown(SocketShutdown.Both); } catch { }
            try { dst.Shutdown(SocketShutdown.Both); } catch { }
        }
    }

    private void ConfigureSocket(Socket s)
    {
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        s.NoDelay = true;
        var size = _fastMode ? 32768 : 262144;
        s.ReceiveBufferSize = size;
        s.SendBufferSize = size;
    }

    private static uint IpToUint(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static string GetDefaultInterfaceIPv4(string target, int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(IPAddress.Parse(target), port);
            var ip = (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            if (!string.IsNullOrEmpty(ip) && !IPAddress.IsLoopback(IPAddress.Parse(ip)))
                return ip;

            return NetworkHelper.GetLocalPhysicalIPAddress();
        }
        catch
        {
            return NetworkHelper.GetLocalPhysicalIPAddress();
        }
    }

    private static void Close(Socket? s)
    {
        try { s?.Close(); } catch { }
    }

    public void Dispose() => Stop();
}
