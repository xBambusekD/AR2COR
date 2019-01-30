﻿using System.Collections.Generic;
using System.Reflection;
using System;
using SimpleJSON;
using UnityEngine;

#if UNITY_EDITOR
using WebSocketSharp;
using System.Threading;
#endif

#if !UNITY_EDITOR
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.Foundation;
#endif

/**
 * This class handles the connection with the external ROS world, deserializing
 * json messages into appropriate instances of packets and messages.
 * 
 * This class also provides a mechanism for having the callback's exectued on the rendering thread.
 * (Remember, Unity has a single rendering thread, so we want to do all of the communications stuff away
 * from that. 
 * 
 * The one other clever thing that is done here is that we only keep 1 (the most recent!) copy of each message type
 * that comes along.
 * 
 * Version History
 * 3.2 - added support for Microsoft Hololens connection
 * 3.1 - changed methods to start with an upper case letter to be more consistent with c#
 * style.
 * 3.0 - modification from hand crafted version 2.0
 * 
 * @author Michael Jenkin, Robert Codd-Downey and Andrew Speers
 * @update Daniel Bambušek
 * @version 3.2
 */

namespace ROSBridgeLib {
    public class ROSBridgeWebSocketConnection {
        private class RenderTask {
            private Type _subscriber;
            private string _topic;
            private ROSBridgeMsg _msg;

            public RenderTask(Type subscriber, string topic, ROSBridgeMsg msg) {
                _subscriber = subscriber;
                _topic = topic;
                _msg = msg;
            }

            public Type getSubscriber() {
                return _subscriber;
            }

            public ROSBridgeMsg getMsg() {
                return _msg;
            }

            public string getTopic() {
                return _topic;
            }
        };
        private string _host;
        private int _port;
        public bool _connected;

#if UNITY_EDITOR
        private WebSocket _ws;
        private System.Threading.Thread _myThread;
#endif

        //WebSocket client from Windows.Networking.Sockets
#if !UNITY_EDITOR
        private MessageWebSocket messageWebSocket;
        Uri server;
        DataWriter dataWriter;
#endif
        private List<Type> _subscribers; // our subscribers
        private List<Type> _publishers; //our publishers
        private Type _serviceResponse; // to deal with service responses
        private string _serviceName = null;
        private string _serviceValues = null;
        private List<RenderTask> _taskQ = new List<RenderTask>();

        private object _queueLock = new object();

        private static string GetMessageType(Type t) {
            return (string)t.GetMethod("GetMessageType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).Invoke(null, null);
        }

        private static string GetMessageTopic(Type t) {
            return (string)t.GetMethod("GetMessageTopic", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).Invoke(null, null);
        }

        private static ROSBridgeMsg ParseMessage(Type t, JSONNode node) {
            return (ROSBridgeMsg)t.GetMethod("ParseMessage", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).Invoke(null, new object[] { node });
        }

        private static void Update(Type t, ROSBridgeMsg msg) {
            t.GetMethod("CallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).Invoke(null, new object[] { msg });
        }

        private static void ServiceResponse(Type t, string service, string yaml) {
            t.GetMethod("ServiceCallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).Invoke(null, new object[] { service, yaml });
        }

        private static void IsValidServiceResponse(Type t) {
            if (t.GetMethod("ServiceCallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) == null)
                throw new Exception("invalid service response handler");
        }

        private static void IsValidSubscriber(Type t) {
            if (t.GetMethod("CallBack", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) == null)
                throw new Exception("missing Callback method");
            if (t.GetMethod("GetMessageType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) == null)
                throw new Exception("missing GetMessageType method");
            if (t.GetMethod("GetMessageTopic", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) == null)
                throw new Exception("missing GetMessageTopic method");
            if (t.GetMethod("ParseMessage", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) == null)
                throw new Exception("missing ParseMessage method");
        }

        private static void IsValidPublisher(Type t) {
            if (t.GetMethod("GetMessageType", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) == null)
                throw new Exception("missing GetMessageType method");
            if (t.GetMethod("GetMessageTopic", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy) == null)
                throw new Exception("missing GetMessageTopic method");
        }

        /**
		 * Make a connection to a host/port. 
		 * This does not actually start the connection, use Connect to do that.
		 */
        public ROSBridgeWebSocketConnection() {
            _connected = false;
#if UNITY_EDITOR
            _myThread = null;
#endif
            _subscribers = new List<Type>();
            _publishers = new List<Type>();
        }

        public void SetIPConfig(string serverIP, int port) {
            _host = "ws://" + serverIP;
            _port = port;
        }

        /**
		 * Add a service response callback to this connection.
		 */
        public void AddServiceResponse(Type serviceResponse) {
            IsValidServiceResponse(serviceResponse);
            _serviceResponse = serviceResponse;
        }

        /**
		 * Add a subscriber callback to this connection. There can be many subscribers.
		 */
        public void AddSubscriber(Type subscriber) {
            IsValidSubscriber(subscriber);
            _subscribers.Add(subscriber);
        }

        /**
		 * Add a publisher to this connection. There can be many publishers.
		 */
        public void AddPublisher(Type publisher) {
            IsValidPublisher(publisher);
            _publishers.Add(publisher);
        }

        /**
		 * Connect to the remote ros environment.
		 */
        public void Connect() {
#if UNITY_EDITOR
            _myThread = new System.Threading.Thread(Run);
            _myThread.Start();
#endif

#if !UNITY_EDITOR        

            messageWebSocket = new MessageWebSocket();

            server = new Uri(_host + ":" + _port.ToString());

            //set handler for MessageReceived and parse string received from socket
            messageWebSocket.MessageReceived += (sender, e) => this.OnMessage(e.GetDataReader().ReadString(e.GetDataReader().UnconsumedBufferLength));          
            IAsyncAction asyncAction = messageWebSocket.ConnectAsync(server);
            AsyncActionCompletedHandler asyncActionCompletedHandler = new AsyncActionCompletedHandler(NetworkConnectedHandler);
            asyncAction.Completed = asyncActionCompletedHandler;
#endif
        }

        //Successfull network connection handler on hololens
#if !UNITY_EDITOR
        public void NetworkConnectedHandler(IAsyncAction asyncInfo, AsyncStatus status)
        {
            // Status completed is successful.
            if (status == AsyncStatus.Completed)
            {            
                //Creating the writer that will be repsonsible to send a message through Rosbridge
                dataWriter = new DataWriter(messageWebSocket.OutputStream);
                
                //Connect to all topics in _subscribers and _publishers
                foreach (Type p in _subscribers) {
                    dataWriter.WriteString(ROSBridgeMsg.Subscribe(GetMessageTopic(p), GetMessageType(p)));
                    dataWriter.StoreAsync();
                    Debug.Log("Sending " + ROSBridgeMsg.Subscribe(GetMessageTopic(p), GetMessageType(p)));
                }
                foreach (Type p in _publishers) {
                    dataWriter.WriteString(ROSBridgeMsg.Advertise(GetMessageTopic(p), GetMessageType(p)));
                    dataWriter.StoreAsync();
                    Debug.Log("Sending " + ROSBridgeMsg.Advertise(GetMessageTopic(p), GetMessageType(p)));
                }
                _connected = true;
            }
        }
#endif

        /**
		 * Disconnect from the remote ros environment.
		 */
        public void Disconnect() {
#if UNITY_EDITOR
            _myThread.Abort();
            foreach (Type p in _subscribers) {
                _ws.Send(ROSBridgeMsg.UnSubscribe(GetMessageTopic(p)));
                Debug.Log("Sending " + ROSBridgeMsg.UnSubscribe(GetMessageTopic(p)));
            }
            foreach (Type p in _publishers) {
                _ws.Send(ROSBridgeMsg.UnAdvertise(GetMessageTopic(p)));
                Debug.Log("Sending " + ROSBridgeMsg.UnAdvertise(GetMessageTopic(p)));
            }
            _ws.Close();
            _connected = false;
#endif
#if !UNITY_EDITOR
            Debug.Log("Disconnectig...");
            foreach (Type p in _subscribers) {
                dataWriter.WriteString(ROSBridgeMsg.UnSubscribe(GetMessageTopic(p)));
                dataWriter.StoreAsync();
                Debug.Log("Sending " + ROSBridgeMsg.UnSubscribe(GetMessageTopic(p)));
            }
            foreach (Type p in _publishers) {
                dataWriter.WriteString(ROSBridgeMsg.UnAdvertise(GetMessageTopic(p)));
                dataWriter.StoreAsync();
                Debug.Log("Sending " + ROSBridgeMsg.UnAdvertise(GetMessageTopic(p)));
            }
            messageWebSocket.Dispose();
            messageWebSocket = null;
            _connected = false;
#endif
        }

        private void Run() {
#if UNITY_EDITOR
            _ws = new WebSocket(_host + ":" + _port);
            _ws.OnMessage += (sender, e) => this.OnMessage(e.Data);
            //TODO connecting takes too long
            _ws.Connect();

            foreach (Type p in _subscribers) {
                _ws.Send(ROSBridgeMsg.Subscribe(GetMessageTopic(p), GetMessageType(p)));
                Debug.Log("Sending " + ROSBridgeMsg.Subscribe(GetMessageTopic(p), GetMessageType(p)));
            }
            foreach (Type p in _publishers) {
                _ws.Send(ROSBridgeMsg.Advertise(GetMessageTopic(p), GetMessageType(p)));
                Debug.Log("Sending " + ROSBridgeMsg.Advertise(GetMessageTopic(p), GetMessageType(p)));
            }

            _connected = true;

            while (true) {
                Thread.Sleep(1000);
            }
#endif
        }

        //Calls when message is received
        private void OnMessage(string s) {
            //Debug.Log ("Got a message " + s);
            if ((s != null) && !s.Equals("")) {
                JSONNode node = JSONNode.Parse(s);
                //Debug.Log ("Parsed it");
                string op = node["op"];
                //Debug.Log ("Operation is " + op);
                if ("publish".Equals(op)) {
                    string topic = node["topic"];
                    //Debug.Log ("Got a message on " + topic);
                    foreach (Type p in _subscribers) {
                        if (topic.Equals(GetMessageTopic(p))) {
                            //Debug.Log ("And will parse it " + GetMessageTopic (p));
                            ROSBridgeMsg msg = ParseMessage(p, node["msg"]);
                            RenderTask newTask = new RenderTask(p, topic, msg);
                            lock (_queueLock) {
                                bool found = false;
                                for (int i = 0; i < _taskQ.Count; i++) {
                                    if (_taskQ[i].getTopic().Equals(topic)) {
                                        _taskQ.RemoveAt(i);
                                        _taskQ.Insert(i, newTask);
                                        found = true;
                                        break;
                                    }
                                }
                                if (!found)
                                    _taskQ.Add(newTask);
                            }

                        }
                    }
                }
                else if ("service_response".Equals(op)) {
                    Debug.Log("Got service response " + node.ToString());
                    _serviceName = node["service"];
                    _serviceValues = (node["values"] == null) ? "" : node["values"].ToString();
                }
                else
                    Debug.Log("Must write code here for other messages");
            }
            else
                Debug.Log("Got an empty message from the web socket");
        }

        public void Render() {
            RenderTask newTask = null;
            lock (_queueLock) {
                if (_taskQ.Count > 0) {
                    newTask = _taskQ[0];
                    _taskQ.RemoveAt(0);
                }
            }
            if (newTask != null)
                Update(newTask.getSubscriber(), newTask.getMsg());

            if (_serviceName != null && _serviceValues != null) {
                ServiceResponse(_serviceResponse, _serviceName, _serviceValues);
                _serviceName = null;
                _serviceValues = null;
            }
        }

        public void Publish(String topic, ROSBridgeMsg msg, bool debug_log = true) {
#if UNITY_EDITOR
            if (_ws != null) {
                string s = ROSBridgeMsg.Publish(topic, msg.ToYAMLString());
                if (debug_log)
                    Debug.Log ("Sending " + s);
                _ws.Send(s);
            }
#endif
#if !UNITY_EDITOR
            if (messageWebSocket != null) {
                string s = ROSBridgeMsg.Publish(topic, msg.ToYAMLString());
                dataWriter.WriteString(s);
                dataWriter.StoreAsync();
            }
#endif
        }

        public void CallService(string service, string args) {
#if UNITY_EDITOR
            if (_ws != null) {
                string s = ROSBridgeMsg.CallService(service, args);
                Debug.Log("Sending " + s);
                _ws.Send(s);
            }
#endif
#if !UNITY_EDITOR
            if (messageWebSocket != null) {
                string s = ROSBridgeMsg.CallService(service, args);
                dataWriter.WriteString(s);
                dataWriter.StoreAsync();
            }
#endif
        }
    }

}
