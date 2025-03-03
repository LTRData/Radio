using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if !NETCOREAPP && !NETSTANDARD2_1_OR_GREATER
using DiscUtils.Streams.Compatibility;
#endif
using LTRData.Extensions.IO;
using LTRData.Extensions.Async;

namespace LTRData.RigCtl;

/// <summary>
/// Represents a connection to a radio.
/// </summary>
public class RadioConnection : StreamReader
{
    #region STATIC
    private static readonly byte[] get_tx_command = "t"u8.ToArray();
    private static readonly byte[] set_tx_on_command = "T1\n"u8.ToArray();
    private static readonly byte[] set_tx_off_command = "T0\n"u8.ToArray();
    private static readonly byte[] get_freq_command = "f"u8.ToArray();
    private static readonly byte[] set_freq_command = "F"u8.ToArray();
    private static readonly byte[] set_membank_command = "B"u8.ToArray();
    private static readonly byte[] set_memch_command = "E"u8.ToArray();
    private static readonly byte[] get_mode_command = "m"u8.ToArray();
    private static readonly byte[] set_mode_command = "M"u8.ToArray();
    private static readonly byte[] get_tx_tone_command = "c"u8.ToArray();
    private static readonly byte[] get_level_command = "l"u8.ToArray();
    private static readonly byte[] set_level_command = "L"u8.ToArray();
    private static readonly byte[] set_vfo_command = "V"u8.ToArray();
    private static readonly byte[] set_rptr_shift_command = "R"u8.ToArray();
    private static readonly byte[] _byteArrayZeroChar = "0"u8.ToArray();
    private static readonly byte[] _byteArrayQuestionChar = "?"u8.ToArray();
    #endregion

    private readonly SemaphoreSlim sync = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Value returned by radio connection to indicate success.
    /// </summary>
    public const string ResultOK = "RPRT 0";

    /// <summary>
    /// Values for repeater shift settings
    /// </summary>
    public enum RepeaterShift : byte
    {
        /// <summary>
        /// No repeater shift
        /// </summary>
        Off = (byte)'0',

        /// <summary>
        /// Repeater shift down
        /// </summary>
        Minus = (byte)'-',

        /// <summary>
        /// Repeater shift up
        /// </summary>
        Plus = (byte)'+'
    }

    /// <summary>
    /// Values for step direction
    /// </summary>
    public enum StepDirection
    {
        /// <summary>
        /// Frequency value is absolute
        /// </summary>
        None,

        /// <summary>
        /// Frequency value is added to current frequency
        /// </summary>
        Up,

        /// <summary>
        /// Frequency value is subtracted from current frequency
        /// </summary>
        Down
    }

#if NET9_0_OR_GREATER
    private static readonly Lock _syncobj = new();
#else
    private static readonly object _syncobj = new();
#endif

    /// <summary>
    /// The <see cref="NetworkStream"/> object used for communication with the radio.
    /// </summary>
    public NetworkStream? NetworkStream => BaseStream as NetworkStream;

    /// <summary>
    /// The <see cref="Socket"/> used for communication with the radio.
    /// </summary>
    public Socket? Socket => (BaseStream as RadioConnectionNetworkStream)?.Socket;

    /// <summary>
    /// Returns whether radio connection is active or not.
    /// </summary>
    public bool IsConnected => BaseStream is RadioConnectionNetworkStream stream
        && !stream.IsDisposed && stream.Socket is not null && stream.Socket.Connected;

    private sealed class RadioConnectionNetworkStream(Socket socket) : NetworkStream(socket)
    {
        public new Socket Socket => base.Socket;

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;

            base.Dispose(disposing);
        }
    }

    private RadioConnection(Socket socket)
        : base(new RadioConnectionNetworkStream(socket))
    {
    }

    private static string[]? twoElementStringBuffer;

    /// <summary>
    /// Closes the underlying stream, releases the unmanaged resources used by the System.IO.StreamReader,
    /// and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged
    /// resources.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            sync.Dispose();
        }

        base.Dispose(disposing);
    }

    private static async ValueTask<Socket> ConnectRigCtlAsync(IPEndPoint address, CancellationToken cancellationToken)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = false,
            NoDelay = true
        };

#if NET6_0_OR_GREATER
        await s.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
#else
        var connectTask = s.ConnectAsync(address);
        if (cancellationToken.CanBeCanceled)
        {
            await connectTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await connectTask.ConfigureAwait(false);
        }
#endif

        return s;
    }

    /// <summary>
    /// Creates a new <see cref="RadioConnection"/> instance and asynchronously connects it to
    /// the radio at specified IP address.
    /// </summary>
    /// <param name="address">IP address of listening rigctld daemon</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async ValueTask<RadioConnection> ConnectAsync(IPEndPoint address, CancellationToken cancellationToken)
    {
        var connection = await ConnectRigCtlAsync(address, cancellationToken).ConfigureAwait(false);
        return new(connection);
    }

    /// <summary>
    /// Returns string value "1" if radio is currently transmitting.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public ValueTask<string?> GetTxAsync(CancellationToken cancellationToken) => SendCommandAsync(get_tx_command, cancellationToken);

    /// <summary>
    /// Gets a meter level, such as "STRENGTH" for incoming signal strength.
    /// </summary>
    /// <param name="v">Name of meter, such as STRENGTH, SWR or ALC</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async ValueTask<string?> GetLevelAsync(string v, CancellationToken cancellationToken)
    {
        var result = await SendCommandAsync(get_level_command, Encoding.ASCII.GetBytes(v), cancellationToken).ConfigureAwait(false);

        if (decimal.TryParse(result, NumberStyles.Any, NumberFormatInfo.InvariantInfo, out var value))
        {
            result = value.ToString("0.##", NumberFormatInfo.InvariantInfo);
        }

        return result;
    }

    /// <summary>
    /// Creates a new <see cref="RadioConnection"/> instance and synchronously connects it to
    /// the radio at specified IP address.
    /// </summary>
    /// <param name="address">IP address of listening rigctld daemon</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public RadioConnection(IPEndPoint address, CancellationToken cancellationToken)
        : this(ConnectRigCtlAsync(address, cancellationToken).WaitForResult())
    {
    }

    /// <summary>
    /// Sends a command to the radio and puts returned strings in supplied <see cref="IList{String}"/>.
    /// </summary>
    /// <param name="resultLines">List to hold strings returned from radio.</param>
    /// <param name="cmd">Command to send to radio</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async ValueTask SendCommandAsync(IList<string?> resultLines, ReadOnlyMemory<byte> cmd, CancellationToken cancellationToken)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BaseStream.WriteAsync(cmd, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < resultLines.Count; i++)
            {
                resultLines[i] = await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            sync.Release();
        }
    }

    /// <summary>
    /// Sends a command to the radio and returns a single result string from the radio.
    /// </summary>
    /// <param name="cmd">Command to send to radio</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Single string returned from radio</returns>
    public async ValueTask<string?> SendCommandAsync(ReadOnlyMemory<byte> cmd, CancellationToken cancellationToken)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BaseStream.WriteAsync(cmd, cancellationToken).ConfigureAwait(false);
            return await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            sync.Release();
        }
    }

    /// <summary>
    /// Sends a command with a single parameter to the radio and returns a single result string from the radio.
    /// </summary>
    /// <param name="cmd">Command to send to radio</param>
    /// <param name="parameter">Parameter to send to the radio along with supplied command</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Single string returned from radio</returns>
    public async ValueTask<string?> SendCommandAsync(ReadOnlyMemory<byte> cmd, ReadOnlyMemory<byte> parameter, CancellationToken cancellationToken)
    {
        var bufferLength = cmd.Length + 1 + parameter.Length + 1;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferLength);
        try
        {
            cmd.Span.CopyTo(buffer);

            var i = cmd.Length;

            buffer[i] = (byte)' ';
            i++;

            parameter.Span.CopyTo(buffer.AsSpan(i));
            i += parameter.Length;
            buffer[i] = (byte)'\n';
            i++;

            await sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await BaseStream.WriteAsync(buffer.AsMemory(0, i), cancellationToken).ConfigureAwait(false);
                return await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                sync.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Sends a command with a parameters to the radio and returns a single result string from the radio.
    /// </summary>
    /// <param name="cmd">Command to send to radio</param>
    /// <param name="parameters">Parameters to send to the radio along with supplied command</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Single string returned from radio</returns>
    public async ValueTask<string?> SendCommandAsync(ReadOnlyMemory<byte> cmd, CancellationToken cancellationToken, params ReadOnlyMemory<byte>[] parameters)
    {
        var bufferLength = cmd.Length + 1 + parameters.Sum(parameter => parameter.Length + 1);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferLength);
        try
        {
            cmd.Span.CopyTo(buffer);

            var i = cmd.Length;

            buffer[i] = (byte)' ';
            i++;

            for (var p = 0; p < parameters.Length; p++)
            {
                parameters[p].Span.CopyTo(buffer.AsSpan(i));
                i += parameters[p].Length;
                buffer[i] = (byte)'\n';
                i++;
            }

            await sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await BaseStream.WriteAsync(buffer.AsMemory(0, i), cancellationToken).ConfigureAwait(false);
                return await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                sync.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Sends a command with a parameters to the radio and returns a result strings from the radio in a supplied <see cref="IList{String}"/>.
    /// </summary>
    /// <param name="resultLines">List to hold strings returned from radio.</param>
    /// <param name="cmd">Command to send to radio</param>
    /// <param name="parameters">Parameters to send to the radio along with supplied command</param>
    /// <param name="cancellationToken"></param>
    public async ValueTask SendCommandAsync(IList<string?> resultLines, ReadOnlyMemory<byte> cmd, ReadOnlyMemory<byte> parameters, CancellationToken cancellationToken)
    {
        var bufferLength = cmd.Length + 1 + parameters.Length + 1;

        var buffer = ArrayPool<byte>.Shared.Rent(bufferLength);

        try
        {
            cmd.Span.CopyTo(buffer);

            var i = cmd.Length;
            buffer[i] = (byte)' ';
            i++;

            parameters.Span.CopyTo(buffer.AsSpan(i));

            i += parameters.Length;
            buffer[i] = (byte)'\n';
            i++;

            try
            {
                await sync.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await BaseStream.WriteAsync(buffer.AsMemory(0, i), cancellationToken).ConfigureAwait(false);

                    for (var r = 0; r < resultLines.Count; r++)
                    {
                        resultLines[r] = await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    sync.Release();
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Set frequency.
    /// </summary>
    /// <param name="freq">Frequency to set in Hz</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from radio</returns>
    public ValueTask<string?> SetFrequencyAsync(int freq, CancellationToken cancellationToken) =>
        SendCommandAsync(set_freq_command, Encoding.ASCII.GetBytes(freq.ToString("0", NumberFormatInfo.InvariantInfo)), cancellationToken);

    /// <summary>
    /// Set memory bank.
    /// </summary>
    /// <param name="membank">Memory bank to set</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from radio</returns>
    public ValueTask<string?> SetMemoryBankAsync(int membank, CancellationToken cancellationToken) =>
        SendCommandAsync(set_membank_command, Encoding.ASCII.GetBytes(membank.ToString("0", NumberFormatInfo.InvariantInfo)), cancellationToken);

    /// <summary>
    /// Set memory channel.
    /// </summary>
    /// <param name="memchannel">Memory channel to set</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from radio</returns>
    public ValueTask<string?> SetMemoryChannelAsync(int memchannel, CancellationToken cancellationToken) =>
        SendCommandAsync(set_memch_command, Encoding.ASCII.GetBytes(memchannel.ToString("0", NumberFormatInfo.InvariantInfo)), cancellationToken);

    /// <summary>
    /// Get modes available with the connected radio.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Space separated list of supported modes and a result string from the radio</returns>
    public async ValueTask<(string Modes, string Result)> GetAvailableModesAsync(CancellationToken cancellationToken)
    {
        var result = Interlocked.Exchange(ref twoElementStringBuffer, null) ?? GetArray<string>(length: 2);

        try
        {
            await SendCommandAsync(result, set_mode_command, _byteArrayQuestionChar, cancellationToken).ConfigureAwait(false);
            return (Modes: result[0], Result: result[1]);
        }
        finally
        {
            Array.Clear(result, 0, result.Length);
            Interlocked.Exchange(ref twoElementStringBuffer, result);
        }
    }

#if NET5_0_OR_GREATER
    private static T[] GetArray<T>(int length) => GC.AllocateUninitializedArray<T>(length);
#else
    private static T[] GetArray<T>(int length) => new T[length];
#endif

    /// <summary>
    /// Get levels available for adjustments in the connected radio.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Space separated list of supported levels and a result string from the radio</returns>
    public async ValueTask<(string Levels, string Result)> GetAvailableLevelsAsync(CancellationToken cancellationToken)
    {
        var result = Interlocked.Exchange(ref twoElementStringBuffer, null) ?? GetArray<string>(length: 2);

        try
        {
            await SendCommandAsync(result, set_level_command, _byteArrayQuestionChar, cancellationToken).ConfigureAwait(false);
            return (Levels: result[0], Result: result[1]);
        }
        finally
        {
            Array.Clear(result, 0, result.Length);
            Interlocked.Exchange(ref twoElementStringBuffer, result);
        }
    }

    /// <summary>
    /// Sets transmission mode on or off.
    /// </summary>
    /// <param name="on">Set to true to activate transmission mode, false to activate receive mode.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio</returns>
    public ValueTask<string?> SetTxAsync(bool on, CancellationToken cancellationToken) =>
        SendCommandAsync(on ? set_tx_on_command : set_tx_off_command, cancellationToken);

    /// <summary>
    /// Gets curremt frequency from radio.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio in Hz</returns>
    public ValueTask<string?> GetFrequencyAsync(CancellationToken cancellationToken) =>
        SendCommandAsync(get_freq_command, cancellationToken);

    /// <summary>
    /// Gets curremt mode and passband width from radio.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Result from the radio split into two string values</returns>
    public async ValueTask<(string Mode, string Passband)> GetModeAndWidthAsync(CancellationToken cancellationToken)
    {
        var result = Interlocked.Exchange(ref twoElementStringBuffer, null) ?? GetArray<string>(2);

        try
        {
            await SendCommandAsync(result, get_mode_command, cancellationToken).ConfigureAwait(false);
            return (Mode: result[0], Passband: result[1]);
        }
        finally
        {
            Array.Clear(result, 0, result.Length);
            Interlocked.Exchange(ref twoElementStringBuffer, result);
        }
    }

    /// <summary>
    /// Gets transmission subtone frequency from radio.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio</returns>
    public ValueTask<string?> GetTxToneAsync(CancellationToken cancellationToken)
        => SendCommandAsync(get_tx_tone_command, cancellationToken);

    /// <summary>
    /// Sets current mode and passband width in radio.
    /// </summary>
    /// <param name="mode">Mode to set in radio</param>
    /// <param name="passband">Passband width to set in radio</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio</returns>
    public ValueTask<string?> SetModeAsync(string mode, int passband, CancellationToken cancellationToken)
        => SendCommandAsync(set_mode_command, cancellationToken, Encoding.ASCII.GetBytes(mode), Encoding.ASCII.GetBytes(passband.ToString("0", NumberFormatInfo.InvariantInfo)));

    /// <summary>
    /// Sets current mode in radio.
    /// </summary>
    /// <param name="mode">Mode to set in radio</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio</returns>
    public ValueTask<string?> SetModeAsync(string mode, CancellationToken cancellationToken)
        => SendCommandAsync(set_mode_command, cancellationToken, Encoding.ASCII.GetBytes(mode), _byteArrayZeroChar);

    /// <summary>
    /// Set current VFO, usually VFOA, VFOB or MEM.
    /// </summary>
    /// <param name="vfo">Usually VFOA, VFOB or MEM to select current VFO or memory mode</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio</returns>
    public ValueTask<string?> SetVFOAsync(string vfo, CancellationToken cancellationToken)
        => SendCommandAsync(set_vfo_command, Encoding.ASCII.GetBytes(vfo), cancellationToken);

    /// <summary>
    /// Sets a configurable level at the radio, for example AF.
    /// </summary>
    /// <param name="level">Level to set. Usually AF, SQL and similar</param>
    /// <param name="levelvalue">Level value to set</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio</returns>
    public ValueTask<string?> SetLevelAsync(string level, decimal levelvalue, CancellationToken cancellationToken)
        => SendCommandAsync(set_level_command, cancellationToken, Encoding.ASCII.GetBytes(level), Encoding.ASCII.GetBytes(levelvalue.ToString("", NumberFormatInfo.InvariantInfo)));

    /// <summary>
    /// Set repeater shift in the radio.
    /// </summary>
    /// <param name="shift">Shift mode to set.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Result string from the radio</returns>
    public ValueTask<string?> SetRepaterShiftAsync(RepeaterShift shift, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            buffer[0] = (byte)shift;
            return SendCommandAsync(set_rptr_shift_command, buffer.AsMemory(0, 1), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
