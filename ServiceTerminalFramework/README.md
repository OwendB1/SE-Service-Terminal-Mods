# Service Terminal Framework

Client-side framework for sharing the Economy 2 Services Terminal's main interaction between vanilla Services and multiple mod-provided services.

The framework replaces each live `services` detector entry with a reversible proxy while retaining the original use object. Pressing the normal interaction key invokes the original primary `Use()` immediately, then keeps a Rich HUD provider navigation visible beside its right edge. The full list sits beside the native screen when there is enough horizontal margin. On narrower aspect ratios it folds into a compact rail that expands inward on demand. Selecting **Vanilla services** deactivates the current mod view and reveals the already-open native screen; every other option activates its registered provider. While a custom provider is active, the framework hides the normal gameplay HUD and restores the player's previous HUD mode after the provider reports that it closed. Original HUD hints and secondary actions are delegated unchanged.

The block definition and model are never modified.

## Dependencies

- [Rich HUD Master](https://steamcommunity.com/sharedfiles/filedetails/?id=1965654081)

If Rich HUD is unavailable, the retained vanilla Services interaction still opens normally.

## Provider API

The API deliberately uses only game/BCL types so provider mods require no assembly reference to the framework.

```csharp
using System;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.ModAPI;
using VRage.ModAPI;

const long RegistrationChannel = 0x5354465245470001L;
const long ReadyChannel = 0x5354465244590001L;
const string ServiceId = "YourName.YourService";

Action<IMyTerminalBlock, IMyEntity, bool, Action> setActive = delegate(
    IMyTerminalBlock terminal, IMyEntity user, bool active, Action closed)
{
    if (active)
    {
        // Show your client UI and invoke closed when it closes itself.
    }
    else
    {
        // Hide your client UI. The framework already knows it is closing.
    }
};

Action register = delegate
{
    MyAPIGateway.Utilities.SendModMessage(RegistrationChannel,
        new MyTuple<string, string,
            Action<IMyTerminalBlock, IMyEntity, bool, Action>>(
            ServiceId, "Your service name", setActive));
};

Action<object> ready = delegate(object message)
{
    if (message is bool && (bool)message) register();
};

// Start: handles both provider/framework load orders.
MyAPIGateway.Utilities.RegisterMessageHandler(ReadyChannel, ready);
register();

// Unload:
MyAPIGateway.Utilities.UnregisterMessageHandler(ReadyChannel, ready);
MyAPIGateway.Utilities.SendModMessage(RegistrationChannel, ServiceId);
```

Sending another registration with the same ID replaces that provider entry. Sending the ID string unregisters it. Provider names are sorted alphabetically; vanilla Services always appears first. Providers must treat repeated activation/deactivation calls as safe, hide their UI when `active` is `false`, and invoke the supplied `closed` callback exactly once when an active panel closes itself. The callback lets the framework restore the gameplay HUD and navigation state.
