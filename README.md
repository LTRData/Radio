# Radio

.NET ham radio libraries by Olof Lagerkvist, LTR Data.

The repository currently contains one package, [LTRData.RigCtlConnection](https://www.nuget.org/packages/LTRData.RigCtlConnection), for querying and controlling a radio through a [Hamlib `rigctld` daemon](https://github.com/Hamlib/Hamlib/blob/master/doc/man1/rigctld.1). Its C# namespace is `LTRData.RigCtl`.

## Installation and requirements

```sh
dotnet add package LTRData.RigCtlConnection
```

The library targets `net48`, `netstandard2.0`, `netstandard2.1`, `net8.0` and `net9.0`. These are the targets in the project file, which overrides the broader list in the shared build properties.

Run and configure `rigctld` separately on the computer connected to the radio. This library communicates with it over TCP; it does not open the radio's serial/USB device itself or require native Hamlib in the .NET process. Available commands, modes and levels depend on the Hamlib backend and radio.

The client currently creates **IPv4 sockets**. Use an IPv4 endpoint, or a hostname whose selected DNS address is IPv4. The connection uses the plain `rigctld` protocol and provides no TLS or authentication layer.

## API overview

| Type | Purpose |
| --- | --- |
| [RadioConnection](https://github.com/LTRData/Radio/blob/master/RigCtlConnection/RadioConnection.cs) | Connects to the daemon; queries frequency, mode/passband, transmit state, tone and meter levels; sets frequency, mode, VFO, memory bank/channel, levels, repeater shift and transmit state. Also exposes raw command methods. |
| [RadioConnectionFactory](https://github.com/LTRData/Radio/blob/master/RigCtlConnection/RadioConnectionFactory.cs) | Uses `IConfiguration` to locate the daemon and reuses a cached connection when available. |
| [RadioValues](https://github.com/LTRData/Radio/blob/master/RigCtlConnection/RadioValues.cs) | Parses/formats signal-strength and SWR/ALC values and supplies mode-dependent tuning-step defaults. |

Commands use asynchronous methods with cancellation tokens. Frequency parameters are in Hz; setters take `int` frequencies. Most command methods return response strings rather than throwing for every daemon-reported error. For setters, compare the response with `RadioConnection.ResultOK` (`"RPRT 0"`).

The repeater-shift method is currently spelled `SetRepaterShiftAsync` in the public API.

## Read the current frequency

With `rigctld` already listening on IPv4 loopback port 4532:

```csharp
using System;
using System.Net;
using System.Threading;
using LTRData.RigCtl;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

using var radio = await RadioConnection.ConnectAsync(
    new IPEndPoint(IPAddress.Loopback, 4532),
    timeout.Token);

var frequency = await radio.GetFrequencyAsync(timeout.Token);
Console.WriteLine($"Frequency response (Hz on success): {frequency}");
```

This example only queries the radio. Methods such as `SetFrequencyAsync`, `SetModeAsync` and `SetTxAsync` change its state; `SetTxAsync(true, ...)` requests transmission.

## Configuration-based connections

For applications using `Microsoft.Extensions.Configuration`, set the root configuration key:

```json
{
  "RigCtlHost": "127.0.0.1:4532"
}
```

Pass the application's `IConfiguration` to `new RadioConnectionFactory(configuration)`, then call `GetConnectionAsync(cancellationToken)`.

If the setting is absent, the factory uses hostname `rigctl`; if the port is omitted, it uses 4532. Hostname resolution selects the first returned address, without trying other addresses on connection failure.

The factory keeps a weak reference to the connection. Keep your own reference while using it and coordinate its lifetime when sharing the factory. `ResetConnection()` only forgets the cached reference; it does not dispose an existing connection.

## Building

Use an SDK that supports the .NET 9 target, such as the .NET 10 SDK. From the repository root:

```sh
dotnet build RigCtlConnection/RigCtlConnection.csproj -c Debug -f net9.0
```

Outputs are placed under `Debug/<framework>/` or `Release/<framework>/`. Release builds also generate NuGet packages; `LocalNuGetPath` controls the package output directory.

Dependencies are `LTRData.Extensions` and `Microsoft.Extensions.Configuration.Abstractions`. The `net48` and `netstandard2.0` builds additionally use `LTRData.DiscUtils.Streams` for compatibility helpers. The root README is included in the package.
