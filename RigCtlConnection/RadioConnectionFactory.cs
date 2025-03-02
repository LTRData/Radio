using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LTRData.RigCtl;

/// <summary>
/// Class used together with dependency injection to configure radio connection
/// using setting "RigCtlHost" in appsettings.json or similar methods.
/// </summary>
/// <param name="configuration">Configuration object supplied by dependency injection.</param>
public class RadioConnectionFactory(IConfiguration configuration)
{
    private readonly WeakReference<RadioConnection?> activeConnection = new(null);

    private IPEndPoint? IPEndPoint;

    /// <summary>
    /// Gets or creates connection asynchronously and returns a <see cref="RadioConnection"/> object
    /// that can be used to communicate with the radio.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="Exception">Failed to connect to a listening rigctld daemon at the supplied host name</exception>
    public async ValueTask<RadioConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!activeConnection.TryGetTarget(out var connection) ||
                connection is null ||
                !connection.IsConnected)
            {
                connection?.Dispose();
                var address = await GetIPEndPointAsync(cancellationToken).ConfigureAwait(false);
                connection = await RadioConnection.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
                activeConnection.SetTarget(connection);
            }

            return connection;
        }
        catch (Exception ex)
        {
            activeConnection.SetTarget(null);
            throw new Exception("Radio connection closed", ex);
        }
    }

    /// <summary>
    /// Resets current connection and reconnects next time <see cref="GetConnectionAsync(CancellationToken)"/> is called.
    /// </summary>
    public void ResetConnection() => activeConnection.SetTarget(null);

    private async ValueTask<IPEndPoint> GetIPEndPointAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hostname = configuration["RigCtlHost"] ?? "rigctl";

        var port = 4532;

        var portIndex = hostname.LastIndexOf(':');
        if (portIndex >= 0)
        {
            var portStr = hostname.Substring(portIndex + 1);

            if (!int.TryParse(portStr, out port))
            {
                throw new ArgumentException($"Value '{portStr}' is not a valid port number");
            }

            hostname = hostname.Substring(0, portIndex);
        }

        if (!IPAddress.TryParse(hostname, out var ipAddress))
        {
            try
            {
#if NET6_0_OR_GREATER
                var dnsTask = Dns.GetHostAddressesAsync(hostname, cancellationToken);
#else
                var dnsTask = Dns.GetHostAddressesAsync(hostname);
#endif

                ipAddress = (await dnsTask.ConfigureAwait(false)).FirstOrDefault() ??
                    throw new Exception($"No IP address found for host '{hostname}'");
            }
            catch (Exception ex)
            {
                throw new Exception($"Host '{hostname}' lookup failed: {ex}");
            }
        }

        IPEndPoint = new IPEndPoint(ipAddress, port);

        return IPEndPoint;
    }
}
