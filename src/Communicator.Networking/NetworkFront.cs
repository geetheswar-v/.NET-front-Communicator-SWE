// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text;
using System.Net;
using Communicator.Core.RPC;

namespace Communicator.Networking;
public class NetworkFront : IController, INetworking
{
    /** Variable to store the function mappings. */
    private Dictionary<int, IMessageListener> _listeners = new Dictionary<int, IMessageListener>();

    /** Variable to track the number of functions. */
    private int _functionCount = 1;
    /** Variable to store the RPC. */
    private IRPC _moduleRpc = null;
    public void SendData(byte[] data, ClientNode[] dest, int module, int priority)
    {
        int dataLength = data.Length;
        int destSize = 0;
        foreach (ClientNode record in dest)
        {
            byte[] hostName = Encoding.UTF8.GetBytes(record.HostName);
            destSize += 1 + hostName.Length + sizeof(int); // 1 byte length + host + port
        }
        // 1 - data length 1 - dest count 1 - module 1 - priority
        int bufferSize = dataLength + destSize + 4 * sizeof(int);
        MemoryStream buffer = new MemoryStream(bufferSize);
        BinaryWriter writer = new BinaryWriter(buffer);
        writer.Write(IPAddress.HostToNetworkOrder(dest.Length));
        foreach (ClientNode record in dest)
        {
            byte[] hostName = Encoding.UTF8.GetBytes(record.HostName);
            writer.Write((byte)hostName.Length);
            writer.Write(hostName);
            writer.Write(IPAddress.HostToNetworkOrder(record.Port));
        }
        writer.Write(IPAddress.HostToNetworkOrder(dataLength));
        writer.Write(data);
        writer.Write(IPAddress.HostToNetworkOrder(module));
        writer.Write(IPAddress.HostToNetworkOrder(priority));
        byte[] args = buffer.ToArray();

        _moduleRpc.Call("networkRPCSendData", args);
    }

    public void Broadcast(byte[] data, int module, int priority)
    {
        int dataLength = data.Length;
        int bufferSize = dataLength + 3 * sizeof(int);
        MemoryStream buffer = new MemoryStream(bufferSize);
        BinaryWriter writer = new BinaryWriter(buffer);
        writer.Write(IPAddress.HostToNetworkOrder(dataLength));
        writer.Write(data);
        writer.Write(IPAddress.HostToNetworkOrder(module));
        writer.Write(IPAddress.HostToNetworkOrder(priority));
        byte[] args = buffer.ToArray();

        _moduleRpc.Call("networkRPCBroadcast", args);
    }

    private HashSet<int> _registeredModules = new HashSet<int>();

    public void RegisterModule(int moduleId)
    {
        if (_registeredModules.Contains(moduleId)) return;

        string callbackName = "callback" + moduleId;
        if (_moduleRpc != null)
        {
            _moduleRpc.Subscribe(callbackName, (byte[] args) => {
                if (_listeners.TryGetValue(moduleId, out IMessageListener? listener))
                {
                    listener.ReceiveData(args);
                }
                else
                {
                    Console.WriteLine($"[NetworkFront] Received data for module {moduleId} but no listener subscribed.");
                }
                return new byte[0];
            });
        }
        _registeredModules.Add(moduleId);
    }

    public void Subscribe(int name, IMessageListener function)
    {
        _listeners[name] = function;
        
        if (!_registeredModules.Contains(name))
        {
            RegisterModule(name);
        }
    }

    public void RemoveSubscription(int name)
    {
        int bufferSize = sizeof(int);
        MemoryStream buffer = new MemoryStream(bufferSize);
        BinaryWriter writer = new BinaryWriter(buffer);
        writer.Write(IPAddress.HostToNetworkOrder(name));
        byte[] args = buffer.ToArray();

        _moduleRpc.Call("networkRPCRemoveSubscription", args);
    }

    public void AddUser(ClientNode deviceAddress, ClientNode mainServerAddress)
    {
        int bufferSize = 2 + deviceAddress.HostName.Length + mainServerAddress.HostName.Length
                + 2 * sizeof(int);
        MemoryStream buffer = new MemoryStream(bufferSize);
        BinaryWriter writer = new BinaryWriter(buffer);
        byte[] hostName = Encoding.UTF8.GetBytes(deviceAddress.HostName);
        writer.Write((byte)hostName.Length);
        writer.Write(hostName);
        writer.Write(IPAddress.HostToNetworkOrder(deviceAddress.Port));
        hostName = Encoding.UTF8.GetBytes(mainServerAddress.HostName);
        writer.Write((byte)hostName.Length);
        writer.Write(hostName);
        writer.Write(IPAddress.HostToNetworkOrder(mainServerAddress.Port));
        byte[] args = buffer.ToArray();

        _moduleRpc.Call("getNetworkRPCAddUser", args);
    }

    /**
     * Function to call the subscriber in frontend.
     *
     * @param data the data to send
     */
    public void NetworkFrontCallSubscriber(byte[] data)
    {
        if (data.Length < 4)
        {
            Console.WriteLine($"[NetworkFront] Error: Received data too short ({data.Length} bytes)");
            return;
        }
        
        MemoryStream buffer = new MemoryStream(data);
        BinaryReader reader = new BinaryReader(buffer);
        int module = IPAddress.NetworkToHostOrder(reader.ReadInt32());
        byte[] newData = reader.ReadBytes(data.Length - 4);
        
        Console.WriteLine($"[NetworkFront] NetworkFrontCallSubscriber called for module {module} with {newData.Length} bytes");

        if (_listeners.TryGetValue(module, out IMessageListener? function))
        {
            function.ReceiveData(newData);
        }
        else
        {
            Console.WriteLine($"[NetworkFront] Warning: No listener found for module {module}");
        }
    }

    public void CloseNetworking()
    {
        Console.WriteLine("Closing Networking in front...");
        _moduleRpc.Call("networkRPCCloseNetworking", new byte[0]);
    }

    public void ConsumeRPC(IRPC rpc)
    {
        _moduleRpc = rpc;

        // Subscribe to the multiplexed callback from Java
        _moduleRpc.Subscribe("networkFrontCallSubscriber", (byte[] data) => {
            Console.WriteLine($"[NetworkFront] Received networkFrontCallSubscriber call with {data.Length} bytes");
            NetworkFrontCallSubscriber(data);
            return new byte[0];
        });

        foreach (KeyValuePair<int, IMessageListener> listener in _listeners)
        {
            int key = listener.Key;
            byte[] args = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(key));

            // Call the RPC method asynchronously
            _moduleRpc.Call("networkRPCSubscribe", args);
        }
        
        // Also register any modules that were pre-registered
        foreach (int moduleId in _registeredModules)
        {
             string callbackName = "callback" + moduleId;
             // We don't need to subscribe to callbackName here because Java calls networkFrontCallSubscriber
             // But we keep the subscription just in case legacy code uses it, or to satisfy the interface if needed.
             // Actually, let's just ensure we send the subscription request to Java.
            
            // Send subscription to backend
            byte[] args = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(moduleId));
            _moduleRpc.Call("networkRPCSubscribe", args);
        }
    }
}
