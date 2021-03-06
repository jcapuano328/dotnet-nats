﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using dotnet_sockets;

namespace dotnet_nats
{
    public class NATS : INATS, IDisposable
    {
        #region Constructors
        IFactory _factory;
        Options _opts;
        ILog _log;
        ICollection<IServer> _servers;
        IEnumerator<IServer> _itr;
        IServer _server;
        IMessenger _msgr;
        IDictionary<string, Subscription> _subscriptions;                
        ConcurrentQueue<Action<string>> _pongs;
        bool _closing;
        Action<bool> _connecthandler;
        CancellationTokenSource _cancel = new CancellationTokenSource();

        public NATS(IFactory factory, Options opts, ILog log)
        {
            _factory = factory;
            _opts = opts;
            _log = log;
            _subscriptions = new Dictionary<string, Subscription>();
            _pongs = new ConcurrentQueue<Action<string>>();
            _msgr = _factory.NewMessenger();
			connectMessenger();
            loadServers();
        }
        public NATS(IFactory factory, Options opts) : this(factory, opts, new log.Logger()) { }                
        #endregion

        #region Connect
        public static INATS Connect(Options opts, ILog log)
        {            
            INATS nats = new NATS(new Factory(log), opts, log);
            nats.Connect();
            return nats;
        }
        public static INATS Connect(Options opts)
        {
            return NATS.Connect(opts, new log.Logger());
        }

        void reconnect()
        {
            _log.Debug("Reconnecting to server");
            new Action(() => { Connect(); }).ExecuteAfter(_opts.reconnectDelay);
        }
        #endregion

        #region INATS
        public int Servers { get { return _servers != null ? _servers.Count : 0; } }
        public bool Connected { get { return _server != null && _server.IsConnected; } }

        public Task<bool> Connect(Action<bool> handler = null)
        {
            try
            {
                _connecthandler = handler;
                if (_server == null)
                    _server = nextServer();
                if (_server == null)
                {
                    _log.Warn("Failed to retrieve a server from the queue");
                    return Task<bool>.FromResult(false);
                }
                _log.Info("Connecting to Server @ {0}", _server.URL);
                return _server.Open();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to connect to server", ex);
                throw;
            }
        }

        public void Close()
        {
            try
            {            
                if (_server != null)
                {
                    _log.Info("Disconnecting from Server @ {0}", _server.URL);
                    _closing = true;                    
                    _server.Close().Wait();
                }            
            }
            catch (Exception ex)
            {
                _log.Error("Failed to connect to server", ex);
                throw;
            }
            finally
            {
                _closing = false;
                _server = null;
                _itr.Reset();
            }
        }

        public void Publish(string subject, string data, Action<string> handler = null)
        {
            try
            {
                sendPublication(subject, data);                
                if (handler != null)
                    sendPing(handler);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to publish {0} to server", ex, subject);
                throw;
            }            
        }

        public void Subscribe(string subject, Action<string> handler)
        {            
            if (!_subscriptions.ContainsKey(subject))
            {
                _subscriptions[subject] = new Subscription(subject, handler);
                sendSubscription(_subscriptions[subject].ID, subject);
            }
        }

        public void Unsubscribe(string subject)
        {            
            if (_subscriptions.ContainsKey(subject))
            {
                sendUnsubscription(_subscriptions[subject].ID);
                _subscriptions.Remove(subject);
            }
                            
        }

        public void Flush()
        {
            throw new NotImplementedException();
        }
        #endregion

        #region send
        void sendConnect()
        {
            _server.Send(Message.Connect(_opts));
        }

        void sendPublication(string subject, string data)
        {
            _server.Send(Message.Publish(subject, data));
        }

        void sendSubscription(int id, string subject, string queue = " ")
        {
            _server.Send(Message.Subscribe(id, subject, queue));
        }

        void sendSubscriptions()
        {
            foreach(var sub in _subscriptions)
            {
                sendSubscription(sub.Value.ID, sub.Value.Subject, sub.Value.Queue);
            }            
        }

        void sendUnsubscription(int sid)
        {
            _server.Send(Message.Unsubscribe(sid));
        }

        void sendPing(Action<string> handler)
        {
            _pongs.Enqueue(handler);
            _server.Send(Message.Ping());
        }
            
        void sendPong()
        {
            _server.Send(Message.Pong());
        }
			
        #endregion

        #region receive
		void connectMessenger()
		{
			_msgr.Msg += (s,e) => {
                // find the subscribers to this message
                var subs = _subscriptions.Where(x => x.Value.ID == e.SubscriptionID);
                foreach (var kvp in subs)
                {
                    var sub = kvp.Value;
                    if (sub != null && sub.Handler != null)
                        sub.Handler(e.Data);
                }
			};
			_msgr.Ping += (s,e) => {
				try
				{
					sendPong();
				} 
				catch (Exception ex)
				{
					_log.Error("Failed to process Ping", ex);
				}
			};
			_msgr.Pong += (s,e) => {
				try
				{
		            Action<string> pong;                                    
		            if (_pongs.TryDequeue(out pong))
		            {
		                pong(string.Empty);
		            }                                    
				} 
				catch (Exception ex)
				{
					_log.Error("Failed to process Pong", ex);
				}
			};
			_msgr.Info += (s,e) => {
			};
			_msgr.Error += (s,e) => {
                try
                {
                    Close();
                    reconnect();
                }
                catch (Exception ex)
                {
                    _log.Error("Failed to process Error", ex);
                }
            };
		}
		
        Task receive()
        {
            return Task.Factory.StartNew(() =>
            {                                
                while (!_closing && _server.IsConnected)
                {
                    _server.Receive().Wait(_cancel.Token);
                }
            });
        }
        #endregion

        #region servers
        void loadServers()
        {            
            _servers = _factory.NewServer(_opts.uris.ToArray());
            _servers.ToList().ForEach(connectServer);            
            _itr = _servers.GetEnumerator();            
            _server = nextServer();
        }

        void connectServer(IServer s)
        {
            s.Connected += (sender, b) =>
            {
                _log.Debug("Connected to server @ {0}", s.URL);
                sendConnect();                
                if (_connecthandler != null) _connecthandler(true);
                receive();
            };
            s.Disconnected += (sender, b) =>
            {
                _log.Warn("Disconnected from server @ {0}", s.URL);
                _cancel.Cancel();
                if (_connecthandler != null) _connecthandler(false);
                if (_closing) return;

                reconnect();
            };
            s.Error += (sender, err) =>
            {
                _log.Error("Error with server @ {0}", s.URL, err.Value);
            };
            s.ReceivedData += (sender, args) =>
            {
                //_log.Trace("[" + Encoding.UTF8.GetString(args.Data, 0, args.Size).Replace("\r", "R").Replace("\n", "N") + "]");                
                _msgr.Receive(args.Data, args.Size);
            };
            s.Sent += (sender, sent) =>
            {
                _log.Trace("Sent {0} bytes to server @ {1}", sent.Value, s.URL);
            };
        }

        // need to expand this for clustering: basically, use a server until its maximum re-connect attempts is reached, then move to the next
        // eventually swing around to the first...
        IServer nextServer()
        {
            if (!_itr.MoveNext())
            {
                _itr.Reset();
                _itr.MoveNext();
            }
            return _itr.Current;            
        }

        #endregion

        #region IDisposable
        public void Dispose()
        {
            //throw new NotImplementedException();
        }
        #endregion
    }
}
