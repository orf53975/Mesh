﻿/*
Technitium Mesh
Copyright (C) 2018  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Proxy;

namespace MeshCore.Network.DHT
{
    public enum DhtNetworkType
    {
        IPv4Internet = 1,
        IPv6Internet = 2,
        LocalNetwork = 3,
        TorNetwork = 4
    }

    public class DhtManager : IDisposable
    {
        #region variables

        const string DHT_BOOTSTRAP_URL = "https://mesh.im/dht-bootstrap.bin";

        const int WRITE_BUFFERED_STREAM_SIZE = 128;

        const int SOCKET_CONNECTION_TIMEOUT = 2000;
        const int SOCKET_SEND_TIMEOUT = 5000;
        const int SOCKET_RECV_TIMEOUT = 5000;

        const int NETWORK_WATCHER_INTERVAL = 15000;

        const int LOCAL_DISCOVERY_ANNOUNCE_PORT = 41988;
        const int ANNOUNCEMENT_INTERVAL = 60000;
        const int ANNOUNCEMENT_RETRY_INTERVAL = 2000;
        const int ANNOUNCEMENT_RETRY_COUNT = 3;

        const int BUFFER_MAX_SIZE = 32;

        const string IPV6_MULTICAST_IP = "FF12::1";

        readonly Timer _networkWatcher;
        readonly List<NetworkInfo> _networks = new List<NetworkInfo>();
        readonly List<LocalNetworkDhtManager> _localNetworkDhtManagers = new List<LocalNetworkDhtManager>();

        readonly DhtNode _ipv4InternetDhtNode;
        readonly DhtNode _ipv6InternetDhtNode;
        readonly DhtNode _torInternetDhtNode;

        #endregion

        #region constructor

        public DhtManager(int localDhtPort, string torOnionAddress, IDhtConnectionManager connectionManager, IEnumerable<EndPoint> ipv4BootstrapNodes, IEnumerable<EndPoint> ipv6BootstrapNodes, IEnumerable<EndPoint> torBootstrapNodes, NetProxy proxy, bool enableLocalNetworkDht, bool enableTorNetworkDht)
        {
            //init internet dht nodes
            _ipv4InternetDhtNode = new DhtNode(connectionManager, new IPEndPoint(IPAddress.Any, localDhtPort), true);
            _ipv6InternetDhtNode = new DhtNode(connectionManager, new IPEndPoint(IPAddress.IPv6Any, localDhtPort), true);

            //add known bootstrap nodes
            _ipv4InternetDhtNode.AddNode(ipv4BootstrapNodes);
            _ipv6InternetDhtNode.AddNode(ipv6BootstrapNodes);

            if (enableTorNetworkDht)
            {
                //init tor dht node
                _torInternetDhtNode = new DhtNode(connectionManager, new DomainEndPoint(torOnionAddress, localDhtPort), true);
                //add known bootstrap nodes
                _torInternetDhtNode.AddNode(torBootstrapNodes);
            }

            //add bootstrap nodes via web
            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                try
                {
                    using (WebClientEx wC = new WebClientEx())
                    {
                        wC.Proxy = proxy;

                        using (BinaryReader bR = new BinaryReader(new MemoryStream(wC.DownloadData(DHT_BOOTSTRAP_URL))))
                        {
                            int count = bR.ReadByte();

                            for (int i = 0; i < count; i++)
                            {
                                EndPoint nodeEP = EndPointExtension.Parse(bR);

                                switch (nodeEP.AddressFamily)
                                {
                                    case AddressFamily.InterNetwork:
                                        _ipv4InternetDhtNode.AddNode(nodeEP);
                                        break;

                                    case AddressFamily.InterNetworkV6:
                                        _ipv6InternetDhtNode.AddNode(nodeEP);
                                        break;

                                    case AddressFamily.Unspecified:
                                        _torInternetDhtNode?.AddNode(nodeEP);
                                        break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);
                }
            });

            if (enableLocalNetworkDht)
            {
                //start network watcher
                _networkWatcher = new Timer(NetworkWatcherAsync, null, 1000, NETWORK_WATCHER_INTERVAL);
            }
        }

        #endregion

        #region IDisposable

        bool _disposed = false;

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_networkWatcher != null)
                    _networkWatcher.Dispose();

                lock (_localNetworkDhtManagers)
                {
                    foreach (LocalNetworkDhtManager localNetworkDhtManager in _localNetworkDhtManagers)
                        localNetworkDhtManager.Dispose();

                    _localNetworkDhtManagers.Clear();
                }

                if (_ipv4InternetDhtNode != null)
                    _ipv4InternetDhtNode.Dispose();

                if (_ipv6InternetDhtNode != null)
                    _ipv6InternetDhtNode.Dispose();

                if (_torInternetDhtNode != null)
                    _torInternetDhtNode.Dispose();

                _disposed = true;
            }
        }

        #endregion

        #region private

        private void NetworkWatcherAsync(object state)
        {
            try
            {
                bool networkChanged = false;
                List<NetworkInfo> newNetworks = new List<NetworkInfo>();

                {
                    List<NetworkInfo> currentNetworks = NetUtilities.GetNetworkInfo();

                    networkChanged = (currentNetworks.Count != _networks.Count);

                    foreach (NetworkInfo currentNetwork in currentNetworks)
                    {
                        if (!_networks.Contains(currentNetwork))
                        {
                            networkChanged = true;
                            newNetworks.Add(currentNetwork);
                        }
                    }

                    _networks.Clear();
                    _networks.AddRange(currentNetworks);
                }

                if (networkChanged)
                {
                    lock (_localNetworkDhtManagers)
                    {
                        //remove local network dht manager with offline networks
                        {
                            List<LocalNetworkDhtManager> localNetworkDhtManagersToRemove = new List<LocalNetworkDhtManager>();

                            foreach (LocalNetworkDhtManager localNetworkDhtManager in _localNetworkDhtManagers)
                            {
                                if (!_networks.Contains(localNetworkDhtManager.Network))
                                    localNetworkDhtManagersToRemove.Add(localNetworkDhtManager);
                            }

                            foreach (LocalNetworkDhtManager localNetworkDhtManager in localNetworkDhtManagersToRemove)
                            {
                                localNetworkDhtManager.Dispose();
                                _localNetworkDhtManagers.Remove(localNetworkDhtManager);
                            }
                        }

                        //add local network dht managers for new online networks
                        if (newNetworks.Count > 0)
                        {
                            foreach (NetworkInfo network in _networks)
                            {
                                if (IPAddress.IsLoopback(network.LocalIP))
                                    continue; //skip loopback networks

                                _localNetworkDhtManagers.Add(new LocalNetworkDhtManager(network));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Write(this.GetType().Name, ex);
            }
        }

        #endregion

        #region public

        public void AcceptInternetDhtConnection(Stream s, EndPoint remoteNodeEP)
        {
            switch (remoteNodeEP.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    _ipv4InternetDhtNode.AcceptConnection(s, remoteNodeEP);
                    break;

                case AddressFamily.InterNetworkV6:
                    _ipv6InternetDhtNode.AcceptConnection(s, remoteNodeEP);
                    break;

                case AddressFamily.Unspecified:
                    _torInternetDhtNode.AcceptConnection(s, remoteNodeEP);
                    break;

                default:
                    throw new NotSupportedException("AddressFamily not supported.");
            }
        }

        public void BeginFindPeers(BinaryNumber networkId, bool localNetworkOnly, Action<DhtNetworkType, PeerEndPoint[]> callback)
        {
            if (!localNetworkOnly)
            {
                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = _ipv4InternetDhtNode.FindPeers(networkId);
                            if ((peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.IPv4Internet, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }

                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = _ipv6InternetDhtNode.FindPeers(networkId);
                            if ((peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.IPv6Internet, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }

                if (_torInternetDhtNode != null)
                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = _torInternetDhtNode.FindPeers(networkId);
                            if ((peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.TorNetwork, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }
            }

            lock (_localNetworkDhtManagers)
            {
                foreach (LocalNetworkDhtManager localNetworkDhtManager in _localNetworkDhtManagers)
                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = localNetworkDhtManager.DhtNode.FindPeers(networkId);
                            if ((peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.LocalNetwork, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }
            }
        }

        public void BeginAnnounce(BinaryNumber networkId, bool localNetworkOnly, PeerEndPoint serviceEP, Action<DhtNetworkType, PeerEndPoint[]> callback)
        {
            if (!localNetworkOnly)
            {
                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = _ipv4InternetDhtNode.Announce(networkId, serviceEP);
                            if ((callback != null) && (peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.IPv4Internet, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }

                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = _ipv6InternetDhtNode.Announce(networkId, serviceEP);
                            if ((callback != null) && (peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.IPv6Internet, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }

                if (_torInternetDhtNode != null)
                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = _torInternetDhtNode.FindPeers(networkId);
                            if ((peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.TorNetwork, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }
            }

            lock (_localNetworkDhtManagers)
            {
                foreach (LocalNetworkDhtManager localNetworkDhtManager in _localNetworkDhtManagers)
                {
                    Thread t = new Thread(delegate (object state)
                    {
                        try
                        {
                            PeerEndPoint[] peers = localNetworkDhtManager.DhtNode.Announce(networkId, serviceEP);
                            if ((callback != null) && (peers != null) && (peers.Length > 0))
                                callback(DhtNetworkType.LocalNetwork, peers);
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    });

                    t.IsBackground = true;
                    t.Start();
                }
            }
        }

        public EndPoint[] GetIPv4DhtNodes()
        {
            return _ipv4InternetDhtNode.GetAllNodeEPs(false);
        }

        public EndPoint[] GetIPv6DhtNodes()
        {
            return _ipv6InternetDhtNode.GetAllNodeEPs(false);
        }

        public EndPoint[] GetTorDhtNodes()
        {
            if (_torInternetDhtNode == null)
                return new EndPoint[] { };

            return _torInternetDhtNode.GetAllNodeEPs(false);
        }

        public EndPoint[] GetLanDhtNodes()
        {
            List<EndPoint> nodeEPs = new List<EndPoint>();

            lock (_localNetworkDhtManagers)
            {
                foreach (LocalNetworkDhtManager localDht in _localNetworkDhtManagers)
                    nodeEPs.AddRange(localDht.DhtNode.GetAllNodeEPs(false));
            }

            return nodeEPs.ToArray();
        }

        public EndPoint[] GetIPv4KRandomNodeEPs()
        {
            return _ipv4InternetDhtNode.GetKRandomNodeEPs();
        }

        #endregion

        #region properties

        public BinaryNumber IPv4DhtNodeId
        { get { return _ipv4InternetDhtNode.LocalNodeID; } }

        public BinaryNumber IPv6DhtNodeId
        { get { return _ipv6InternetDhtNode.LocalNodeID; } }

        public BinaryNumber TorDhtNodeId
        {
            get
            {
                if (_torInternetDhtNode == null)
                    return null;

                return _torInternetDhtNode.LocalNodeID;
            }
        }

        public int IPv4DhtTotalNodes
        { get { return _ipv4InternetDhtNode.TotalNodes; } }

        public int IPv6DhtTotalNodes
        { get { return _ipv6InternetDhtNode.TotalNodes; } }

        public int TorDhtTotalNodes
        {
            get
            {
                if (_torInternetDhtNode == null)
                    return 0;

                return _torInternetDhtNode.TotalNodes;
            }
        }

        public int LanDhtTotalNodes
        {
            get
            {
                int totalNodes = 0;

                lock (_localNetworkDhtManagers)
                {
                    foreach (LocalNetworkDhtManager localDht in _localNetworkDhtManagers)
                        totalNodes += localDht.DhtNode.TotalNodes;
                }

                return totalNodes;
            }
        }

        #endregion

        class LocalNetworkDhtManager : IDhtConnectionManager, IDisposable
        {
            #region variables

            readonly NetworkInfo _network;

            readonly Socket _udpListener;
            readonly Thread _udpListenerThread;

            readonly Socket _tcpListener;
            readonly Thread _tcpListenerThread;

            readonly IPEndPoint _dhtEndPoint;
            readonly DhtNode _dhtNode;

            readonly Timer _announceTimer;

            #endregion

            #region constructor

            public LocalNetworkDhtManager(NetworkInfo network)
            {
                _network = network;

                //start udp & tcp listeners
                switch (_network.LocalIP.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        _udpListener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        break;

                    case AddressFamily.InterNetworkV6:
                        _udpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                        _tcpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);

                        if ((Environment.OSVersion.Platform == PlatformID.Win32NT) && (Environment.OSVersion.Version.Major >= 6))
                        {
                            //windows vista & above
                            _udpListener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                            _tcpListener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                        }
                        break;

                    default:
                        throw new NotSupportedException("Address family not supported.");
                }

                _udpListener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                _udpListener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                _udpListener.Bind(new IPEndPoint(_network.LocalIP, LOCAL_DISCOVERY_ANNOUNCE_PORT));

                _tcpListener.Bind(new IPEndPoint(_network.LocalIP, 0));
                _tcpListener.Listen(10);

                _dhtEndPoint = _tcpListener.LocalEndPoint as IPEndPoint;

                //init dht node
                _dhtNode = new DhtNode(this, _dhtEndPoint, false);

                if (_udpListener.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    NetworkInterface nic = _network.Interface;
                    if ((nic.OperationalStatus == OperationalStatus.Up) && (nic.Supports(NetworkInterfaceComponent.IPv6)) && nic.SupportsMulticast)
                    {
                        try
                        {
                            _udpListener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(IPAddress.Parse(IPV6_MULTICAST_IP), nic.GetIPProperties().GetIPv6Properties().Index));
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);
                        }
                    }
                }

                //start reading packets
                _udpListenerThread = new Thread(RecvDataAsync);
                _udpListenerThread.IsBackground = true;
                _udpListenerThread.Start();

                //start accepting connections
                _tcpListenerThread = new Thread(AcceptTcpConnectionAsync);
                _tcpListenerThread.IsBackground = true;
                _tcpListenerThread.Start();

                //announce async
                _announceTimer = new Timer(AnnounceAsync, null, 1000, Timeout.Infinite);
            }

            #endregion

            #region IDisposable

            bool _disposed = false;

            public void Dispose()
            {
                if (!_disposed)
                {
                    if (_udpListener != null)
                        _udpListener.Dispose();

                    if (_tcpListener != null)
                        _tcpListener.Dispose();

                    if (_dhtNode != null)
                        _dhtNode.Dispose();

                    if (_announceTimer != null)
                        _announceTimer.Dispose();

                    _disposed = true;
                }
            }

            #endregion

            #region private

            private void RecvDataAsync(object parameter)
            {
                EndPoint remoteEP = null;
                byte[] buffer = new byte[BUFFER_MAX_SIZE];
                int bytesRecv;

                if (_udpListener.AddressFamily == AddressFamily.InterNetwork)
                    remoteEP = new IPEndPoint(IPAddress.Any, 0);
                else
                    remoteEP = new IPEndPoint(IPAddress.IPv6Any, 0);

                #region this code ignores ICMP port unreachable responses which creates SocketException in ReceiveFrom()

                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    const uint IOC_IN = 0x80000000;
                    const uint IOC_VENDOR = 0x18000000;
                    const uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;

                    _udpListener.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
                }

                #endregion

                try
                {
                    while (true)
                    {
                        //receive message from remote
                        bytesRecv = _udpListener.ReceiveFrom(buffer, ref remoteEP);

                        if (bytesRecv > 0)
                        {
                            IPAddress remoteNodeIP = (remoteEP as IPEndPoint).Address;

                            if (NetUtilities.IsIPv4MappedIPv6Address(remoteNodeIP))
                                remoteNodeIP = NetUtilities.ConvertFromIPv4MappedIPv6Address(remoteNodeIP);

                            try
                            {
                                DhtNodeDiscoveryPacket packet = new DhtNodeDiscoveryPacket(new MemoryStream(buffer, false));

                                IPEndPoint remoteNodeEP = new IPEndPoint(remoteNodeIP, packet.DhtPort);

                                if (!remoteNodeEP.Equals(_dhtEndPoint))
                                {
                                    //add node
                                    _dhtNode.AddNode(remoteNodeEP);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.Write(this.GetType().Name, ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);
                }
            }

            private void AcceptTcpConnectionAsync(object parameter)
            {
                try
                {
                    while (true)
                    {
                        Socket socket = _tcpListener.Accept();

                        socket.NoDelay = true;
                        socket.SendTimeout = SOCKET_SEND_TIMEOUT;
                        socket.ReceiveTimeout = SOCKET_RECV_TIMEOUT;

                        Thread t = new Thread(delegate (object state)
                        {
                            Socket clientSocket = state as Socket;

                            try
                            {
                                using (Stream s = new WriteBufferedStream(new NetworkStream(clientSocket, true), WRITE_BUFFERED_STREAM_SIZE))
                                {
                                    IPEndPoint remoteNodeEP = clientSocket.RemoteEndPoint as IPEndPoint;

                                    if (NetUtilities.IsIPv4MappedIPv6Address(remoteNodeEP.Address))
                                        remoteNodeEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(remoteNodeEP.Address), remoteNodeEP.Port);

                                    _dhtNode.AcceptConnection(s, remoteNodeEP);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.Write(this.GetType().Name, ex);
                                clientSocket.Dispose();
                            }
                        });

                        t.IsBackground = true;
                        t.Start(socket);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);
                }
            }

            private void Broadcast(byte[] buffer, int offset, int count)
            {
                if (_network.LocalIP.AddressFamily == AddressFamily.InterNetwork)
                {
                    IPAddress broadcastIP = _network.BroadcastIP;

                    if (_udpListener.AddressFamily == AddressFamily.InterNetworkV6)
                        broadcastIP = NetUtilities.ConvertToIPv4MappedIPv6Address(broadcastIP);

                    try
                    {
                        _udpListener.SendTo(buffer, offset, count, SocketFlags.None, new IPEndPoint(broadcastIP, LOCAL_DISCOVERY_ANNOUNCE_PORT));
                    }
                    catch (Exception ex)
                    {
                        Debug.Write(this.GetType().Name, ex);
                    }
                }
                else
                {
                    try
                    {
                        _udpListener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, _network.Interface.GetIPProperties().GetIPv6Properties().Index);
                        _udpListener.SendTo(buffer, offset, count, SocketFlags.None, new IPEndPoint(IPAddress.Parse(IPV6_MULTICAST_IP), LOCAL_DISCOVERY_ANNOUNCE_PORT));
                    }
                    catch (Exception ex)
                    {
                        Debug.Write(this.GetType().Name, ex);
                    }
                }
            }

            private void AnnounceAsync(object state)
            {
                try
                {
                    byte[] announcement = (new DhtNodeDiscoveryPacket((ushort)_dhtEndPoint.Port)).ToArray();

                    for (int i = 0; i < ANNOUNCEMENT_RETRY_COUNT; i++)
                    {
                        Broadcast(announcement, 0, announcement.Length);

                        if (i < ANNOUNCEMENT_RETRY_COUNT - 1)
                            Thread.Sleep(ANNOUNCEMENT_RETRY_INTERVAL);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);
                }
                finally
                {
                    if (!_disposed)
                    {
                        if (_dhtNode.TotalNodes < 2)
                            _announceTimer.Change(ANNOUNCEMENT_INTERVAL, Timeout.Infinite);
                    }
                }
            }

            #endregion

            #region IDhtConnectionManager support

            Stream IDhtConnectionManager.GetConnection(EndPoint remoteNodeEP)
            {
                Socket socket = null;

                try
                {
                    socket = new Socket(remoteNodeEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    IAsyncResult result = socket.BeginConnect(remoteNodeEP, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(SOCKET_CONNECTION_TIMEOUT))
                        throw new SocketException((int)SocketError.TimedOut);

                    if (!socket.Connected)
                        throw new SocketException((int)SocketError.ConnectionRefused);

                    socket.NoDelay = true;
                    socket.SendTimeout = SOCKET_SEND_TIMEOUT;
                    socket.ReceiveTimeout = SOCKET_RECV_TIMEOUT;

                    return new WriteBufferedStream(new NetworkStream(socket, true), WRITE_BUFFERED_STREAM_SIZE);
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);

                    if (socket != null)
                        socket.Dispose();

                    throw;
                }
            }

            #endregion

            #region properties

            public NetworkInfo Network
            { get { return _network; } }

            public DhtNode DhtNode
            { get { return _dhtNode; } }

            #endregion
        }

        class DhtNodeDiscoveryPacket
        {
            #region variables

            readonly ushort _dhtPort;

            #endregion

            #region constructor

            public DhtNodeDiscoveryPacket(ushort dhtPort)
            {
                _dhtPort = dhtPort;
            }

            public DhtNodeDiscoveryPacket(Stream s)
            {
                switch (s.ReadByte()) //version
                {
                    case 1:
                        byte[] buffer = new byte[2];
                        s.ReadBytes(buffer, 0, 2);
                        _dhtPort = BitConverter.ToUInt16(buffer, 0);
                        break;

                    case -1:
                        throw new EndOfStreamException();

                    default:
                        throw new IOException("DHT node discovery packet version not supported.");
                }
            }

            #endregion

            #region public

            public byte[] ToArray()
            {
                byte[] buffer = new byte[3];

                buffer[0] = 1; //version
                Buffer.BlockCopy(BitConverter.GetBytes(_dhtPort), 0, buffer, 1, 2); //service port

                return buffer;
            }

            #endregion

            #region properties

            public ushort DhtPort
            { get { return _dhtPort; } }

            #endregion
        }
    }
}
