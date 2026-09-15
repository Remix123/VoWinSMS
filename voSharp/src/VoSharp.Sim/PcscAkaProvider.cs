using VoSharp.Common.Aka;
using VoSharp.Sim.Pcsc;

namespace VoSharp.Sim;

/// <summary>Answers USIM AKA challenges through a PC/SC smartcard reader.</summary>
public sealed class PcscAkaProvider : IAkaProvider, IDisposable
{
    /// <summary>USIM application AID (3GPP TS 31.102).</summary>
    private const string UsimAid = "A0000000871002";

    private readonly PcscReader _reader;
    private bool _disposed;

    public PcscAkaProvider(string? readerName = null)
    {
        _reader = new PcscReader();

        if (!_reader.Initialize())
            throw new InvalidOperationException(
                "Could not establish a PC/SC context. Is the Smart Card service running?");

        var readers = _reader.ListReaders();
        if (readers.Length == 0)
            throw new InvalidOperationException("No PC/SC readers found.");

        var target = string.IsNullOrWhiteSpace(readerName) ? readers[0] : readerName;
        if (!_reader.Connect(target))
            throw new InvalidOperationException($"Could not connect to PC/SC reader '{target}'.");

        ReaderName = target;
    }

    public string ReaderName { get; }

    /// <summary>Reads EF.ICCID directly from the UICC, without relying on a modem cache.</summary>
    public Task<string> ReadIccidAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SelectFile("3F00");
        SelectFile("2FE2");
        var data = ReadBinary(10);
        var iccid = DecodeBcd(data);
        if (iccid.Length is < 18 or > 22)
            throw new InvalidOperationException("EF.ICCID returned an invalid value.");
        return Task.FromResult(iccid);
    }

    /// <summary>Reads EF.IMSI from ADF.USIM. This is the identity used by EAP-AKA.</summary>
    public Task<string> ReadImsiAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SelectUsimApplication();
        SelectFile("6F07");
        var data = ReadBinary(9);
        if (data.Length < 2)
            throw new InvalidOperationException("EF.IMSI returned no data.");

        // The first octet is the IMSI length in BCD octets. The remaining digits
        // use the same low-nibble-first encoding as EF.ICCID.
        var byteCount = Math.Min(data[0], data.Length - 1);
        var imsi = DecodeBcd(data.AsSpan(1, byteCount));
        if (imsi.Length is < 5 or > 16)
            throw new InvalidOperationException("EF.IMSI returned an invalid value.");
        return Task.FromResult(imsi);
    }

    public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default)
    {
        try
        {
            SelectUsimApplication();
            // In a PC/SC reader a physical card can be exchanged without any
            // modem URC. When a caller has a live identity, compare EF.ICCID so
            // an EAP-AKA attempt can never silently use the replacement card.
            if (!string.IsNullOrWhiteSpace(expectedIccid))
            {
                var actualIccid = ReadIccidAsync(ct).GetAwaiter().GetResult();
                if (!string.Equals(NormalizeDigits(actualIccid), NormalizeDigits(expectedIccid), StringComparison.Ordinal))
                    return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        SelectUsimApplication();

        var apdu = HardwareAka.BuildAuthenticateApdu(challenge.Rand, challenge.Autn);
        var response = HardwareAka.TransmitWithChaining(_reader.TransmitApdu, apdu, cla: 0x00);

        if (response is null || response.Length == 0)
            return Task.FromResult(AkaResult.Failed("Card returned no APDU response for USIM AUTHENTICATE."));

        return Task.FromResult(HardwareAka.ParseAuthenticateResponse(response).ToAkaResult());
    }

    /// <summary>
    /// SELECT by ADF.USIM AID. Most UICCs default to a different application, and issuing
    /// AUTHENTICATE against the wrong one yields the misleading status word 0x9862.
    /// </summary>
    private void SelectUsimApplication()
    {
        var aid = Convert.FromHexString(UsimAid);

        var select = new byte[5 + aid.Length + 1];
        select[0] = 0x00;                  // CLA
        select[1] = 0xA4;                  // INS: SELECT
        select[2] = 0x04;                  // P1: select by name
        select[3] = 0x04;                  // P2: first/only occurrence, return FCI
        select[4] = (byte)aid.Length;      // Lc
        aid.CopyTo(select, 5);
        select[^1] = 0x00;                 // Le

        var response = _reader.TransmitApdu(select);
        if (response is null || response.Length < 2)
            throw new InvalidOperationException("No response while selecting ADF.USIM.");

        var sw = (ushort)((response[^2] << 8) | response[^1]);
        if (sw != 0x9000 && response[^2] != 0x61 && response[^2] != 0x9F)
            throw new InvalidOperationException(
                $"Selecting ADF.USIM failed (SW=0x{sw:X4}). Is a USIM inserted?");
    }

    private void SelectFile(string fileId)
    {
        var file = Convert.FromHexString(fileId);
        var response = _reader.TransmitApdu([0x00, 0xA4, 0x00, 0x04, 0x02, file[0], file[1]]);
        EnsureSuccess(response, $"selecting EF {fileId}");
    }

    private byte[] ReadBinary(byte length)
    {
        var response = _reader.TransmitApdu([0x00, 0xB0, 0x00, 0x00, length]);
        EnsureSuccess(response, "reading transparent EF");
        return response![..^2];
    }

    private static void EnsureSuccess(byte[]? response, string operation)
    {
        if (response is null || response.Length < 2)
            throw new InvalidOperationException($"No APDU response while {operation}.");
        var sw = (ushort)((response[^2] << 8) | response[^1]);
        if (sw != 0x9000)
            throw new InvalidOperationException($"PC/SC card rejected {operation} (SW=0x{sw:X4}).");
    }

    private static string DecodeBcd(ReadOnlySpan<byte> bytes)
    {
        var digits = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            AppendBcd(value & 0x0F, digits);
            AppendBcd(value >> 4, digits);
        }
        return digits.ToString();

        static void AppendBcd(int nibble, System.Text.StringBuilder output)
        {
            if (nibble is >= 0 and <= 9) output.Append((char)('0' + nibble));
            // 0xF is the normal padding nibble; other values are not digits.
        }
    }

    private static string NormalizeDigits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
    }
}
