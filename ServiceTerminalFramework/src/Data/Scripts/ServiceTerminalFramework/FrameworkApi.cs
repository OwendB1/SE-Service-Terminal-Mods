using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace ServiceTerminalFramework
{
    using Registration = MyTuple<string, string,
        Action<IMyTerminalBlock, IMyEntity, bool, Action>>;

    internal sealed class FrameworkApi
    {
        internal const long RegistrationChannel = 0x5354465245470001L;
        internal const long ReadyChannel = 0x5354465244590001L;

        private readonly Dictionary<string, ServiceEntry> _entries =
            new Dictionary<string, ServiceEntry>(StringComparer.Ordinal);

        internal event Action Changed;

        internal void Start()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(RegistrationChannel, OnMessage);
            MyAPIGateway.Utilities.SendModMessage(ReadyChannel, true);
            FrameworkSession.Log("API ready (channel=" + RegistrationChannel + ")");
        }

        internal void Stop()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(RegistrationChannel, OnMessage);
            _entries.Clear();
            Changed = null;
        }

        internal List<ServiceEntry> GetEntries()
        {
            List<ServiceEntry> entries = new List<ServiceEntry>(_entries.Values);
            entries.Sort(delegate(ServiceEntry left, ServiceEntry right)
            {
                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });
            return entries;
        }

        private void OnMessage(object message)
        {
            if (message is string)
            {
                string id = (string)message;
                if (_entries.Remove(id)) NotifyChanged();
                return;
            }

            if (!(message is Registration)) return;
            Registration registration = (Registration)message;
            if (string.IsNullOrWhiteSpace(registration.Item1) ||
                string.IsNullOrWhiteSpace(registration.Item2) || registration.Item3 == null)
                return;

            _entries[registration.Item1] = new ServiceEntry(registration.Item1,
                registration.Item2, registration.Item3);
            FrameworkSession.Log("Registered service " + registration.Item1 + " (" +
                registration.Item2 + ")");
            NotifyChanged();
        }

        private void NotifyChanged()
        {
            Action changed = Changed;
            if (changed != null) changed();
        }
    }

    internal sealed class ServiceEntry
    {
        internal readonly string Id;
        internal readonly string Name;
        internal readonly Action<IMyTerminalBlock, IMyEntity, bool, Action> SetActive;

        internal ServiceEntry(string id, string name,
            Action<IMyTerminalBlock, IMyEntity, bool, Action> setActive)
        {
            Id = id;
            Name = name;
            SetActive = setActive;
        }
    }
}
