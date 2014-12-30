﻿#region Copyright
/*
This file came from Managed Media Aggregation, You can always find the latest version @ https://net7mma.codeplex.com/
  
 Julius.Friedman@gmail.com / (SR. Software Engineer ASTI Transportation Inc. http://www.asti-trans.com)

Permission is hereby granted, free of charge, 
 * to any person obtaining a copy of this software and associated documentation files (the "Software"), 
 * to deal in the Software without restriction, 
 * including without limitation the rights to :
 * use, 
 * copy, 
 * modify, 
 * merge, 
 * publish, 
 * distribute, 
 * sublicense, 
 * and/or sell copies of the Software, 
 * and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
 * 
 * 
 * JuliusFriedman@gmail.com should be contacted for further details.

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
 * 
 * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, 
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, 
 * TORT OR OTHERWISE, 
 * ARISING FROM, 
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 * 
 * v//
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using Media.Rtcp;
using Media.Rtp;
using Media.Sdp;
using System.Threading;

namespace Media.Rtsp
{
    /// <summary>
    /// Implements RFC 2326
    /// http://www.ietf.org/rfc/rfc2326.txt
    /// Provides facilities for communication with an RtspServer to establish one or more Rtp Transport Channels.
    /// </summary>
    public class RtspClient : Common.BaseDisposable, Media.Common.ISocketReference
    {
        #region Nested Types

        public enum ClientProtocolType
        {
            Tcp = ProtocolType.Tcp,
            Reliable = Tcp,
            Udp = ProtocolType.Udp,
            Unreliable = Udp,
            Http = 2,
            Secure = 4
        }

        #endregion

        #region Fields

        ClientProtocolType m_RtspProtocol;

        ManualResetEventSlim m_InterleaveEvent = new ManualResetEventSlim(false);

        RtspMessage m_LastTransmitted;

        AuthenticationSchemes m_AuthenticationScheme;

        /// <summary>
        /// The location the media
        /// </summary>
        Uri m_Location;

        /// <summary>
        /// The buffer this client uses for all requests 4MB * 2
        /// </summary>
        Common.MemorySegment m_Buffer;

        /// <summary>
        /// The remote IPAddress to which the Location resolves via Dns
        /// </summary>
        IPAddress m_RemoteIP;

        /// <summary>
        /// The remote RtspEndPoint
        /// </summary>
        EndPoint m_RemoteRtsp;

        /// <summary>
        /// The socket used for Rtsp Communication
        /// </summary>
        Socket m_RtspSocket;

        /// <summary>
        /// The protcol in which Rtsp data will be transpored from the server
        /// </summary>
        ProtocolType m_RtpProtocol;

        /// <summary>
        /// The session description associated with the media at Location
        /// </summary>
        SessionDescription m_SessionDescription;

        TimeSpan m_RtspTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Need to seperate counters and other stuff
        /// </summary>
        int m_SentBytes, m_ReceivedBytes,
             m_RtspPort, m_CSeq,
            m_RetryCount = (int)Utility.MicrosecondsPerMillisecond;

        HashSet<RtspMethod> m_SupportedMethods = new HashSet<RtspMethod>();

        internal string m_UserAgent = "ASTI RTP Client", m_SessionId;//, m_TransportMode;

        internal RtpClient m_RtpClient;

        Timer m_KeepAliveTimer, m_ProtocolSwitchTimer;

        bool m_Playing = false;

        DateTime? m_StartedPlaying;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the value of the id in the Session header if it was seen in a response.
        /// </summary>
        public string SessionId { get { return m_SessionId; } }

        /// <summary>
        /// Any additional headers which may be required by the RtspClient.
        /// </summary>
        public readonly Dictionary<string, string> AdditionalHeaders = new Dictionary<string, string>();

        //Determine if Start and EndTime are worth having?

        /// <summary>
        /// If playing, the TimeSpan which represents the time this media started playing from.
        /// </summary>
        public TimeSpan? StartTime { get { return Client != null ? (TimeSpan?)Client.TransportContexts.Max(tc => tc.MediaStartTime) : null; } }

        /// <summary>
        /// If playing, the TimeSpan which represents the time the media will end.
        /// </summary>
        public TimeSpan? EndTime { get { return Client != null ? (TimeSpan?)Client.TransportContexts.Max(tc => tc.MediaEndTime) : null; } }

        //Remaining?

        /// <summary>
        /// If playing, indicates if the RtspClient is playing from a live source which means there is no absolute start or end time and seeking may not be supported.
        /// </summary>
        public bool LivePlay { get { return EndTime < TimeSpan.Zero; } }

        /// <summary>
        /// True if the RtspClient has received the Playing event, False if the RtspClient has received the Stopping event or otherwise such as the media has finished playing.
        /// </summary>
        //Should take into account if Paused? or Pausing everything should set m_Playing to false...
        public bool Playing { get { return Connected && (m_StartedPlaying.HasValue && LivePlay ? m_Playing : EndTime.HasValue ? (DateTime.UtcNow - m_StartedPlaying < EndTime.Value) : m_Playing); } }

        /// <summary>
        /// The DateTime in which the client started playing if playing, otherwise null.
        /// </summary>
        public DateTime? StartedPlaying { get { return m_StartedPlaying; } }

        /// <summary>
        /// The amount of time in seconds in which the RtspClient will switch protocols if no Packets have been recieved.
        /// </summary>
        public TimeSpan ProtocolSwitchTime { get; set; }

        /// <summary>
        /// The amount of time in seconds the KeepAlive request will be sent to the server after connected.
        /// If a GET_PARAMETER request is not supports OPTIONS will be sent instead.
        /// </summary>
        public TimeSpan KeepAliveTimeout { get { return m_RtspTimeout; }
            set
            {
                m_RtspTimeout = value;
                
                if (m_RtspTimeout <= TimeSpan.Zero)
                {
                    if (m_KeepAliveTimer != null) m_KeepAliveTimer.Dispose();
                    m_KeepAliveTimer = null;
                }

                //Update the timer period (taking into account the last time a request was sent) if there is a timer.
                if (m_KeepAliveTimer != null) m_KeepAliveTimer.Change(m_LastTransmitted != null && m_LastTransmitted.Transferred.HasValue ? (m_RtspTimeout - (DateTime.UtcNow - m_LastTransmitted.Created)) : m_RtspTimeout, Utility.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// The amount of times each RtspRequest will be sent if a response is not recieved in ReadTimeout
        /// </summary>
        public int RetryCount { get { return m_RetryCount; } set { m_RetryCount = value; if (m_RetryCount <= 0) m_RetryCount = 1; } }

        //The last RtspMessage transmittted by the RtspClient (Sent or Received)
        public RtspMessage LastTransmitted { get { return m_LastTransmitted; } }

        /// <summary>
        /// The ClientProtocolType the RtspClient is using Reliable (Tcp), Unreliable(Udp) or Http(Tcp)
        /// </summary>
        public ClientProtocolType RtspProtocol { get { return m_RtspProtocol; } }

        /// <summary>
        /// The ProtocolType the RtspClient will setup for underlying RtpClient.
        /// </summary>
        public ProtocolType RtpProtocol { get { return m_RtpProtocol; } }

        /// <summary>
        /// Gets or sets location to the Media on the Rtsp Server and updates Remote information and ClientProtocol if required by the change.
        /// If the RtspClient was listening then it will be stopped and started again
        /// </summary>
        public Uri Location
        {
            get { return m_Location; }
            set
            {
                try
                {
                    //If Different
                    if (m_Location != value)
                    {

                        bool wasPlaying = Playing;

                        if (wasPlaying) StopPlaying();

                        m_Location = value;

                        //(Should allow InterNetworkV6)
                        m_RemoteIP = System.Net.Dns.GetHostAddresses(m_Location.Host).Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).FirstOrDefault();

                        m_RtspPort = m_Location.Port;

                        //Validate ports, should throw?
                        if (m_RtspPort <= ushort.MinValue || m_RtspPort > ushort.MaxValue) m_RtspPort = 554;

                        //Determine protocol
                        if (m_Location.Scheme == RtspMessage.ReliableTransport) m_RtspProtocol = ClientProtocolType.Tcp;
                        else if (m_Location.Scheme == RtspMessage.UnreliableTransport) m_RtspProtocol = ClientProtocolType.Udp;
                        else m_RtspProtocol = ClientProtocolType.Http;

                        //Make a IPEndPoint 
                        m_RemoteRtsp = new IPEndPoint(m_RemoteIP, m_RtspPort);

                        if (wasPlaying) StartPlaying();
                    }
                }
                catch (Exception ex)
                {
                    Common.ExceptionExtensions.CreateAndRaiseException(this, "Could not resolve host from the given location. See InnerException.", ex);
                }
            }
        }

        /// <summary>
        /// Indicates if the RtspClient is connected to the remote host
        /// </summary>
        public bool Connected { get { return m_RtspSocket != null && m_RtspSocket.Connected; } }

        /// <summary>
        /// The network credential to utilize in RtspRequests
        /// </summary>
        public NetworkCredential Credential { get; set; }

        /// <summary>
        /// The type of AuthenticationScheme to utilize in RtspRequests
        /// </summary>
        public AuthenticationSchemes AuthenticationScheme { get { return m_AuthenticationScheme; } set { if (value == m_AuthenticationScheme) return; if (value != AuthenticationSchemes.Basic && value != AuthenticationSchemes.Digest && value != AuthenticationSchemes.None) throw new System.InvalidOperationException("Only None, Basic and Digest are supported"); else m_AuthenticationScheme = value; } }

        /// <summary>
        /// The amount of bytes sent by the RtspClient
        /// </summary>
        public int BytesSent { get { return m_SentBytes; } }

        /// <summary>
        /// The amount of bytes recieved by the RtspClient
        /// </summary>
        public int BytesRecieved { get { return m_ReceivedBytes; } }

        /// <summary>
        /// The current SequenceNumber of the RtspClient
        /// </summary>
        public int ClientSequenceNumber { get { return m_CSeq; } }

        /// <summary>
        /// Gets the SessionDescription provided by the server for the media at <see cref="Location"/>
        /// </summary>
        public SessionDescription SessionDescription { get { return m_SessionDescription; } internal set { m_SessionDescription = value; } }

        /// <summary>
        /// Gets the methods supported by the server recieved in the options request.
        /// </summary>
        public Rtsp.RtspMethod[] SupportedMethods { get { return m_SupportedMethods.ToArray(); } }

        /// <summary>
        /// The RtpClient associated with this RtspClient
        /// </summary>
        public RtpClient Client { get { return m_RtpClient; } }

        /// <summary>
        /// Gets or Sets the ReadTimeout of the underlying NetworkStream / Socket (msec)
        /// </summary>
        public int SocketReadTimeout { get { return m_RtspSocket.ReceiveTimeout; } set { m_RtspSocket.ReceiveTimeout = value; } }

        /// <summary>
        /// Gets or Sets the WriteTimeout of the underlying NetworkStream / Socket (msec)
        /// </summary>
        public int SocketWriteTimeout { get { return m_RtspSocket.SendTimeout; } set { m_RtspSocket.SendTimeout = value; } }

        /// <summary>
        /// The UserAgent sent with every RtspRequest
        /// </summary>
        public string UserAgent { get { return m_UserAgent; } set { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentNullException("UserAgent cannot consist of only null or whitespace."); m_UserAgent = value; } }

        #endregion

        #region Constructor

        static RtspClient()
        {
            if (!UriParser.IsKnownScheme(RtspMessage.ReliableTransport))
                UriParser.Register(new HttpStyleUriParser(), RtspMessage.ReliableTransport, 554);

            if (!UriParser.IsKnownScheme(RtspMessage.UnreliableTransport))
                UriParser.Register(new HttpStyleUriParser(), RtspMessage.UnreliableTransport, 555);

            if (!UriParser.IsKnownScheme(RtspMessage.SecureTransport))
                UriParser.Register(new HttpStyleUriParser(), RtspMessage.SecureTransport, 322);
        }
      
        /// <summary>
        /// Creates a RtspClient on a non standard Rtsp Port
        /// </summary>
        /// <param name="location">The absolute location of the media</param>
        /// <param name="rtspPort">The port to the RtspServer is listening on</param>
        /// <param name="rtpProtocolType">The type of protocol the underlying RtpClient will utilize and will not deviate from the protocol is no data is received, if null it will be determined from the location Scheme</param>
        public RtspClient(Uri location, ClientProtocolType? rtpProtocolType = null, int bufferSize = RtspMessage.MaximumLength)
        {
            if (!location.IsAbsoluteUri) throw new ArgumentException("Must be absolute", "location");
            if (!(location.Scheme == RtspMessage.ReliableTransport || location.Scheme == RtspMessage.UnreliableTransport || location.Scheme == System.Uri.UriSchemeHttp)) throw new ArgumentException("Uri Scheme must be rtsp or rtspu or http", "location");

            //Set the location and determines the m_RtspProtocol
            Location = location;

            //If the client has specified a Protcol to use then use it
            if (rtpProtocolType.HasValue)
            {
                //Determine if this means anything for Rtp Transport and set the field
                if (rtpProtocolType.Value == ClientProtocolType.Tcp || rtpProtocolType.Value == ClientProtocolType.Http)
                {
                    m_RtpProtocol = ProtocolType.Tcp;
                }
                else if(rtpProtocolType.Value == ClientProtocolType.Udp)
                {
                    m_RtpProtocol = ProtocolType.Udp;                    
                }
                else throw new ArgumentException("Must be Tcp or Udp.", "protocolType");
            }

            //Create the segment given the amount of memory required (Should be Mtu based)
            m_Buffer = new Common.MemorySegment(bufferSize);

            //Indicate how much time the client will switch from udp to tcp in if no data has been received once playing.
            ProtocolSwitchTime = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Creates a new RtspClient from the given uri in string form.
        /// E.g. 'rtsp://somehost/sometrack/
        /// </summary>
        /// <param name="location">The string which will be parsed to obtain the Location</param>
        /// <param name="rtpProtocolType">The type of protocol the underlying RtpClient will utilize, if null it will be determined from the location Scheme</param>
        /// <param name="bufferSize">The amount of bytes the client will use during message reception, Must be at least 4096 and if larger it will also be shared with the underlying RtpClient</param>
        public RtspClient(string location, ClientProtocolType? rtpProtocolType = null, int bufferSize = RtspMessage.MaximumLength)
            : this(new Uri(location), rtpProtocolType, bufferSize)
        {
        }

        //TODO A RtspClient should be able to be created from an existing socket and when the RtspClient is Disposed it should be able to leave that socket open
        //public RtspClient(string location, ClientProtocolType? rtpProtocolType, int bufferSize, Socket existing = null, bool leaveOpen = false)
        //    : this(new Uri(location), rtpProtocolType, bufferSize)
        //{
        //}


        ~RtspClient()
        {
            Dispose();
        }

        #endregion

        #region Events

        public delegate void RtspClientAction(RtspClient sender, object args);

        public delegate void RequestHandler(RtspClient sender, RtspMessage request);

        public delegate void ResponseHandler(RtspClient sender, RtspMessage request, RtspMessage response);

        public event RtspClientAction OnConnect;

        internal protected void OnConnected() { if (OnConnect != null) OnConnect(this, EventArgs.Empty); }

        public event RequestHandler OnRequest;

        internal protected void Requested(RtspMessage request) { if (OnRequest != null) OnRequest(this, request); }

        public event ResponseHandler OnResponse;

        internal protected void Received(RtspMessage request, RtspMessage response) { if (OnResponse != null) OnResponse(this, request, response); }

        public event RtspClientAction OnDisconnect;

        internal void OnDisconnected() { m_Playing = false; if (OnDisconnect != null) OnDisconnect(this, EventArgs.Empty); }

        public event RtspClientAction OnPlay;

        internal protected void OnPlaying(MediaDescription mediaDescription = null) { m_StartedPlaying = DateTime.UtcNow; m_Playing = true; if (OnPlay != null) OnPlay(this, mediaDescription); }

        public event RtspClientAction OnStop;

        internal protected void OnStopping(MediaDescription mediaDescription = null) { m_Playing = (mediaDescription != null); if (OnStop != null) OnStop(this, mediaDescription); }

        public event RtspClientAction OnPause;

        internal protected void OnPausing(MediaDescription mediaDescription = null) { if (OnPause != null) OnPause(this, mediaDescription); }

        #endregion

        #region Methods

        /// <summary>
        /// Handles Interleaved Data for the RtspClient by parsing the given memory for a valid RtspMessage.
        /// </summary>
        /// <param name="sender">The RtpClient instance which called this method</param>
        /// <param name="memory">The memory to parse</param>
        void ProcessInterleaveData(object sender, byte[] data, int offset, int length)
        {
            //Cache offset and count, leave a register for received data (should be calulated with length)
            int received = 0;

            //Must contain textual data to be an interleaved rtsp request.
            if (!Utility.FoundValidUniversalTextFormat(data, ref offset, ref length)) return;

            //Validate the data
            RtspMessage interleaved = new RtspMessage(data, offset, length);

            //Determine what to do with the interleaved message
            switch (interleaved.MessageType)
            {
                //Handle new requests or responses
                case RtspMessageType.Request: 
                case RtspMessageType.Response:
                    {
                        //If not playing an interleaved stream, Complete the message if not complete
                        if (!(Playing && m_RtpProtocol == ProtocolType.Tcp)) while (!interleaved.IsComplete) received += interleaved.CompleteFrom(m_RtspSocket, m_Buffer);

                        unchecked
                        {
                            //Update counters
                            m_ReceivedBytes += length + received;
                        }

                        //Disposes the last message.
                        m_LastTransmitted.Dispose();

                        m_LastTransmitted = null;

                        //Store the last message
                        m_LastTransmitted = interleaved;

                        goto default;
                    }
                case RtspMessageType.Invalid:
                    {
                        //Dispose the invalid message
                        interleaved.Dispose();

                        interleaved = null;

                        //If playing and interleaved stream AND the last transmitted message is NOT null and is NOT Complete then attempt to complete it
                        if (Playing && m_RtpProtocol == ProtocolType.Tcp && m_LastTransmitted != null && !m_LastTransmitted.IsComplete)
                        {
                            //Create a memory segment and complete the message as required from the buffer.
                            using (var memory = new Media.Common.MemorySegment(data, offset, length)) received += m_LastTransmitted.CompleteFrom(null, memory);
                        }

                        goto default;
                    }
                default:
                    {
                        //Indicate an interleaved data transfer has occured.
                        m_InterleaveEvent.Set();
                        return;
                    }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        /// <summary>
        /// Increments and returns the current SequenceNumber
        /// </summary>
        internal int NextClientSequenceNumber() { return ++m_CSeq; }

        //Should have end time also?
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void StartPlaying(TimeSpan? start = null, TimeSpan? end = null, Sdp.MediaType? mediaType = null)
        {

            //Try to connect if not already connected.
            if (!Connected) Connect();

            //Only use options and describe if not already playing
            if (!Playing)
            {
                //Send the options
                using (var options = SendOptions()) if (options == null || options.StatusCode != RtspStatusCode.OK) Common.ExceptionExtensions.CreateAndRaiseException(options, "Options Response was null or not OK. See Tag.");

                //Send describe
                using (var describe = SendDescribe()) if (describe == null || describe.StatusCode != RtspStatusCode.OK) Common.ExceptionExtensions.CreateAndRaiseException(describe, "Describe Response was null or not OK. See Tag.");
            }

            //Determine if any context was present or created.
            bool hasContext = false;

            //For each MediaDescription in the SessionDecscription
            foreach (Sdp.MediaDescription md in SessionDescription.MediaDescriptions)
            {
                //Don't setup unwanted streams
                if (mediaType.HasValue && md.MediaType != mediaType) continue;

                //If transport was already setup then see if the trasnport has a context for the media
                if (Client != null && Client.GetContextForMediaDescription(md) != null && !hasContext)
                {
                    //We have a context already, don't setup.
                    hasContext = true;

                    //Continue to the next MediaDescription
                    continue;
                }

                try
                {
                    //Send a setup
                    using (RtspMessage setup = SendSetup(md))
                    {
                        //If the setup was okay
                        if (setup != null && setup.StatusCode == RtspStatusCode.OK)
                        {
                            //Only setup tracks if response was OK
                            hasContext = true;
                        }
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }
            
            //If we have a play context then send the play request.
            if (hasContext) using (RtspMessage play = SendPlay(Location, start ?? StartTime, end ?? EndTime))
                {
                    //Should check if already playing because subscribers may attach events twice because of this.
                    //Shouldn't be too much of an issue because a Pause on everything should probably set Playing to false?
                    if (play == null || play != null && play.StatusCode == RtspStatusCode.OK) OnPlaying();
                }
            else throw new InvalidOperationException("Cannot Start Playing, No Tracks Setup.");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void StopPlaying()
        {
            try
            {
                if (!Playing) return;
                else Disconnect();
            }
            catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void Pause(MediaDescription mediaDescription = null, bool force = false)
        {
            //Don't pause if playing.
            if (!Playing) return;

            //Dont pause media which is not setup unless forced.
            if (!force && mediaDescription != null && Client.GetContextForMediaDescription(mediaDescription) == null) return;            

            //Send the pause.
            SendPause(mediaDescription, force);
        }

        /// <summary>
        /// Sends a SETUP if not already setup and then a PLAY for the given.
        /// If nothing is given this would be equivalent to calling <see cref="StartPlaying"/>
        /// </summary>
        /// <param name="mediaDescription"></param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void Play(MediaDescription mediaDescription = null, TimeSpan? startTime = null, TimeSpan? endTime = null, string rangeType = "npt", string rangeFormat = null)
        {
            bool playing = Playing;
            //If already playing and nothing was given then there is nothing to do
            if (playing && mediaDescription == null) return;
            else if (!playing) //We are not playing and nothing was given.
            {
                //Start playing everything
                StartPlaying();
                return;
            }
            
            //Dont setup media which is already setup.
            if (mediaDescription != null && Client.GetContextForMediaDescription(mediaDescription) == null) return;

            //setup the media description
            using (var setupResponse = SendSetup(mediaDescription))
            {
                //If the response was OKAY
                if (setupResponse != null && setupResponse.StatusCode == RtspStatusCode.OK)
                {
                    //Send the PLAY.
                    using (SendPlay(mediaDescription, startTime, endTime, rangeType, rangeFormat)) ;
                }
            }
            
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void Connect()
        {
            try
            {
                IAsyncResult connectResult = null;
                if (Connected) return;
                else if (m_RtspSocket == null)
                {
                    if (m_RtspProtocol == ClientProtocolType.Http || m_RtspProtocol == ClientProtocolType.Reliable)
                    {
                        m_RtspSocket = new Socket(m_RemoteIP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                        // Set option that allows socket to close gracefully without lingering.
                        //e.g. DON'T Linger on close if unsent data is present. (Should be moved to ISocketReference)
                        m_RtspSocket.DontLinger();
                        
                        m_RtspSocket.NoDelay = true;

                        connectResult = m_RtspSocket.BeginConnect(m_RemoteRtsp, new AsyncCallback((iar) =>
                        {
                            try
                            {
                                m_RtspSocket.EndConnect(iar);

                                SocketReadTimeout = SocketWriteTimeout = (int)m_RtspTimeout.TotalSeconds;

                                OnConnected();
                            }
                            catch { }
                        }), null);

                    }
                    else if (m_RtspProtocol == ClientProtocolType.Unreliable)
                    {
                        m_RtspSocket = new Socket(m_RemoteIP.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    }
                    else throw new NotSupportedException("The given ClientProtocolType is not supported.");
                }
               
                if(connectResult != null) while (!connectResult.IsCompleted) System.Threading.Thread.Yield();

            }
            catch
            {
                throw;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void Disconnect()
        {
            //Get rid of the timers

            if (m_ProtocolSwitchTimer != null)
            {
                m_ProtocolSwitchTimer.Dispose();
                m_ProtocolSwitchTimer = null;
            }

            if (m_KeepAliveTimer != null)
            {
                m_KeepAliveTimer.Dispose();
                m_KeepAliveTimer = null;
            }

            //Determine if we need to do anything
            if (Playing && !string.IsNullOrWhiteSpace(m_SessionId))
            {

                //Send the Teardown
                try
                {
                    using (SendTeardown()) ;
                }
                catch
                {
                    //We may not recieve a response if the socket is closed in a violatile fashion on the sending end
                    //And we realy don't care
                }
            }

            if (Client != null && Client.Connected) Client.Disconnect();
        
            if (m_RtspSocket != null)
            {
                if(m_RtspSocket.Connected) m_RtspSocket.Close();
                m_RtspSocket = null;
            }

            m_SessionId = null;
            
            m_SessionDescription = null;

            //Fire an event
            OnDisconnected();
        }

        #endregion

        #region Rtsp

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public RtspMessage SendRtspRequest(RtspMessage request)
        {
            try
            {
                if (!Connected)
                {
                    Connect();
                }

                //Add the user agent
                if (!request.ContainsHeader(RtspHeaders.UserAgent))
                {
                    request.SetHeader(RtspHeaders.UserAgent, m_UserAgent);
                }

                //If there not already an Authorization header and there is an AuthenticationScheme utilize the information in the Credential
                if (!request.ContainsHeader(RtspHeaders.Authorization) && m_AuthenticationScheme != AuthenticationSchemes.None && Credential != null)
                {
                    //Basic
                    if (m_AuthenticationScheme == AuthenticationSchemes.Basic)
                    {
                        request.SetHeader(RtspHeaders.Authorization, RtspHeaders.BasicAuthorizationHeader(request.Encoding, Credential));
                    }
                    else if (m_AuthenticationScheme == AuthenticationSchemes.Digest)
                    {
                        //Digest
                        request.SetHeader(RtspHeaders.Authorization,
                            RtspHeaders.DigestAuthorizationHeader(request.Encoding, request.Method, request.Location, Credential, null, null, null, null, null, false, null, request.Body));
                    }
                }

                //Add the content encoding header
                if (!request.ContainsHeader(RtspHeaders.ContentEncoding)) request.SetHeader(RtspHeaders.ContentEncoding, request.Encoding.EncodingName);

                //12.7 Blocksize
                /*
                 This request header field is sent from the client to the media server
                    asking the server for a particular media packet size. This packet
                    size does not include lower-layer headers such as IP, UDP, or RTP.
                    The server is free to use a blocksize which is lower than the one
                    requested. The server MAY truncate this packet size to the closest
                    multiple of the minimum, media-specific block size, or override it
                    with the media-specific size if necessary. The block size MUST be a
                    positive decimal number, measured in octets. The server only returns
                    an error (416) if the value is syntactically invalid.
                 */

                //This is important if the server can support it, it will ensure that packets can fit in the buffer.
                //It also tells the server what our buffer size is so if they wanted they could intentionally make packets which allowed only a certain amount of bytes remaining in the buffer....
                if (!request.ContainsHeader(RtspHeaders.Blocksize)) request.SetHeader(RtspHeaders.Blocksize, m_Buffer.Count.ToString());

                ///Use the sessionId if present
                if (m_SessionId != null) request.SetHeader(RtspHeaders.Session, m_SessionId);

                //Get the next Sequence Number and set it in the request. (If not already present)
                if (!request.ContainsHeader(RtspHeaders.CSeq)) request.CSeq = NextClientSequenceNumber();

                //Use any additional headers if given
                if (AdditionalHeaders.Count > 0) foreach (var additional in AdditionalHeaders) request.AppendOrSetHeader(additional.Key, additional.Value);

                //Get the bytes of the request
                byte[] buffer = m_RtspProtocol == ClientProtocolType.Http ? RtspMessage.ToHttpBytes(request) : request.ToBytes();


                int attempt = 0, //The attempt counter itself
                    sent = 0, received = 0, //counter for sending and receiving locally
                    offset = m_Buffer.Offset, length = buffer.Length;

                //The error which will be ignored incase non-blocking sockets are being used.
                SocketError error = SocketError.Success;

                #region Reference

                /*
                    RFC2326 - http://tools.ietf.org/html/rfc2326 [Page 18]
                 
                    RTSP is a text-based protocol and uses the ISO 10646 character set in
                    UTF-8 encoding (RFC 2279 [21]). Lines are terminated by CRLF, but
                    receivers should be prepared to also interpret CR and LF by
                    themselves as line terminators.
                    
                See also RFC 2326 - http://tools.ietf.org/html/rfc2326 [Page 28]               
                */

                #endregion

                unchecked
                {
                    sent += m_RtspSocket.Send(buffer, sent, length - sent, SocketFlags.None, out error);

                    //If we could not send the message indicate so
                    if (sent < length || error != SocketError.Success) return null;

                    //Set the time when the message was transferred.
                    request.Transferred = DateTime.UtcNow;

                    //Fire the event
                    Requested(m_LastTransmitted = request);

                    //Increment our byte counters for Rtsp
                    m_SentBytes += sent;

                    //Attempt to receive
                    attempt = 0;

                    //Set the block
                    m_InterleaveEvent.Reset();

                    //Jump ahead to wait
                    if (Playing && m_RtpProtocol == ProtocolType.Tcp) goto Wait;
                }

                //Receive some data
            Receive:
                received = m_RtspSocket.Receive(m_Buffer.Array, offset, m_Buffer.Count, SocketFlags.None, out error);

                //Try again if allowed
                if (error == SocketError.TryAgain) goto Receive;


                //Handle the connection reset or connection aborted.
                if (error != SocketError.Success) return null;

                //If anything was received
                if (received > 0)
                {
                    //TODO
                    //RtspClient.TransportContext must handle the reception because it must strip away the RTSP and process only the interleaved data in a frame.
                    //Right now just pass it to the RtpClient.
                    if (m_RtpClient != null && m_Buffer.Array[offset] == Media.Rtp.RtpClient.BigEndianFrameControl)
                    {
                        //connect the rtp client
                        m_RtpClient.Connect();

                        //Adjust for non rtsp data
                        received -= m_RtpClient.ProcessFrameData(m_Buffer.Array, offset, received, m_RtspSocket);

                        //Handle when we received a lot of data and no response was found.
                        if (received < 0) received = 0;
                    }
                    else ProcessInterleaveData(this, m_Buffer.Array, offset, received);
                }
                else if (!Playing) goto Receive;

            Wait: //Wait for the response unless playing or tearing down. (Some implementations do not send a play response especially in interleaved mode, others don't send a tear down response), the latter one can make quickly ending the client take a long time which is not desirable.
                if (request.Method != RtspMethod.UNKNOWN)// && request.Method != RtspMethod.TEARDOWN)
                {
                    //We have not yet received a COMPLETE response, wait on the interleave event for the amount of time specified, if signaled a response was created
                    while ((m_LastTransmitted == null || m_LastTransmitted.MessageType != RtspMessageType.Response || !m_LastTransmitted.IsComplete) && ++attempt <= m_RetryCount)
                    {
                        //Wait a small amount of time for the response because the cancellation token was not used...
                        if (m_InterleaveEvent.IsSet || m_InterleaveEvent.Wait((int)((m_RtspTimeout.TotalMilliseconds + 1) / m_RetryCount))) continue;
                        else if (m_RtspSocket.ProtocolType == ProtocolType.Udp) goto Receive;
                    }
                }

                unchecked
                {

                    //Update counters for any data received.
                    m_ReceivedBytes += received;
                }

                //If we were not authorized and we did not give a nonce and there was an WWWAuthenticate header given then we will attempt to authenticate using the information in the header
                //(Note for Vivontek you can still bypass the Auth anyway :)
                //http://www.coresecurity.com/advisories/vivotek-ip-cameras-rtsp-authentication-bypass
                if (m_LastTransmitted != null && m_LastTransmitted.MessageType == RtspMessageType.Response && m_LastTransmitted.StatusCode == RtspStatusCode.Unauthorized && m_LastTransmitted.ContainsHeader(RtspHeaders.WWWAuthenticate) && Credential != null)
                {
                    //http://tools.ietf.org/html/rfc2617
                    //3.2.1 The WWW-Authenticate Response Header
                    //Example
                    //WWW-Authenticate: Digest realm="GeoVision", nonce="b923b84614fc11c78c712fb0e88bc525"\r\n

                    string authenticateHeader = m_LastTransmitted[RtspHeaders.WWWAuthenticate];

                    string[] baseParts = authenticateHeader.Split(RtspHeaders.SpaceSplit, StringSplitOptions.RemoveEmptyEntries);

                    if (string.Compare(baseParts[0].Trim(), "basic", true) == 0)
                    {
                        AuthenticationScheme = AuthenticationSchemes.Basic;

                        //Get the realm if we don't have one.
                        if (Credential.Domain == null)
                        {
                            string realm = baseParts.Where(p => p.StartsWith("realm", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(realm))
                            {
                                realm = realm.Substring(6).Replace("\"", string.Empty).Replace("\'", string.Empty);
                                Credential.Domain = realm;
                            }
                        }

                        request.SetHeader(RtspHeaders.Authorization, RtspHeaders.BasicAuthorizationHeader(request.Encoding, Credential));

                        //Recurse the call with the info from then authenticate header
                        return SendRtspRequest(request);

                    }
                    else if (string.Compare(baseParts[0].Trim(), "digest", true) == 0)
                    {
                        AuthenticationScheme = AuthenticationSchemes.Digest;

                        string algorithm = "MD5";

                        string username = baseParts.Where(p => p.StartsWith("username", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(username)) username = username.Substring(9);
                        else username = Credential.UserName; //use the username of the credential.

                        string realm;

                        //Get the realm if we don't have one.
                        if (Credential.Domain == null)
                        {
                            realm = baseParts.Where(p => p.StartsWith("realm", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(realm))
                            {
                                realm = realm.Substring(6).Replace("\"", string.Empty).Replace("\'", string.Empty);
                                Credential.Domain = realm;
                            }
                        }
                        else realm = Credential.Domain; //Use the realm of the Credential.

                        string nc = baseParts.Where(p => p.StartsWith("nc", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(nc)) nc = realm.Substring(3);

                        string nonce = baseParts.Where(p => p.StartsWith("nonce", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(nonce)) nonce = nonce.Substring(6).Replace("\"", string.Empty).Replace("\'", string.Empty);

                        string cnonce = baseParts.Where(p => p.StartsWith("cnonce", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();//parts.Where(p => string.Compare("cnonce", p, true) == 0).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(cnonce)) cnonce = cnonce.Substring(7).Replace("\"", string.Empty).Replace("\'", string.Empty);//cnonce = cnonce.Replace("cnonce=", string.Empty);

                        string uri = baseParts.Where(p => p.StartsWith("uri", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault(); //parts.Where(p => p.Contains("uri")).FirstOrDefault();
                        bool rfc2069 = !string.IsNullOrWhiteSpace(uri) && !uri.Contains(RtspHeaders.HyphenSign);

                        if (!string.IsNullOrWhiteSpace(uri))
                        {
                            if (rfc2069) uri = uri.Substring(4);
                            else uri = uri.Substring(11);
                        }

                        string qop = baseParts.Where(p => string.Compare("qop", p, true) == 0).FirstOrDefault();

                        if (!string.IsNullOrWhiteSpace(qop))
                        {
                            qop = qop.Replace("qop=", string.Empty);
                            if (nc != null) nc = nc.Substring(3);
                        }

                        string opaque = baseParts.Where(p => p.StartsWith("opaque", StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(opaque)) opaque = opaque.Substring(7);

                        request.SetHeader(RtspHeaders.Authorization, RtspHeaders.DigestAuthorizationHeader(request.Encoding, request.Method, request.Location, Credential, qop, nc, nonce, cnonce, opaque, rfc2069, algorithm, request.Body));

                        //Recurse the call with the info from then authenticate header
                        return SendRtspRequest(request);
                    }
                }

                //Check for the response.
                if (m_LastTransmitted != null && m_LastTransmitted.MessageType == RtspMessageType.Response)
                {
                    //TODO
                    //REDIRECT (Handle loops)
                    //if(m_LastTransmitted.StatusCode == RtspStatusCode.MovedPermanently)

                    switch (m_LastTransmitted.StatusCode)
                    {
                        case RtspStatusCode.NotImplemented: m_SupportedMethods.Remove(m_LastTransmitted.Method); break;
                        case RtspStatusCode.MethodNotValidInThisState: if (m_LastTransmitted.ContainsHeader(RtspHeaders.Allow)) SwitchProtocols(); break;
                        default: break;
                    }

                    //Check for a SessionId and Timeout if we don't already have one unless this is a GET_PARAMETER or TEARDOWN
                    if (request.Method != RtspMethod.TEARDOWN && string.IsNullOrWhiteSpace(m_SessionId) && m_LastTransmitted.ContainsHeader(RtspHeaders.Session))
                    {
                        string sessionHeader = m_LastTransmitted[RtspHeaders.Session];

                        //If there is a session header it may contain the option timeout
                        if (!string.IsNullOrWhiteSpace(sessionHeader))
                        {
                            //Check for session and timeout

                            //Get the values
                            string[] temp = sessionHeader.Split(RtspHeaders.SemiColon);

                            //Check for any values
                            if (temp.Length > 0)
                            {
                                //Get the SessionId if present
                                m_SessionId = temp[0].Trim();

                                //Check for a timeout
                                if (temp.Length > 1)
                                {
                                    int timeoutStart = 1 + temp[1].IndexOf(Media.Sdp.SessionDescription.EqualsSign);
                                    if (timeoutStart >= 0 && int.TryParse(temp[1].Substring(timeoutStart), out timeoutStart))
                                    {
                                        //Should already be set...
                                        if (timeoutStart <= 0)
                                        {
                                            m_RtspTimeout = TimeSpan.FromSeconds(60);//Default
                                        }
                                        else
                                        {
                                            m_RtspTimeout = TimeSpan.FromSeconds(timeoutStart);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                //The timeout was not present
                                m_SessionId = sessionHeader.Trim();

                                m_RtspTimeout = TimeSpan.FromSeconds(60);//Default
                            }
                        }
                    }

                    //Raise an event
                    Received(request, m_LastTransmitted);
                }

                //Return the result
                return m_LastTransmitted;
            }
            catch
            {
                throw;
            }
        }

        /// <summary>
        /// Sends the Rtsp OPTIONS request
        /// </summary>
        /// <param name="useStar">The OPTIONS * request will be sent rather then one with the <see cref="RtspClient.Location"/></param>
        /// <returns>The <see cref="RtspMessage"/> as a response to the request</returns>
        public RtspMessage SendOptions(bool useStar = false)
        {
            using(var options = new RtspMessage(RtspMessageType.Request)
            {
                Method = RtspMethod.OPTIONS,
                Location = useStar ? null : Location
            })
            {
                RtspMessage response = SendRtspRequest(options);

                if (response == null || response.StatusCode != RtspStatusCode.OK) Common.ExceptionExtensions.CreateAndRaiseException(this, "Unable to get options");
                else
                {
                    m_SupportedMethods.Clear();

                    string publicMethods = response[RtspHeaders.Public];

                    if (string.IsNullOrWhiteSpace(publicMethods)) return response;

                    foreach (string method in publicMethods.Split(RtspHeaders.Comma))
                    {
                        m_SupportedMethods.Add((RtspMethod)Enum.Parse(typeof(RtspMethod), method.Trim(), true));
                    }

                    //Should also store Supported:
                }

                return response;
            }
        }

        /// <summary>
        /// Assigns the SessionDescription returned from the server
        /// </summary>
        /// <returns></returns>
        public RtspMessage SendDescribe()
        {

            RtspMessage response = null;

            try
            {
                using (RtspMessage describe = new RtspMessage(RtspMessageType.Request)
                {
                    Method = RtspMethod.DESCRIBE,
                    Location = Location
                })
                {
                    #region Reference

                    // The DESCRIBE method retrieves the description of a presentation or
                    // media object identified by the request URL from a server. It may use
                    // the Accept header to specify the description formats that the client
                    // understands. The server responds with a description of the requested
                    // resource. The DESCRIBE reply-response pair constitutes the media
                    // initialization phase of RTSP.

                    #endregion

                    describe.SetHeader(RtspHeaders.Accept, Sdp.SessionDescription.MimeType);

                    response = SendRtspRequest(describe);

                    if (response == null) Common.ExceptionExtensions.CreateAndRaiseException(describe, "Unable to describe media, no response to DESCRIBE request. The request is in the Tag property.");
                    else if (response.StatusCode != RtspStatusCode.OK)
                    {
                        Common.ExceptionExtensions.CreateAndRaiseException(response.StatusCode, "Unable to describe media. The StatusCode is in the Tag property.");
                    }
                    else if (response.GetHeader(RtspHeaders.ContentType).Trim() != Sdp.SessionDescription.MimeType || string.IsNullOrWhiteSpace(response.Body))
                    {
                        Common.ExceptionExtensions.CreateAndRaiseException(this, "Unable to describe media, Missing Session Description");
                    }

                    m_SessionDescription = new Sdp.SessionDescription(response.Body);
                }
            }
            catch (Common.Exception<RtspClient>)
            {
                throw;
            }
            catch (Common.Exception<SessionDescription>)
            {
                Common.ExceptionExtensions.CreateAndRaiseException(this, "Unable to describe media, Session Description Exception Occured.");
            }
            catch(Exception ex) { Common.ExceptionExtensions.CreateAndRaiseException(this, "An error occured", ex); }


            return response;
        }

        public RtspMessage SendTeardown(MediaDescription mediaDescription = null)
        {
            RtspMessage response = null;
            try
            {
                //If there is a client then stop the flow of this media now with RTP
                if (m_RtpClient != null)
                {
                    //Send a goodbye for all contexts if the mediaDescription was not given
                    if (mediaDescription == null) m_RtpClient.SendGoodbyes();
                    else//Find the context for the description
                    {
                        RtpClient.TransportContext context = m_RtpClient.GetContextForMediaDescription(mediaDescription);
                        //If context was determined then send a goodbye
                        if (context != null)
                        {
                            m_RtpClient.SendGoodbye(context);
                            context = null;
                        }
                    }
                }

                //The media is stopping now.
                OnStopping(mediaDescription);

                //Return the result of the Teardown
                using (var teardown = new RtspMessage(RtspMessageType.Request)
                {
                    Method = RtspMethod.TEARDOWN,
                    Location = mediaDescription != null ? mediaDescription.GetAbsoluteControlUri(Location) : Location
                })
                {
                    return SendRtspRequest(teardown);
                }
                
            }
            catch (Common.Exception<RtspClient>)
            {
                return response;
            }
            catch
            {
                throw;
            }
        }

        public RtspMessage SendSetup(MediaDescription mediaDescription)
        {
            if (mediaDescription == null) throw new ArgumentNullException("mediaDescription");

            //Send the setup
            return SendSetup(mediaDescription.GetAbsoluteControlUri(Location), mediaDescription);
        }

        //Remove unicast...
        internal RtspMessage SendSetup(Uri location, MediaDescription mediaDescription, bool unicast = true)//False to use manually set protocol
        {
            if (!SupportedMethods.Contains(RtspMethod.SETUP)) throw new InvalidOperationException("Server does not support SETUP.");

            if (location == null) throw new ArgumentNullException("location");

            if (mediaDescription == null) throw new ArgumentNullException("mediaDescription");

            try
            {
                //TODO Shouldn't create a RtcpSocket when mediaDescription has Rtcp Disabled.

                //Should either create context NOW or use these sockets in the created context.

                //Create sockets to reserve the ports we think we will need.
                Socket rtpTemp = null, rtcpTemp = null;

                using(RtspMessage setup = new RtspMessage(RtspMessageType.Request)
                {
                    Method = RtspMethod.SETUP,
                    Location = location ?? Location
                })
                {
                    //Todo Determine if Unicast or Multicast from mediaDescription ....?
                    string connectionType = unicast ? "unicast;" : "multicast";

                    //Todo, could send ssrc here to let server know the ssrc we have early...

                    // TCP was specified or the MediaDescription specified we need to use Tcp as specified in RFC4571
                    if (m_RtpProtocol == ProtocolType.Tcp)
                    {
                        //If there is already a RtpClient with at-least 1 TransportContext
                        if (m_RtpClient != null && m_RtpClient.GetTransportContexts().Any())
                        {
                            RtpClient.TransportContext lastContext = m_RtpClient.GetTransportContexts().Last();
                            setup.SetHeader(RtspHeaders.Transport, RtspHeaders.TransportHeader(RtpClient.RtpAvpProfileIdentifier + "/TCP", null, null, null, null, null, null, true, false, null, true, (byte)(lastContext.DataChannel + 2), (byte)(lastContext.ControlChannel + 2)));
                        }
                        else
                        {
                            setup.SetHeader(RtspHeaders.Transport, RtspHeaders.TransportHeader(RtpClient.RtpAvpProfileIdentifier + "/TCP", null, null, null, null, null, null, true, false, null, true, (byte)(0), (byte)(1)));
                        }
                    }
                    else if (string.Compare(mediaDescription.MediaProtocol, RtpClient.RtpAvpProfileIdentifier, true) == 0) // We need to find an open Udp Port
                    {
                        //Is probably Ip, set to Udp
                        m_RtpProtocol = ProtocolType.Udp;

                        //Might want to reserver this port now by making a socket...

                        //Could send 0 to have server pick port?                        
                        int openPort = Utility.FindOpenPort(ProtocolType.Udp, 10000, true); //Should allow this to be given or set as a property MinimumUdpPort, MaximumUdpPort

                        rtpTemp = Utility.ReservePort(SocketType.Dgram, ProtocolType.Udp, ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address, openPort);
                        rtcpTemp = Utility.ReservePort(SocketType.Dgram, ProtocolType.Udp, ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address, openPort + 1);

                        if (openPort == -1) Common.ExceptionExtensions.CreateAndRaiseException(this, "Could not find open Udp Port");
                        //else if (MaximumUdp.HasValue && openPort > MaximumUdp)
                        //{
                        //    Common.ExceptionExtensions.CreateAndRaiseException(this, "Found Udp Port > MaximumUdp. Found: " + openPort);
                        //}    
                        setup.SetHeader(RtspHeaders.Transport, RtspHeaders.TransportHeader(RtpClient.RtpAvpProfileIdentifier, null, null, openPort, openPort + 1, null, null, true, false, null, false, 0, 0));
                    }

                    //Get the response for the setup
                    RtspMessage response = SendRtspRequest(setup);

                    if (response == null || response.MessageType != RtspMessageType.Response) Common.ExceptionExtensions.CreateAndRaiseException(this, "No response to SETUP");
                    //Response not OK
                    else if (response.StatusCode != RtspStatusCode.OK)
                    {
                        //Transport requested not valid
                        if (response.StatusCode == RtspStatusCode.UnsupportedTransport && m_RtpProtocol != ProtocolType.Tcp)
                        {
                            goto SetupTcp;
                        }
                        else if (response.StatusCode == RtspStatusCode.SessionNotFound)
                        {
                            using (var teardown = SendTeardown()) return SendSetup(location, mediaDescription);
                        }
                        else
                        {
                            Common.ExceptionExtensions.CreateAndRaiseException(response.StatusCode, "Unable to setup media. The status code is in the Tag property.");
                        }
                    }

                    string transportHeader = response[RtspHeaders.Transport];

                    //Values in the header we need
                    int clientRtpPort = -1, clientRtcpPort = -1, serverRtpPort = -1, serverRtcpPort = -1, ssrc = 0;

                    //Cache this to prevent having to go to get it every time down the line
                    IPAddress sourceIp = IPAddress.Any;

                    string mode;

                    bool multicast = false, interleaved = false;

                    byte dataChannel = 0, controlChannel = 1;

                    //Todo if ContainsHeader(RtpInfo)
                    //use ParseRtpInfoHeader to obtain information

                    //We need a valid TransportHeader with RTP
                    if (string.IsNullOrEmpty(transportHeader) || !transportHeader.Contains("RTP")
                        ||
                        !RtspHeaders.TryParseTransportHeader(transportHeader,
                        out ssrc, out sourceIp, out serverRtpPort, out serverRtcpPort, out clientRtpPort, out clientRtcpPort,
                        out interleaved, out dataChannel, out controlChannel, out mode, out unicast, out multicast))
                        Common.ExceptionExtensions.CreateAndRaiseException(this, "Cannot setup media, Invalid Transport Header in Rtsp Response: " + transportHeader);

                    //Just incase the source was not given
                    if (sourceIp == IPAddress.Any) sourceIp = ((IPEndPoint)m_RtspSocket.RemoteEndPoint).Address;

                    //If interleaved was present in the response then use a RTP/AVP/TCP Transport
                    if (interleaved)
                    {
                        //Create the context (determine if the session rangeLine may also be given here, if it gets parsed once it doesn't need to be parsed again)
                        RtpClient.TransportContext created = RtpClient.TransportContext.FromMediaDescription(SessionDescription, dataChannel, controlChannel, mediaDescription, true, ssrc, ssrc != 0 ? 0 : 2);

                        //If there is not a client
                        if (m_RtpClient == null)
                        {
                            //Create a Duplexed reciever using the RtspSocket
                            m_RtpClient = new RtpClient(m_Buffer);

                            //Attach an event for interleaved data
                            m_RtpClient.InterleavedData += ProcessInterleaveData;
                        }
                        else if (m_RtpProtocol != ProtocolType.Tcp) goto SetupTcp;

                        //and initialize the client from the RtspSocket depdning on if the source is on the same server as the existing connection
                        if (IPAddress.Equals(sourceIp, ((IPEndPoint)m_RemoteRtsp).Address)) created.Initialize(m_RtspSocket);
                        else created.Initialize(Utility.GetFirstIPAddress(sourceIp.AddressFamily), sourceIp, serverRtpPort); //Might have to come from source string?

                        //Todo
                        //Care should be taken that the SDP is not directing us to connect to some unknown resource....

                        //try to add the TransportContext
                        m_RtpClient.Add(created);
                    }
                    else
                    {
                        //The server may response with the port used for the request which indicates that TCP should be used?
                        if (serverRtpPort == location.Port) goto SetupTcp;

                        //If we need to make a client then do so
                        if (m_RtpClient == null)
                        {
                            if (m_RtpProtocol == ProtocolType.Udp)
                            {
                                //Create a Udp Reciever
                                m_RtpClient = new RtpClient(m_Buffer);

                                //Attach an event for interleaved data
                                //m_RtpClient.InterleavedData += ProcessInterleaveData;
                            }
                            else Media.Common.ExceptionExtensions.CreateAndRaiseException<RtspClient>(this, "RtpProtocol is not Udp and Server required Udp Transport.");
                        }

                        RtpClient.TransportContext created;

                        if (!m_RtpClient.GetTransportContexts().Any())
                        {
                            created = RtpClient.TransportContext.FromMediaDescription(SessionDescription, 0, 1, mediaDescription, true, ssrc, ssrc != 0 ? 0 : 2);
                        }
                        else
                        {
                            RtpClient.TransportContext lastContext = m_RtpClient.GetTransportContexts().LastOrDefault();

                            if (lastContext != null) created = RtpClient.TransportContext.FromMediaDescription(SessionDescription, (byte)(lastContext.DataChannel + 2), (byte)(lastContext.ControlChannel + 2), mediaDescription, true, ssrc, ssrc != 0 ? 0 : 2);
                            else created = RtpClient.TransportContext.FromMediaDescription(SessionDescription, (byte)dataChannel, (byte)controlChannel, mediaDescription, true, ssrc, ssrc != 0 ? 0 : 2);
                        }

                        created.Initialize(((IPEndPoint)m_RtspSocket.LocalEndPoint).Address, sourceIp, clientRtpPort, clientRtcpPort, serverRtpPort, serverRtcpPort);

                        //rtpTemp.Connect(sourceIp, serverRtpPort);
                        //rtcpTemp.Connect(sourceIp, serverRtcpPort);
                        //created.Initialize(rtpTemp, rtcpTemp);

                        //No longer need the temporary sockets
                        rtpTemp.Dispose();
                        rtcpTemp.Dispose();

                        m_RtpClient.Add(created);
                    }

                    //Setup Complete
                    return response;
                }
            }
            catch (Exception ex)
            {
                Common.ExceptionExtensions.CreateAndRaiseException(this, "Unable to setup media. See InnerException", ex);
            }

        //Setup for Interleaved
        SetupTcp:
            {
                if (m_RtpClient != null && m_RtpClient.GetTransportContexts().Count() > 0)
                {
                    //Disconnect existing sockets
                    foreach (var tc in m_RtpClient.GetTransportContexts()) tc.DisconnectSockets();

                    //Clear existing transportChannels
                    m_RtpClient.TransportContexts.Clear();
                }
                
                m_RtpProtocol = ProtocolType.Tcp;

                //Recurse call to ensure propper setup
                return SendSetup(location, mediaDescription);
            }
        }

        protected virtual void SwitchProtocols(object state = null)
        {
            //If there is no socket or the protocol was forced return`
            if (!Disposed && Playing && Client.GetTransportContexts().All(tc => tc.IsRtpEnabled && tc.RtpPacketsReceived == 0))
            {
                try
                {
                    //If the client has not recieved any bytes and we have not already switched to Tcp
                    if (m_RtpProtocol != ProtocolType.Tcp)
                    {
                        //Ensure Tcp protocol
                        m_RtpProtocol = ProtocolType.Tcp;
                    }
                    else if (m_RtpProtocol != ProtocolType.Udp)
                    {

                        //Ensure Tcp protocol
                        m_RtpProtocol = ProtocolType.Udp;
                    }
                    else 
                    {
                        //Ensure IP protocol
                        m_RtpProtocol = ProtocolType.IP;
                    }

                    //Stop all playback
                    StopPlaying();

                    //Start again
                    StartPlaying();
                }
                catch { return; }
            }
            else if(m_ProtocolSwitchTimer != null)
            {
                m_ProtocolSwitchTimer.Dispose();
                m_ProtocolSwitchTimer = null;
            }

        }

        public RtspMessage SendPlay(MediaDescription mediaDescription, TimeSpan? startTime = null, TimeSpan? endTime = null, string rangeType = "npt", string rangeFormat = null)
        {
            if (mediaDescription == null) throw new ArgumentNullException("mediaDescription");

            var context = Client.GetContextForMediaDescription(mediaDescription);

            if (context == null) throw new InvalidOperationException("The given mediaDescription has not been SETUP.");

            //Raise an event now.
            OnPlaying(mediaDescription);

            //Send the play request
            return SendPlay(mediaDescription.GetAbsoluteControlUri(Location), startTime ?? context.MediaStartTime, endTime ?? context.MediaEndTime, rangeType, rangeFormat);
        }

        public RtspMessage SendPlay(Uri location = null, TimeSpan? startTime = null, TimeSpan? endTime = null, string rangeType = "npt", string rangeFormat = null, bool force = false)
        {
            if (!SupportedMethods.Contains(RtspMethod.PLAY) && !force) throw new InvalidOperationException("Server does not support PLAY.");

            try
            {
                using(RtspMessage play = new RtspMessage(RtspMessageType.Request)
                {
                    Method = RtspMethod.PLAY,
                    Location = location ?? Location
                })
                {
                    /*
                      A PLAY request without a Range header is legal. It starts playing a
                        stream from the beginning unless the stream has been paused. If a
                        stream has been paused via PAUSE, stream delivery resumes at the
                        pause point. If a stream is playing, such a PLAY request causes no
                        further action and can be used by the client to test server liveness.
                     */

                    //Maybe should not be set if no start or end time is given.
                    if (startTime.HasValue || endTime.HasValue) play.SetHeader(RtspHeaders.Range, RtspHeaders.RangeHeader(startTime, endTime, rangeType, rangeFormat));

                    RtspMessage response = SendRtspRequest(play);

                    //Handle allowed problems with reception
                    if (response != null && response.MessageType == RtspMessageType.Response)
                    {
                        if (response.StatusCode == RtspStatusCode.InvalidRange)
                        {
                            play.RemoveHeader(Rtsp.RtspHeaders.Range);
                            ++play.CSeq;
                            return SendRtspRequest(play);
                        }
                    }

                    //Connect and wait for Packets
                    if (!m_RtpClient.Connected) m_RtpClient.Connect();

                    //If we have a timeout to switch the protocols and the protocol has not been forced
                    if (m_RtpClient.TotalBytesReceieved == 0)
                    {
                        m_ProtocolSwitchTimer = new System.Threading.Timer(new TimerCallback(SwitchProtocols), null, ProtocolSwitchTime, Utility.InfiniteTimeSpan);
                    }

                    //Setup a timer to send any requests to keep the connection alive and ensure media is flowing.
                    if (m_KeepAliveTimer == null) m_KeepAliveTimer = new Timer(new TimerCallback(SendKeepAlive), null, m_RtspTimeout, Utility.InfiniteTimeSpan);

                    return response;
                }
            }
            catch { throw; }
        }

        /// <summary>
        /// Sends a PAUSE Request
        /// </summary>
        /// <param name="location">The location to indicate in the request</param>
        /// <returns>The response</returns>
        public RtspMessage SendPause(MediaDescription mediaDescription = null, bool force = false)
        { 
            //Ensure media has been setup unless forced.
            if (mediaDescription != null && !force)
            {
                //Get a context for the media
                var context = Client.GetContextForMediaDescription(mediaDescription);

                //If there is no context then throw an exception.
                if (context == null) throw new InvalidOperationException("The given mediaDescription has not been SETUP.");
            }

            //Fire the event now
            OnPausing(mediaDescription);

            //Send the pause request, determining if the request is for all media or just one.
            return SendPause(mediaDescription != null ? mediaDescription.GetAbsoluteControlUri(Location) : Location, force);
        }


        public RtspMessage SendPause(Uri location, bool force = false)
        {
            //If the server doesn't support it
            if (!SupportedMethods.Contains(RtspMethod.PAUSE) && !force) throw new InvalidOperationException("Server does not support PAUSE.");

            //if (!Playing) throw new InvalidOperationException("RtspClient is not Playing.");
            using (RtspMessage pause = new RtspMessage(RtspMessageType.Request)
                {
                    Method = RtspMethod.PAUSE,
                    Location = location ?? Location
                })
            {
                return SendRtspRequest(pause);                 
            }
        }        

        /// <summary>
        /// Sends a ANNOUNCE Request
        /// </summary>
        /// <param name="location">The location to indicate in the request, otherwise null to use the <see cref="Location"/></param>
        /// <param name="sdp">The <see cref="SessionDescription"/> to ANNOUNCE</param>
        /// <returns>The response</returns>
        public RtspMessage SendAnnounce(Uri location, SessionDescription sdp, bool force = false)
        {
            if (!SupportedMethods.Contains(RtspMethod.ANNOUNCE) && !force) throw new InvalidOperationException("Server does not support ANNOUNCE.");
            if (sdp == null) throw new ArgumentNullException("sdp");
            using (RtspMessage announce = new RtspMessage(RtspMessageType.Request)
            {
                Method = RtspMethod.ANNOUNCE,
                Location = location ?? Location                
            })
            {
                announce.Body = sdp.ToString();
                announce.SetHeader(RtspHeaders.ContentType, Sdp.SessionDescription.MimeType);
                return SendRtspRequest(announce);
            }
        }

        //SendRecord

        internal void SendKeepAlive(object state)
        {
            try
            {
                if (!Connected) return;

                if (m_RtspTimeout > TimeSpan.Zero)
                {
                    //Darwin DSS and other servers might not support GET_PARAMETER
                    if (m_SupportedMethods.Contains(RtspMethod.GET_PARAMETER))
                    {
                        using (SendGetParameter(null)) ;
                    }
                    else if (m_SupportedMethods.Contains(RtspMethod.OPTIONS)) //If at least options is supported
                    {
                        using (SendOptions()) ;
                    }
                    else if (m_SupportedMethods.Contains(RtspMethod.PLAY)) //If at least PLAY is supported
                    {
                        using (SendPlay()) ;
                    }
                }
              
                //Determine next time
                m_KeepAliveTimer.Change(m_RtspTimeout, Utility.InfiniteTimeSpan);

                //Only perform these actions if playing anything.
                if (Playing)
                {
                    //Ensure everything is flowing
                    EnsureMediaFlow();

                    //Raise events for ended media.
                    foreach (var ended in Client.GetTransportContexts().Where(tc => !tc.IsContinious && tc.TimeReceiving >= tc.MediaEndTime)) OnStopping(ended.MediaDescription);
                }
                    
            }
            catch
            {
                //Maybe should only happen if !Playing or !Connected?
                if(m_KeepAliveTimer != null) m_KeepAliveTimer.Dispose();
                m_KeepAliveTimer = null;
            }
        }

        public void EnsureMediaFlow()
        {
            //If playing for greater than the timeout (If the media is shorter than this it would have already ended).
            if (Playing && Client.Uptime > m_RtspTimeout)
            {
                //Determine if there any are contexts without data flow by findings contexts where a packet has not been received  OR the last packet was received more then the interval ago.
                var contextsWithoutDataFlow = Client.GetTransportContexts().Where(tc => tc.LastRtpPacketReceived > tc.ReceiveInterval);

                //If there are such contexts
                if (contextsWithoutDataFlow.Any())
                {
                    //If the server doens't support pause then we cant pause.
                    bool supportPause = m_SupportedMethods.Contains(RtspMethod.PAUSE);

                    //If any media was pausedOrStopped.
                    bool pausedOrStoppedAnything = false;

                    //If we cannot stop a single media item we will set this to true.
                    bool stopAll = false;

                    //Iterate all inactive contexts.
                    foreach (var context in contextsWithoutDataFlow)
                    {
                        //Send a pause request if not already paused and the server supports PAUSE
                        if (supportPause)
                        {
                            //Send the PAUSE request
                            using (var pauseResponse = SendPause(context.MediaDescription))
                            {
                                //If the paused request was not a sucess then it's probably due to an aggregate operation
                                pausedOrStoppedAnything = pauseResponse != null && pauseResponse.StatusCode == RtspStatusCode.OK;

                                //Determine if we have to stop everything.
                                if (!pausedOrStoppedAnything)
                                {
                                    //See if everything has to be stopped.
                                    stopAll = pauseResponse.StatusCode == RtspStatusCode.AggregateOpperationNotAllowed;

                                    //Could move this logic to the SendPause method which would check it before returning the response.

                                    //Ensure external state is observed
                                    OnPlaying(context.MediaDescription);
                                }
                            }
                        }
                        else
                        {
                            //We can't pause so STOP JUST THIS MEDIA
                            using (var teardownResponse = SendTeardown(context.MediaDescription))
                            {
                                //If the Teardown was not a success then it's probably due to an aggregate operation.
                                pausedOrStoppedAnything = teardownResponse == null || teardownResponse != null && teardownResponse.StatusCode == RtspStatusCode.OK;
                                
                                //Determine if we have to stop everything.
                                if (!pausedOrStoppedAnything) stopAll = teardownResponse.StatusCode == RtspStatusCode.AggregateOpperationNotAllowed;
                            }
                        }

                        //If we have to stop everything and the server doesn't support pause then stop iterating.
                        if (stopAll) break;

                        //The media was paused ot stopped, so play it again.
                        if (pausedOrStoppedAnything) Play(context.MediaDescription);
                    }

                    //If everything needs to stop.
                    if (stopAll)
                    {
                        if (supportPause)
                        {
                            //Pause all media
                            Pause();

                            //Start playing again
                            StartPlaying();
                        }
                        else
                        {
                            //Stop playing everything
                            StopPlaying();

                            //Start playing everything
                            StartPlaying();
                        }
                    } 
                }
            }
        }

        public RtspMessage SendGetParameter(string body = null, string contentType = null, bool force = false)
        {
            //If the server doesn't support it
            if (!SupportedMethods.Contains(RtspMethod.GET_PARAMETER) && !force) throw new InvalidOperationException("Server does not support GET_PARAMETER.");

            using (RtspMessage get = new RtspMessage(RtspMessageType.Request)
            {
                Method = RtspMethod.GET_PARAMETER,
                Location = Location,
                Body = body ?? string.Empty
            })
            {
                if (!string.IsNullOrWhiteSpace(contentType)) get.SetHeader(RtspHeaders.ContentType, contentType);
                return SendRtspRequest(get);
            }
        }

        public RtspMessage SendSetParameter(string body = null, string contentType = null, bool force = false)
        {
            //If the server doesn't support it
            if (!SupportedMethods.Contains(RtspMethod.SET_PARAMETER) && !force) throw new InvalidOperationException("Server does not support GET_PARAMETER.");

            using (RtspMessage set = new RtspMessage(RtspMessageType.Request)
            {
                Method = RtspMethod.SET_PARAMETER,
                Location = Location,
                Body = body ?? string.Empty
            })
            {
                if (!string.IsNullOrWhiteSpace(contentType)) set.SetHeader(RtspHeaders.ContentType, contentType);
                return SendRtspRequest(set);
            }
        }

        #endregion

        #region IDisposable

        public override void Dispose()
        {
            if (Disposed) return;

            StopPlaying();

            base.Dispose();

            if (m_RtpClient != null)
            {
                m_RtpClient.InterleavedData -= ProcessInterleaveData;
                if (!m_RtpClient.Disposed) m_RtpClient.Dispose();
                m_RtpClient = null;
            }

            if (m_Buffer != null)
            {
                m_Buffer.Dispose();
                m_Buffer = null;
            }

            if (m_InterleaveEvent != null)
            {
                m_InterleaveEvent.Dispose();
                m_InterleaveEvent = null;
            }

            if (m_LastTransmitted != null)
            {
                m_LastTransmitted.Dispose();
                m_LastTransmitted = null;
            }

            OnConnect = null;
            OnDisconnect = null;
            OnStop = null;
            OnPlay = null;
            OnPause = null;
            OnRequest = null;
            OnResponse = null;
        }

        #endregion

        IEnumerable<Socket> Common.ISocketReference.GetReferencedSockets()
        {
            return m_RtspSocket.Yield();
        }
    }
}
