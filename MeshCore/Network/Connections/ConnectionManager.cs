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

using MeshCore.Network.DHT;
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
using TechnitiumLibrary.Net.Tor;
using TechnitiumLibrary.Net.UPnP.Networking;

namespace MeshCore.Network.Connections
{
    public enum InternetConnectivityStatus
    {
        Identifying = 0,
        NoInternetConnection = 1,
        DirectInternetConnection = 2,
        HttpProxyInternetConnection = 3,
        Socks5ProxyInternetConnection = 4,
        NatInternetConnectionViaUPnPRouter = 5,
        NatOrFirewalledInternetConnection = 6,
        FirewalledInternetConnection = 7,
        ProxyConnectionFailed = 8,
        NoProxyInternetConnection = 9
    }

    public enum UPnPDeviceStatus
    {
        Identifying = 0,
        Disabled = 1,
        DeviceNotFound = 2,
        ExternalIpPrivate = 3,
        PortForwarded = 4,
        PortForwardingFailed = 5,
        PortForwardedNotAccessible = 6
    }

    public class ConnectionManager : IDhtConnectionManager, IDisposable
    {
        #region variables

        const int WRITE_BUFFERED_STREAM_SIZE = 8 * 1024;

        const int SOCKET_CONNECTION_TIMEOUT = 10000; //10 sec connection timeout
        const int SOCKET_INITIAL_SEND_TIMEOUT = 10000;
        const int SOCKET_INITIAL_RECV_TIMEOUT = 10000;

        const int TOR_CONNECTION_TIMEOUT = 30000; //30 sec connection timeout

        const int SOCKET_SEND_BUFFER_SIZE = 16 * 1024;
        const int SOCKET_RECV_BUFFER_SIZE = 16 * 1024;

        const int WRITE_TIMEOUT = 30000; //30 sec socket write  timeout
        const int READ_TIMEOUT = 120000; //keep socket open for long time to allow tunnelling requests between time

        readonly MeshNode _node;

        readonly BinaryNumber _localPeerId;

        readonly Dictionary<EndPoint, object> _makeConnectionList = new Dictionary<EndPoint, object>();
        readonly Dictionary<EndPoint, object> _makeVirtualConnectionList = new Dictionary<EndPoint, object>();

        readonly Dictionary<EndPoint, Connection> _connectionListByEndPoint = new Dictionary<EndPoint, Connection>();
        readonly Dictionary<BinaryNumber, Connection> _connectionListByPeerId = new Dictionary<BinaryNumber, Connection>();

        //tcp listener
        readonly Socket _tcpListener;
        readonly Thread _tcpListenerThread;

        //tor controller
        readonly TorController _torController;
        readonly TorHiddenServiceInfo _torHiddenServiceInfo;
        readonly NetProxy _torProxy;

        //dht
        readonly DhtManager _dhtManager;

        //tcp relay
        const int MAX_TCP_RELAY_CLIENT_CONNECTIONS = 3;
        readonly List<Connection> _tcpRelayClientConnections = new List<Connection>();

        const int TCP_RELAY_CLIENT_TIMER_INTERVAL = 30000;
        readonly Timer _tcpRelayClientTimer;

        readonly Dictionary<BinaryNumber, List<Connection>> _tcpRelayServerHostedNetworkConnections = new Dictionary<BinaryNumber, List<Connection>>();

        //internet connectivity
        const int CONNECTIVITY_CHECK_TIMER_INTERVAL = 60 * 1000;
        readonly Timer _connectivityCheckTimer;

        readonly Uri IPv4_CONNECTIVITY_CHECK_WEB_SERVICE = new Uri("https://mesh.im/connectivity/check.aspx");
        InternetConnectivityStatus _ipv4InternetStatus = InternetConnectivityStatus.Identifying;
        InternetGatewayDevice _upnpDevice;
        UPnPDeviceStatus _upnpDeviceStatus = UPnPDeviceStatus.Identifying;
        IPAddress _ipv4LocalLiveIP;
        IPAddress _upnpExternalIP;
        EndPoint _ipv4ConnectivityCheckExternalEP;

        InternetConnectivityStatus _ipv6InternetStatus = InternetConnectivityStatus.Identifying;
        IPAddress _ipv6LocalLiveIP;

        readonly int _localPort;

        #endregion

        #region constructor

        public ConnectionManager(MeshNode node)
        {
            _node = node;

            IPEndPoint localEP;

            if (_node.Type == MeshNodeType.Tor)
            {
                _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                localEP = new IPEndPoint(IPAddress.Loopback, _node.LocalPort);
            }
            else
            {
                switch (Environment.OSVersion.Platform)
                {
                    case PlatformID.Win32NT:
                        if (Environment.OSVersion.Version.Major < 6)
                        {
                            //below vista
                            _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                            localEP = new IPEndPoint(IPAddress.Any, _node.LocalPort);
                        }
                        else
                        {
                            //vista & above
                            _tcpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                            _tcpListener.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
                            localEP = new IPEndPoint(IPAddress.IPv6Any, _node.LocalPort);
                        }
                        break;

                    case PlatformID.Unix:
                        if (Socket.OSSupportsIPv6)
                        {
                            _tcpListener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                            localEP = new IPEndPoint(IPAddress.IPv6Any, _node.LocalPort);
                        }
                        else
                        {
                            _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                            localEP = new IPEndPoint(IPAddress.Any, _node.LocalPort);
                        }

                        break;

                    default: //unknown
                        _tcpListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        localEP = new IPEndPoint(IPAddress.Any, _node.LocalPort);
                        break;
                }
            }

            try
            {
                _tcpListener.Bind(localEP);
                _tcpListener.Listen(10);
            }
            catch (Exception ex)
            {
                Debug.Write(this.GetType().Name, ex);

                localEP.Port = 0;

                _tcpListener.Bind(localEP);
                _tcpListener.Listen(10);
            }

            _localPort = (_tcpListener.LocalEndPoint as IPEndPoint).Port;
            _localPeerId = BinaryNumber.GenerateRandomNumber256();

            //init tor controller
            _torController = new TorController(_node.TorExecutableFile);
            _torController.ControlPort = _localPort + 1;
            _torController.Socks5EndPoint = new IPEndPoint(IPAddress.Loopback, _localPort + 2);
            _torProxy = new NetProxy(new SocksClient(_torController.Socks5EndPoint));

            if (_node.Proxy != null)
            {
                switch (_node.Proxy.Type)
                {
                    case NetProxyType.Http:
                        _torController.ProxyType = TorProxyType.Http;
                        _torController.ProxyHost = _node.Proxy.Address;
                        _torController.ProxyPort = _node.Proxy.Port;
                        _torController.ProxyCredential = _node.Proxy.Credential;
                        break;

                    case NetProxyType.Socks5:
                        _torController.ProxyType = TorProxyType.Socks5;
                        _torController.ProxyHost = _node.Proxy.Address;
                        _torController.ProxyPort = _node.Proxy.Port;
                        _torController.ProxyCredential = _node.Proxy.Credential;
                        break;
                }
            }

            if (_node.Type == MeshNodeType.Tor)
            {
                //if node type is tor then start tor in advance
                _torController.Start();

                //start hidden service for incoming connections via tor
                _torHiddenServiceInfo = _torController.CreateHiddenService(_localPort);
            }

            //init dht node
            _dhtManager = new DhtManager(_localPort, _torHiddenServiceInfo?.ServiceId + ".onion", this, _node.IPv4BootstrapDhtNodes, _node.IPv6BootstrapDhtNodes, _node.TorBootstrapDhtNodes, _node.Proxy, (_node.Type == MeshNodeType.P2P), (_node.Type == MeshNodeType.Tor));

            //start accepting connections
            _tcpListenerThread = new Thread(AcceptTcpConnectionAsync);
            _tcpListenerThread.IsBackground = true;
            _tcpListenerThread.Start();

            //start tcp relay client timer
            _tcpRelayClientTimer = new Timer(delegate (object state)
            {
                try
                {
                    int tcpRelayClientConnectionCount;

                    lock (_tcpRelayClientConnections)
                    {
                        tcpRelayClientConnectionCount = _tcpRelayClientConnections.Count;
                    }

                    if (tcpRelayClientConnectionCount < MAX_TCP_RELAY_CLIENT_CONNECTIONS)
                    {
                        //find tcp relay connections via DHT
                        foreach (IPEndPoint nodeEP in _dhtManager.GetIPv4KRandomNodeEPs())
                        {
                            ThreadPool.QueueUserWorkItem(delegate (object state2)
                            {
                                try
                                {
                                    Connection tcpRelayClientConnection = MakeConnection(state2 as IPEndPoint);

                                    lock (_tcpRelayClientConnections)
                                    {
                                        if (_tcpRelayClientConnections.Count < MAX_TCP_RELAY_CLIENT_CONNECTIONS)
                                        {
                                            _tcpRelayClientConnections.Add(tcpRelayClientConnection);
                                            tcpRelayClientConnection.EnableTcpRelayClientMode();
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.Write(this.GetType().Name, ex);
                                }
                            }, nodeEP);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);
                }
            }, null, Timeout.Infinite, TCP_RELAY_CLIENT_TIMER_INTERVAL);

            //start connectivity check timer
            _connectivityCheckTimer = new Timer(delegate (object state)
            {
                Thread t1 = new Thread(IPv4ConnectivityCheck);
                t1.IsBackground = true;
                t1.Start();

                Thread t2 = new Thread(IPv6ConnectivityCheck);
                t2.IsBackground = true;
                t2.Start();
            }, null, 1000, CONNECTIVITY_CHECK_TIMER_INTERVAL);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
        }

        bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                //shutdown tcp listener
                if (_tcpListener != null)
                    _tcpListener.Dispose();

                //stop dht manager
                if (_dhtManager != null)
                    _dhtManager.Dispose();

                if (_tcpRelayClientTimer != null)
                    _tcpRelayClientTimer.Dispose();

                //shutdown connectivity check timer
                if (_connectivityCheckTimer != null)
                    _connectivityCheckTimer.Dispose();

                //stop channel services
                List<Connection> connectionList = new List<Connection>();

                lock (_connectionListByEndPoint)
                {
                    foreach (Connection connection in _connectionListByEndPoint.Values)
                        connectionList.Add(connection);
                }

                foreach (Connection connection in connectionList)
                    connection.Dispose();
            }

            _disposed = true;
        }

        #endregion

        #region private

        private Socket MakeTcpConnection(EndPoint remoteNodeEP)
        {
            Socket socket = null;

            try
            {
                if ((_node.Type == MeshNodeType.Tor) || (remoteNodeEP.AddressFamily == AddressFamily.Unspecified))
                {
                    if (!_torController.IsRunning)
                    {
                        lock (_torController) //lock to prevent multiple start attempts
                        {
                            if (!_torController.IsRunning)
                                _torController.Start();
                        }
                    }

                    socket = _torProxy.Connect(remoteNodeEP, TOR_CONNECTION_TIMEOUT);
                }
                else if (_node.Proxy != null)
                {
                    socket = _node.Proxy.Connect(remoteNodeEP, SOCKET_CONNECTION_TIMEOUT);
                }
                else
                {
                    socket = new Socket(remoteNodeEP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    IAsyncResult result = socket.BeginConnect(remoteNodeEP, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(SOCKET_CONNECTION_TIMEOUT))
                        throw new SocketException((int)SocketError.TimedOut);

                    if (!socket.Connected)
                        throw new SocketException((int)SocketError.ConnectionRefused);
                }

                socket.NoDelay = true;
                socket.SendTimeout = SOCKET_INITIAL_SEND_TIMEOUT;
                socket.ReceiveTimeout = SOCKET_INITIAL_RECV_TIMEOUT;

                return socket;
            }
            catch
            {
                if (socket != null)
                    socket.Dispose();

                throw;
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
                    socket.SendTimeout = SOCKET_INITIAL_SEND_TIMEOUT;
                    socket.ReceiveTimeout = SOCKET_INITIAL_RECV_TIMEOUT;

                    ThreadPool.QueueUserWorkItem(delegate (object state)
                    {
                        try
                        {
                            Stream s = new WriteBufferedStream(new NetworkStream(socket, true), WRITE_BUFFERED_STREAM_SIZE);

                            AcceptDecoyHttpConnection(s);

                            //set socket options
                            socket.SendBufferSize = SOCKET_SEND_BUFFER_SIZE;
                            socket.ReceiveBufferSize = SOCKET_RECV_BUFFER_SIZE;

                            IPEndPoint remotePeerEP = socket.RemoteEndPoint as IPEndPoint;

                            if (NetUtilities.IsIPv4MappedIPv6Address(remotePeerEP.Address))
                                remotePeerEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(remotePeerEP.Address), remotePeerEP.Port);

                            AcceptConnectionInitiateProtocol(s, remotePeerEP);
                        }
                        catch (IOException)
                        {
                            //ignore
                            socket.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Debug.Write(this.GetType().Name, ex);

                            socket.Dispose();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.Write(this.GetType().Name, ex);
            }
        }

        private Connection AddConnection(Stream s, BinaryNumber remotePeerId, EndPoint remotePeerEP)
        {
            if (remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6)
            {
                IPEndPoint ep = remotePeerEP as IPEndPoint;

                if (ep.Address.ScopeId != 0)
                    remotePeerEP = new IPEndPoint(new IPAddress(ep.Address.GetAddressBytes()), ep.Port);
            }

            lock (_connectionListByEndPoint)
            {
                //check for self
                if (_localPeerId.Equals(remotePeerId))
                    return null;

                //check for existing connection by connection id
                if (_connectionListByEndPoint.ContainsKey(remotePeerEP))
                {
                    Connection existingConnection = _connectionListByEndPoint[remotePeerEP];

                    //check for virtual vs real connection
                    bool currentIsVirtual = Connection.IsStreamVirtualConnection(s);
                    bool existingIsVirtual = existingConnection.IsVirtualConnection;

                    if (existingIsVirtual && !currentIsVirtual)
                    {
                        //existing is virtual and current is real; remove existing connection
                        existingConnection.Dispose();
                    }
                    else if (currentIsVirtual)
                    {
                        //existing is real/virtual and current is virtual; keep existing connection
                        return null;
                    }
                }
                else if (_connectionListByPeerId.ContainsKey(remotePeerId)) //check for existing connection by peer id
                {
                    Connection existingConnection = _connectionListByPeerId[remotePeerId];

                    //check for virtual vs real connection
                    bool currentIsVirtual = Connection.IsStreamVirtualConnection(s);
                    bool existingIsVirtual = existingConnection.IsVirtualConnection;

                    if (existingIsVirtual && !currentIsVirtual)
                    {
                        //existing is virtual and current is real; remove existing connection
                        existingConnection.Dispose();
                    }
                    else if (currentIsVirtual)
                    {
                        //existing is real/virtual and current is virtual; keep existing connection
                        return null;
                    }
                    else
                    {
                        //compare existing and new peer ip end-point
                        if (AllowNewConnection(existingConnection.RemotePeerEP, remotePeerEP))
                        {
                            //remove existing connection and allow new connection
                            existingConnection.Dispose();
                        }
                        else
                        {
                            //keep existing connection
                            return null;
                        }
                    }
                }

                //add connection
                Connection connection = new Connection(this, s, remotePeerId, remotePeerEP);
                _connectionListByEndPoint.Add(remotePeerEP, connection);
                _connectionListByPeerId.Add(remotePeerId, connection);

                return connection;
            }
        }

        private bool AllowNewConnection(EndPoint existingIP, EndPoint newIP)
        {
            if (existingIP.AddressFamily != newIP.AddressFamily)
                return false;

            if (existingIP.AddressFamily != AddressFamily.Unspecified)
            {
                if (NetUtilities.IsPrivateIP((existingIP as IPEndPoint).Address))
                    return false;
            }

            return true;
        }

        private static void AcceptDecoyHttpConnection(Stream s)
        {
            //read http request
            int byteRead;
            int crlfCount = 0;

            while (true)
            {
                byteRead = s.ReadByte();
                switch (byteRead)
                {
                    case '\r':
                    case '\n':
                        crlfCount++;
                        break;

                    case -1:
                        throw new EndOfStreamException();

                    default:
                        crlfCount = 0;
                        break;
                }

                if (crlfCount == 4)
                    break; //http request completed
            }

            //write http response
            string httpHeaders = "HTTP/1.1 200 OK\r\n\r\n";

            httpHeaders = httpHeaders.Replace("$DATE", DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss"));
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(httpHeaders);

            s.Write(buffer, 0, buffer.Length);
            s.Flush();
        }

        private static void MakeDecoyHttpConnection(Stream s, EndPoint remotePeerEP)
        {
            //write http request
            string httpHeaders = "CONNECT $HOST HTTP/1.1\r\n\r\n";

            httpHeaders = httpHeaders.Replace("$HOST", remotePeerEP.ToString());
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(httpHeaders);

            s.Write(buffer, 0, buffer.Length);
            s.Flush();

            //read http response
            int byteRead;
            int crlfCount = 0;

            while (true)
            {
                byteRead = s.ReadByte();
                switch (byteRead)
                {
                    case '\r':
                    case '\n':
                        crlfCount++;
                        break;

                    case -1:
                        throw new EndOfStreamException();

                    default:
                        crlfCount = 0;
                        break;
                }

                if (crlfCount == 4)
                    break; //http request completed
            }
        }

        private Connection MakeConnectionInitiateProtocol(Stream s, EndPoint remotePeerEP)
        {
            try
            {
                //send request
                s.WriteByte(1); //version
                _localPeerId.WriteTo(s); //peer id
                s.Write(BitConverter.GetBytes(Convert.ToUInt16(_localPort)), 0, 2); //service port
                s.Flush();

                //read response
                int response = s.ReadByte();
                if (response < 0)
                    throw new EndOfStreamException();

                BinaryNumber remotePeerId = new BinaryNumber(s);

                switch (response)
                {
                    case 0:
                        Connection connection = AddConnection(s, remotePeerId, remotePeerEP);
                        if (connection == null)
                        {
                            //check for existing connection again!
                            Connection existingConnection = GetExistingConnection(remotePeerEP);
                            if (existingConnection != null)
                            {
                                s.Dispose();
                                return existingConnection;
                            }

                            existingConnection = GetExistingConnection(remotePeerId);
                            if (existingConnection != null)
                            {
                                s.Dispose();
                                return existingConnection;
                            }

                            throw new IOException("Cannot connect to remote peer: connection already exists.");
                        }

                        //set stream timeout
                        s.WriteTimeout = WRITE_TIMEOUT;
                        s.ReadTimeout = READ_TIMEOUT;

                        Debug.Write(this.GetType().Name, "MakeConnectionInitiateProtocol: connection made to " + remotePeerId.ToString() + " [" + remotePeerEP.ToString() + "]");

                        return connection;

                    case 1:
                        Thread.Sleep(500); //wait so that other thread gets time to add his connection in list so that this thread can pick same connection to proceed

                        //check for existing connection again!
                        {
                            Connection existingConnection = GetExistingConnection(remotePeerEP);
                            if (existingConnection != null)
                            {
                                s.Dispose();
                                return existingConnection;
                            }

                            existingConnection = GetExistingConnection(remotePeerId);
                            if (existingConnection != null)
                            {
                                s.Dispose();
                                return existingConnection;
                            }
                        }

                        throw new IOException("Cannot connect to remote peer: duplicate connection detected by remote peer.");

                    default:
                        throw new IOException("Invalid response was received.");
                }
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        #endregion

        #region public

        public Connection MakeConnection(EndPoint remotePeerEP)
        {
            if (remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6)
            {
                IPEndPoint ep = remotePeerEP as IPEndPoint;

                if (NetUtilities.IsIPv4MappedIPv6Address(ep.Address))
                    remotePeerEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(ep.Address), ep.Port);
            }

            //prevent multiple connection requests to same remote end-point
            lock (_makeConnectionList)
            {
                while (_makeConnectionList.ContainsKey(remotePeerEP))
                {
                    if (!Monitor.Wait(_makeConnectionList, SOCKET_CONNECTION_TIMEOUT))
                        throw new MeshException("Connection attempt for end-point already in progress.");
                }

                _makeConnectionList.Add(remotePeerEP, null);
            }

            try
            {
                //check if self
                switch (remotePeerEP.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        if (remotePeerEP.Equals(this.IPv4ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;

                    case AddressFamily.InterNetworkV6:
                        if (remotePeerEP.Equals(this.IPv6ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;
                }

                //check existing connection
                Connection existingConnection = GetExistingConnection(remotePeerEP);
                if (existingConnection != null)
                    return existingConnection;

                Socket socket = MakeTcpConnection(remotePeerEP);

                try
                {
                    Stream s = new WriteBufferedStream(new NetworkStream(socket, true), WRITE_BUFFERED_STREAM_SIZE);

                    MakeDecoyHttpConnection(s, remotePeerEP);

                    //set socket options
                    socket.SendBufferSize = SOCKET_SEND_BUFFER_SIZE;
                    socket.ReceiveBufferSize = SOCKET_RECV_BUFFER_SIZE;

                    return MakeConnectionInitiateProtocol(s, remotePeerEP);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            finally
            {
                lock (_makeConnectionList)
                {
                    _makeConnectionList.Remove(remotePeerEP);
                    Monitor.PulseAll(_makeConnectionList); //signal all waiting connection attempt threads
                }
            }
        }

        public Connection MakeVirtualConnection(Connection viaConnection, EndPoint remotePeerEP)
        {
            if (remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6)
            {
                IPEndPoint ep = remotePeerEP as IPEndPoint;

                if (NetUtilities.IsIPv4MappedIPv6Address(ep.Address))
                    remotePeerEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(ep.Address), ep.Port);
            }

            //prevent multiple virtual connection requests to same remote end-point
            lock (_makeVirtualConnectionList)
            {
                while (_makeVirtualConnectionList.ContainsKey(remotePeerEP))
                {
                    if (!Monitor.Wait(_makeVirtualConnectionList, SOCKET_CONNECTION_TIMEOUT))
                        throw new MeshException("Connection attempt for end-point already in progress.");
                }

                _makeVirtualConnectionList.Add(remotePeerEP, null);
            }

            try
            {
                //check if self
                switch (remotePeerEP.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        if (remotePeerEP.Equals(this.IPv4ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;

                    case AddressFamily.InterNetworkV6:
                        if (remotePeerEP.Equals(this.IPv6ExternalEndPoint))
                            throw new IOException("Cannot connect to remote port: self connection.");

                        break;
                }

                //check existing connection
                Connection existingConnection = GetExistingConnection(remotePeerEP);
                if (existingConnection != null)
                    return existingConnection;

                //create tunnel via peer
                Stream s = viaConnection.MakeTunnelConnection(remotePeerEP);

                //make new connection protocol begins
                return MakeConnectionInitiateProtocol(s, remotePeerEP);
            }
            finally
            {
                lock (_makeVirtualConnectionList)
                {
                    _makeVirtualConnectionList.Remove(remotePeerEP);
                    Monitor.PulseAll(_makeVirtualConnectionList); //signal all waiting connection attempt threads
                }
            }
        }

        public void AcceptConnectionInitiateProtocol(Stream s, EndPoint remotePeerEP)
        {
            //read version
            int version = s.ReadByte();

            switch (version)
            {
                case -1:
                    throw new EndOfStreamException();

                case 0: //DHT TCP support switch
                    _dhtManager.AcceptInternetDhtConnection(s, remotePeerEP);
                    break;

                case 1:
                    //read peer id
                    BinaryNumber remotePeerId = new BinaryNumber(s);

                    //read service port
                    byte[] remoteServicePort = new byte[2];
                    s.ReadBytes(remoteServicePort, 0, 2);

                    if (remotePeerEP.AddressFamily == AddressFamily.Unspecified)
                        remotePeerEP = new DomainEndPoint((remotePeerEP as DomainEndPoint).Address, BitConverter.ToUInt16(remoteServicePort, 0));
                    else
                        remotePeerEP = new IPEndPoint((remotePeerEP as IPEndPoint).Address, BitConverter.ToUInt16(remoteServicePort, 0));

                    //add
                    Connection connection = AddConnection(s, remotePeerId, remotePeerEP);
                    if (connection != null)
                    {
                        //send ok
                        s.WriteByte(0); //signal ok
                        _localPeerId.WriteTo(s); //peer id
                        s.Flush();

                        //set stream timeout
                        s.WriteTimeout = WRITE_TIMEOUT;
                        s.ReadTimeout = READ_TIMEOUT;

                        Debug.Write(this.GetType().Name, "AcceptConnectionInitiateProtocol: connection accepted from " + remotePeerId.ToString() + " [" + remotePeerEP.ToString() + "]");
                    }
                    else
                    {
                        //send cancel
                        s.WriteByte(1); //signal cancel
                        _localPeerId.WriteTo(s); //peer id
                        s.Flush();

                        throw new IOException("Cannot accept remote connection: duplicate connection detected.");
                    }

                    break;

                default:
                    throw new IOException("Cannot accept remote connection: protocol version not supported.");
            }
        }

        public Connection GetExistingConnection(EndPoint remotePeerEP)
        {
            if (remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6)
            {
                IPEndPoint ep = remotePeerEP as IPEndPoint;

                if (ep.Address.ScopeId != 0)
                    remotePeerEP = new IPEndPoint(new IPAddress(ep.Address.GetAddressBytes()), ep.Port);
            }

            lock (_connectionListByEndPoint)
            {
                if (_connectionListByEndPoint.ContainsKey(remotePeerEP))
                    return _connectionListByEndPoint[remotePeerEP];

                return null;
            }
        }

        public Connection GetExistingConnection(BinaryNumber remotePeerId)
        {
            lock (_connectionListByEndPoint)
            {
                if (_connectionListByPeerId.ContainsKey(remotePeerId))
                    return _connectionListByPeerId[remotePeerId];
            }

            return null;
        }

        public void ConnectionDisposed(Connection connection)
        {
            //remove connection from connection manager
            EndPoint remotePeerEP = connection.RemotePeerEP;

            if (remotePeerEP.AddressFamily == AddressFamily.InterNetworkV6)
            {
                IPEndPoint ep = remotePeerEP as IPEndPoint;

                if (ep.Address.ScopeId != 0)
                    remotePeerEP = new IPEndPoint(new IPAddress(ep.Address.GetAddressBytes()), ep.Port);
            }

            lock (_connectionListByEndPoint)
            {
                _connectionListByEndPoint.Remove(remotePeerEP);
                _connectionListByPeerId.Remove(connection.RemotePeerId);
            }

            lock (_tcpRelayClientConnections)
            {
                if (connection.IsTcpRelayClientModeEnabled)
                    _tcpRelayClientConnections.Remove(connection);
            }
        }

        public void ClientProfileProxyUpdated()
        {
            NetProxy proxy = _node.Proxy;

            if (proxy != null)
            {
                //stop tcp relay for all networks since this client switched to proxy and can no longer provide tcp relay service
                //TcpRelayService.StopAllTcpRelays();
            }
        }

        #endregion

        #region IDhtConnectionManager support

        Stream IDhtConnectionManager.GetConnection(EndPoint remoteNodeEP)
        {
            if (remoteNodeEP.AddressFamily == AddressFamily.InterNetworkV6)
            {
                IPEndPoint ep = remoteNodeEP as IPEndPoint;

                if (NetUtilities.IsIPv4MappedIPv6Address(ep.Address))
                    remoteNodeEP = new IPEndPoint(NetUtilities.ConvertFromIPv4MappedIPv6Address(ep.Address), ep.Port);
            }

            NetworkStream nS = new NetworkStream(MakeTcpConnection(remoteNodeEP), true);
            Stream s = new WriteBufferedStream(nS, 1024);

            try
            {
                MakeDecoyHttpConnection(s, remoteNodeEP);

                s.WriteByte(0); //DHT TCP support switch
            }
            catch
            {
                nS.Dispose();
                throw;
            }

            return s;
        }

        #endregion

        #region tcp relay

        public void TcpRelayServerRegisterHostedNetwork(Connection connection, BinaryNumber channelId)
        {
            //register hosted network connection
            lock (_tcpRelayServerHostedNetworkConnections)
            {
                if (_tcpRelayServerHostedNetworkConnections.ContainsKey(channelId))
                {
                    List<Connection> connections = _tcpRelayServerHostedNetworkConnections[channelId];

                    if (!connections.Contains(connection))
                        connections.Add(connection);
                }
                else
                {
                    List<Connection> connections = new List<Connection>();
                    connections.Add(connection);

                    _tcpRelayServerHostedNetworkConnections.Add(channelId, connections);
                }
            }

            //announce self on DHT for the hosted network
            _dhtManager.BeginAnnounce(channelId, false, new IPEndPoint(IPAddress.Any, _localPort), null);
        }

        public void TcpRelayServerUnregisterHostedNetwork(Connection connection, BinaryNumber channelId)
        {
            lock (_tcpRelayServerHostedNetworkConnections)
            {
                if (_tcpRelayServerHostedNetworkConnections.ContainsKey(channelId))
                {
                    List<Connection> connections = _tcpRelayServerHostedNetworkConnections[channelId];

                    connections.Remove(connection);

                    if (connections.Count == 0)
                        _tcpRelayServerHostedNetworkConnections.Remove(channelId);
                }
            }
        }

        public void TcpRelayServerUnregisterAllHostedNetworks(Connection connection)
        {
            lock (_tcpRelayServerHostedNetworkConnections)
            {
                List<BinaryNumber> emptyNetworks = new List<BinaryNumber>();

                foreach (KeyValuePair<BinaryNumber, List<Connection>> hostedNetwork in _tcpRelayServerHostedNetworkConnections)
                {
                    hostedNetwork.Value.Remove(connection);

                    if (hostedNetwork.Value.Count == 0)
                        emptyNetworks.Add(hostedNetwork.Key);
                }

                foreach (BinaryNumber channelId in emptyNetworks)
                    _tcpRelayServerHostedNetworkConnections.Remove(channelId);
            }
        }

        public Connection[] GetTcpRelayServerHostedNetworkConnections(BinaryNumber channelId)
        {
            lock (_tcpRelayServerHostedNetworkConnections)
            {
                if (_tcpRelayServerHostedNetworkConnections.ContainsKey(channelId))
                    return _tcpRelayServerHostedNetworkConnections[channelId].ToArray();
            }

            return new Connection[] { };
        }

        public void TcpRelayClientRegisterHostedNetwork(BinaryNumber channelId)
        {
            lock (_tcpRelayClientConnections)
            {
                foreach (Connection tcpRelayClientConnection in _tcpRelayClientConnections)
                {
                    try
                    {
                        tcpRelayClientConnection.TcpRelayRegisterHostedNetwork(channelId);
                    }
                    catch (Exception ex)
                    {
                        Debug.Write(this.GetType().Name, ex);
                    }
                }
            }
        }

        public void TcpRelayClientUnregisterHostedNetwork(BinaryNumber channelId)
        {
            lock (_tcpRelayClientConnections)
            {
                foreach (Connection tcpRelayClientConnection in _tcpRelayClientConnections)
                {
                    try
                    {
                        tcpRelayClientConnection.TcpRelayUnregisterHostedNetwork(channelId);
                    }
                    catch (Exception ex)
                    {
                        Debug.Write(this.GetType().Name, ex);
                    }
                }
            }
        }

        public EndPoint[] GetTcpRelayNodes()
        {
            lock (_tcpRelayClientConnections)
            {
                EndPoint[] tcpRelayNodes = new EndPoint[_tcpRelayClientConnections.Count];
                int i = 0;

                foreach (Connection connection in _tcpRelayClientConnections)
                    tcpRelayNodes[i++] = connection.RemotePeerEP;

                return tcpRelayNodes;
            }
        }

        #endregion

        #region internet connectivity

        private void IPv4ConnectivityCheck()
        {
            if (_upnpDeviceStatus == UPnPDeviceStatus.Identifying)
                _upnpDevice = null;

            EndPoint oldExternalEP = this.IPv4ExternalEndPoint;
            InternetConnectivityStatus oldInternetStatus = _ipv4InternetStatus;
            InternetConnectivityStatus newInternetStatus = InternetConnectivityStatus.Identifying;
            UPnPDeviceStatus newUPnPStatus;
            NetworkInfo defaultNetworkInfo;

            if (_node.EnableUPnP)
                newUPnPStatus = UPnPDeviceStatus.Identifying;
            else
                newUPnPStatus = UPnPDeviceStatus.Disabled;

            try
            {
                if (_node.Proxy != null)
                {
                    switch (_node.Proxy.Type)
                    {
                        case NetProxyType.Http:
                            newInternetStatus = InternetConnectivityStatus.HttpProxyInternetConnection;
                            break;

                        case NetProxyType.Socks5:
                            newInternetStatus = InternetConnectivityStatus.Socks5ProxyInternetConnection;
                            break;

                        default:
                            throw new NotSupportedException("Proxy type not supported.");
                    }

                    newUPnPStatus = UPnPDeviceStatus.Disabled;
                    return;
                }

                defaultNetworkInfo = NetUtilities.GetDefaultIPv4NetworkInfo();
                if (defaultNetworkInfo == null)
                {
                    //no internet available;
                    newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                    newUPnPStatus = UPnPDeviceStatus.Disabled;
                    return;
                }

                if (!NetUtilities.IsPrivateIP(defaultNetworkInfo.LocalIP))
                {
                    //public ip so, direct internet connection available
                    newInternetStatus = InternetConnectivityStatus.DirectInternetConnection;
                    newUPnPStatus = UPnPDeviceStatus.Disabled;
                    _ipv4LocalLiveIP = defaultNetworkInfo.LocalIP;
                    return;
                }
                else
                {
                    _ipv4LocalLiveIP = null;
                }

                if (newUPnPStatus == UPnPDeviceStatus.Disabled)
                {
                    newInternetStatus = InternetConnectivityStatus.NatOrFirewalledInternetConnection;
                    return;
                }

                //check for upnp device
                if ((_upnpDevice == null) || !_upnpDevice.LocalIP.Equals(defaultNetworkInfo.LocalIP) || (_upnpDeviceStatus == UPnPDeviceStatus.PortForwardingFailed))
                {
                    foreach (GatewayIPAddressInformation gateway in defaultNetworkInfo.Interface.GetIPProperties().GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            try
                            {
                                InternetGatewayDevice[] upnpDevices = InternetGatewayDevice.Discover(defaultNetworkInfo.LocalIP, gateway.Address);
                                if (upnpDevices.Length > 0)
                                    _upnpDevice = upnpDevices[0];
                                else
                                    _upnpDevice = null;
                            }
                            catch (Exception ex)
                            {
                                Debug.Write(this.GetType().Name, ex);
                            }

                            break;
                        }
                    }
                }

                if (_upnpDevice == null)
                {
                    newInternetStatus = InternetConnectivityStatus.NatOrFirewalledInternetConnection;
                    newUPnPStatus = UPnPDeviceStatus.DeviceNotFound;
                }
                else
                {
                    newInternetStatus = InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter;

                    //find external ip from router
                    try
                    {
                        _upnpExternalIP = _upnpDevice.GetExternalIPAddress();

                        if (_upnpExternalIP.ToString() == "0.0.0.0")
                        {
                            newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                            newUPnPStatus = UPnPDeviceStatus.Disabled;
                            return; //external ip not available so no internet connection available
                        }
                        else if (NetUtilities.IsPrivateIP(_upnpExternalIP))
                        {
                            newUPnPStatus = UPnPDeviceStatus.ExternalIpPrivate;
                            return; //no use of doing port forwarding for private upnp ip address
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.Write(this.GetType().Name, ex);

                        _upnpExternalIP = null;
                    }

                    //do upnp port forwarding for Bit Chat
                    if (_upnpDevice.ForwardPort(ProtocolType.Tcp, _localPort, new IPEndPoint(defaultNetworkInfo.LocalIP, _localPort), "Bit Chat", true))
                        newUPnPStatus = UPnPDeviceStatus.PortForwarded;
                    else
                        newUPnPStatus = UPnPDeviceStatus.PortForwardingFailed;
                }
            }
            catch (Exception ex)
            {
                Debug.Write(this.GetType().Name, ex);
            }
            finally
            {
                try
                {
                    //validate change in status by performing tests
                    if ((oldInternetStatus != newInternetStatus) || !Equals(oldExternalEP, this.IPv4ExternalEndPoint))
                    {
                        switch (newInternetStatus)
                        {
                            case InternetConnectivityStatus.NoInternetConnection:
                                _ipv4LocalLiveIP = null;
                                _upnpExternalIP = null;
                                _ipv4ConnectivityCheckExternalEP = null;
                                break;

                            case InternetConnectivityStatus.HttpProxyInternetConnection:
                            case InternetConnectivityStatus.Socks5ProxyInternetConnection:
                                if (!_node.Proxy.IsProxyAvailable())
                                    newInternetStatus = InternetConnectivityStatus.ProxyConnectionFailed;
                                else if (!WebUtilities.IsWebAccessible(null, _node.Proxy, WebClientExNetworkType.IPv4Only, 10000, false))
                                    newInternetStatus = InternetConnectivityStatus.NoProxyInternetConnection;

                                _ipv4LocalLiveIP = null;
                                _upnpExternalIP = null;
                                _ipv4ConnectivityCheckExternalEP = null;
                                break;

                            default:
                                if (WebUtilities.IsWebAccessible(null, null, WebClientExNetworkType.IPv4Only, 10000, false))
                                {
                                    switch (newInternetStatus)
                                    {
                                        case InternetConnectivityStatus.DirectInternetConnection:
                                            if (!IPv4DoWebCheckIncomingConnection(_localPort))
                                                _ipv4LocalLiveIP = null;

                                            break;

                                        case InternetConnectivityStatus.NatOrFirewalledInternetConnection:
                                            if (!IPv4DoWebCheckIncomingConnection(_localPort))
                                                _ipv4ConnectivityCheckExternalEP = null;

                                            break;

                                        case InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter:
                                            break;

                                        default:
                                            _ipv4LocalLiveIP = null;
                                            _upnpExternalIP = null;
                                            _ipv4ConnectivityCheckExternalEP = null;
                                            break;
                                    }
                                }
                                else
                                {
                                    newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                                    _ipv4LocalLiveIP = null;
                                    _upnpExternalIP = null;
                                    _ipv4ConnectivityCheckExternalEP = null;
                                }
                                break;
                        }
                    }

                    if ((newInternetStatus == InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter) && (_upnpDeviceStatus != newUPnPStatus) && (newUPnPStatus == UPnPDeviceStatus.PortForwarded))
                    {
                        if (_upnpDeviceStatus == UPnPDeviceStatus.PortForwardedNotAccessible)
                        {
                            newUPnPStatus = UPnPDeviceStatus.PortForwardedNotAccessible;
                        }
                        else if (!IPv4DoWebCheckIncomingConnection(_localPort))
                        {
                            newUPnPStatus = UPnPDeviceStatus.PortForwardedNotAccessible;
                        }
                    }

                    //update status
                    _ipv4InternetStatus = newInternetStatus;
                    _upnpDeviceStatus = newUPnPStatus;
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);
                }
            }
        }

        private void IPv6ConnectivityCheck()
        {
            EndPoint oldExternalEP = this.IPv6ExternalEndPoint;
            InternetConnectivityStatus oldInternetStatus = _ipv6InternetStatus;
            InternetConnectivityStatus newInternetStatus = InternetConnectivityStatus.Identifying;
            NetworkInfo defaultNetworkInfo;

            try
            {
                if (_node.Proxy != null)
                {
                    switch (_node.Proxy.Type)
                    {
                        case NetProxyType.Http:
                            newInternetStatus = InternetConnectivityStatus.HttpProxyInternetConnection;
                            break;

                        case NetProxyType.Socks5:
                            newInternetStatus = InternetConnectivityStatus.Socks5ProxyInternetConnection;
                            break;

                        default:
                            throw new NotSupportedException("Proxy type not supported.");
                    }
                }
                else
                {
                    defaultNetworkInfo = NetUtilities.GetDefaultIPv6NetworkInfo();
                    if (defaultNetworkInfo == null)
                    {
                        //no internet available;
                        newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                    }
                    else
                    {
                        if (NetUtilities.IsPublicIPv6(defaultNetworkInfo.LocalIP))
                        {
                            //public ip so, direct internet connection available
                            newInternetStatus = InternetConnectivityStatus.DirectInternetConnection;
                            _ipv6LocalLiveIP = defaultNetworkInfo.LocalIP;
                        }
                        else
                        {
                            newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                            _ipv6LocalLiveIP = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Write(this.GetType().Name, ex);
            }
            finally
            {
                try
                {
                    //validate change in status by performing tests
                    if ((oldInternetStatus != newInternetStatus) || !Equals(oldExternalEP, this.IPv6ExternalEndPoint))
                    {
                        switch (newInternetStatus)
                        {
                            case InternetConnectivityStatus.NoInternetConnection:
                                _ipv6LocalLiveIP = null;
                                break;

                            case InternetConnectivityStatus.HttpProxyInternetConnection:
                            case InternetConnectivityStatus.Socks5ProxyInternetConnection:
                                if (!_node.Proxy.IsProxyAvailable())
                                    newInternetStatus = InternetConnectivityStatus.ProxyConnectionFailed;
                                else if (!WebUtilities.IsWebAccessible(null, _node.Proxy, WebClientExNetworkType.IPv6Only, 10000, false))
                                    newInternetStatus = InternetConnectivityStatus.NoProxyInternetConnection;

                                _ipv6LocalLiveIP = null;
                                break;

                            default:
                                if (!WebUtilities.IsWebAccessible(null, null, WebClientExNetworkType.IPv6Only, 10000, false))
                                {
                                    newInternetStatus = InternetConnectivityStatus.NoInternetConnection;
                                    _ipv6LocalLiveIP = null;
                                }
                                break;
                        }
                    }

                    //update status
                    _ipv6InternetStatus = newInternetStatus;
                }
                catch (Exception ex)
                {
                    Debug.Write(this.GetType().Name, ex);
                }
            }
        }

        private bool IPv4DoWebCheckIncomingConnection(int externalPort)
        {
            bool _webCheckError = false;
            bool _webCheckSuccess = false;

            try
            {
                using (WebClientEx client = new WebClientEx())
                {
                    client.NetworkType = WebClientExNetworkType.IPv4Only;
                    client.Proxy = _node.Proxy;
                    client.QueryString.Add("port", externalPort.ToString());
                    client.Timeout = 10000;

                    using (BinaryReader bR = new BinaryReader(client.OpenRead(IPv4_CONNECTIVITY_CHECK_WEB_SERVICE)))
                    {
                        _webCheckError = false;
                        _webCheckSuccess = (bR.ReadByte() == 1);

                        if (_webCheckSuccess)
                            _ipv4ConnectivityCheckExternalEP = EndPointExtension.Parse(bR);
                        else
                            _ipv4ConnectivityCheckExternalEP = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Write(this.GetType().Name, ex);

                _webCheckError = true;
                _webCheckSuccess = false;
                _ipv4ConnectivityCheckExternalEP = null;
            }

            return _webCheckSuccess || _webCheckError;
        }

        public void ReCheckConnectivity()
        {
            if (_ipv4InternetStatus != InternetConnectivityStatus.Identifying)
            {
                _ipv4InternetStatus = InternetConnectivityStatus.Identifying;
                _upnpDeviceStatus = UPnPDeviceStatus.Identifying;
                _ipv6InternetStatus = InternetConnectivityStatus.Identifying;

                _connectivityCheckTimer.Change(1000, CONNECTIVITY_CHECK_TIMER_INTERVAL);
            }
        }

        #endregion

        #region properties

        public BinaryNumber LocalPeerId
        { get { return _localPeerId; } }

        public int LocalPort
        { get { return _localPort; } }

        public InternetConnectivityStatus IPv4InternetStatus
        { get { return _ipv4InternetStatus; } }

        public InternetConnectivityStatus IPv6InternetStatus
        { get { return _ipv6InternetStatus; } }

        public bool TorRunning
        { get { return _torController.IsRunning; } }

        public string TorHiddenServiceAddress
        {
            get
            {
                if (_torController.IsRunning && (_torHiddenServiceInfo != null))
                    return _torHiddenServiceInfo.ServiceId + ".onion";

                return "";
            }
        }

        public UPnPDeviceStatus UPnPStatus
        { get { return _upnpDeviceStatus; } }

        public IPAddress UPnPDeviceIP
        {
            get
            {
                if (_upnpDevice == null)
                    return null;
                else
                    return _upnpDevice.DeviceIP;
            }
        }

        public IPAddress UPnPExternalIP
        {
            get
            {
                if (_ipv4InternetStatus == InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter)
                    return _upnpExternalIP;
                else
                    return null;
            }
        }

        public EndPoint IPv4ExternalEndPoint
        {
            get
            {
                switch (_ipv4InternetStatus)
                {
                    case InternetConnectivityStatus.DirectInternetConnection:
                        if (_ipv4LocalLiveIP == null)
                            return null;
                        else
                            return new IPEndPoint(_ipv4LocalLiveIP, _localPort);

                    case InternetConnectivityStatus.NatInternetConnectionViaUPnPRouter:
                        switch (_upnpDeviceStatus)
                        {
                            case UPnPDeviceStatus.PortForwarded:
                                if (_upnpExternalIP == null)
                                    return null;
                                else
                                    return new IPEndPoint(_upnpExternalIP, _localPort);

                            default:
                                return null;
                        }

                    case InternetConnectivityStatus.Identifying:
                        return null;

                    default:
                        return _ipv4ConnectivityCheckExternalEP;
                }
            }
        }

        public EndPoint IPv6ExternalEndPoint
        {
            get
            {
                switch (_ipv6InternetStatus)
                {
                    case InternetConnectivityStatus.DirectInternetConnection:
                        if (_ipv6LocalLiveIP == null)
                            return null;
                        else
                            return new IPEndPoint(_ipv6LocalLiveIP, _localPort);

                    default:
                        return null;
                }
            }
        }

        public DhtManager DhtManager
        { get { return _dhtManager; } }

        public MeshNode Node
        { get { return _node; } }

        #endregion
    }
}
