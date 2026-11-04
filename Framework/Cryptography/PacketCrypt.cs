/*
 * Copyright (C) 2012-2020 CypherCore <http://github.com/CypherCore>
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Framework.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Framework.Cryptography;

public sealed class WorldCrypt : IDisposable
{
    private const int TagSizeInBytes = 12; // 96-bit tag for WoW protocol
    private const int TagSizeInBits = TagSizeInBytes * 8;
    private const uint ServerNonceSuffix = 0x52565253; // "SRVR"
    private const uint ClientNonceSuffix = 0x544E4C43; // "CLNT"

    // Test/benchmark hook: forces the BouncyCastle path even when platform AesGcm would work.
    // Production code never sets this — gated through InternalsVisibleTo.
    internal static bool ForceBouncyCastleForTests;

    public void Initialize(ReadOnlySpan<byte> key)
    {
        if (IsInitialized)
            throw new InvalidOperationException("PacketCrypt already initialized!");

        if (!ForceBouncyCastleForTests && TryInitializeNative(key))
        {
            _useBouncyCastle = false;
            Log.Print(LogType.Server, "WorldCrypt: using platform AesGcm");
        }
        else
        {
            InitializeBouncyCastle(key);
            _useBouncyCastle = true;
            Log.Print(LogType.Server, "WorldCrypt: using BouncyCastle GcmBlockCipher (platform AesGcm rejected 12-byte tag or is unavailable)");
        }

        IsInitialized = true;
    }

    private bool TryInitializeNative(ReadOnlySpan<byte> key)
    {
        try
        {
            _serverEncrypt = new AesGcm(key, TagSizeInBytes);
            _clientDecrypt = new AesGcm(key, TagSizeInBytes);
            return true;
        }
        catch (ArgumentException)
        {
            // Tag size unsupported by the platform's AesGcm provider (e.g. macOS CommonCrypto).
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            // No AES-GCM provider at all.
            return false;
        }
    }

    private void InitializeBouncyCastle(ReadOnlySpan<byte> key)
    {
        // BC's KeyParameter ctor only accepts byte[]. Materialize once at session start.
        _bcKey = new KeyParameter(key.ToArray());
        _bcServerEncrypt = new GcmBlockCipher(new AesEngine());
        _bcClientDecrypt = new GcmBlockCipher(new AesEngine());
    }

    public bool Encrypt(Span<byte> data, Span<byte> tag)
    {
        if (!IsInitialized)
        {
            ++_serverCounter;
            return true;
        }

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, _serverCounter);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], ServerNonceSuffix);

        bool ok = _useBouncyCastle
            ? EncryptBc(_bcServerEncrypt, nonce, data, tag)
            : EncryptNative(_serverEncrypt!, nonce, data, tag);

        ++_serverCounter;
        return ok;
    }

    public bool Decrypt(Span<byte> data, Span<byte> tag)
    {
        if (!IsInitialized)
        {
            ++_clientCounter;
            return true;
        }

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, _clientCounter);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], ClientNonceSuffix);

        bool ok = _useBouncyCastle
            ? DecryptBc(_bcClientDecrypt, nonce, data, tag)
            : DecryptNative(_clientDecrypt!, nonce, data, tag);

        ++_clientCounter;
        return ok;
    }

    private static bool EncryptNative(AesGcm cipher, ReadOnlySpan<byte> nonce, Span<byte> data, Span<byte> tag)
    {
        try
        {
            cipher.Encrypt(nonce, data, data, tag);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool DecryptNative(AesGcm cipher, ReadOnlySpan<byte> nonce, Span<byte> data, Span<byte> tag)
    {
        try
        {
            cipher.Decrypt(nonce, data, tag, data);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private bool EncryptBc(GcmBlockCipher cipher, ReadOnlySpan<byte> nonce, Span<byte> data, Span<byte> tag)
    {
        // BC contract: Init must be called before each message; reusing the cipher instance is safe.
        // AeadParameters takes the nonce as byte[], so a 12-byte allocation per packet is unavoidable.
        cipher.Init(forEncryption: true, new AeadParameters(_bcKey, TagSizeInBits, nonce.ToArray()));

        int dataLen = data.Length;
        int outLen = cipher.GetOutputSize(dataLen); // = dataLen + TagSizeInBytes
        byte[] inBuf = ArrayPool<byte>.Shared.Rent(dataLen);
        byte[] outBuf = ArrayPool<byte>.Shared.Rent(outLen);
        try
        {
            data.CopyTo(inBuf);
            int written = cipher.ProcessBytes(inBuf, 0, dataLen, outBuf, 0);
            cipher.DoFinal(outBuf, written); // appends the 12-byte tag
            outBuf.AsSpan(0, dataLen).CopyTo(data);
            outBuf.AsSpan(dataLen, TagSizeInBytes).CopyTo(tag);
            return true;
        }
        catch (InvalidCipherTextException)
        {
            return false;
        }
        catch (CryptoException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inBuf);
            ArrayPool<byte>.Shared.Return(outBuf);
        }
    }

    private bool DecryptBc(GcmBlockCipher cipher, ReadOnlySpan<byte> nonce, Span<byte> data, Span<byte> tag)
    {
        cipher.Init(forEncryption: false, new AeadParameters(_bcKey, TagSizeInBits, nonce.ToArray()));

        int dataLen = data.Length;
        int inLen = dataLen + TagSizeInBytes;
        byte[] inBuf = ArrayPool<byte>.Shared.Rent(inLen);
        byte[] outBuf = ArrayPool<byte>.Shared.Rent(dataLen);
        try
        {
            data.CopyTo(inBuf);
            tag.CopyTo(inBuf.AsSpan(dataLen));

            int written = cipher.ProcessBytes(inBuf, 0, inLen, outBuf, 0);
            cipher.DoFinal(outBuf, written); // throws InvalidCipherTextException on MAC mismatch
            outBuf.AsSpan(0, dataLen).CopyTo(data);
            return true;
        }
        catch (InvalidCipherTextException)
        {
            return false;
        }
        catch (CryptoException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inBuf);
            ArrayPool<byte>.Shared.Return(outBuf);
        }
    }

    public void Dispose()
    {
        IsInitialized = false;
        _serverEncrypt?.Dispose();
        _clientDecrypt?.Dispose();
        _serverEncrypt = null;
        _clientDecrypt = null;
        // BouncyCastle types do not implement IDisposable.
    }

    public bool IsInitialized { get; set; }
    internal bool UsingBouncyCastle => _useBouncyCastle;

    bool _useBouncyCastle;

    AesGcm? _serverEncrypt;
    AesGcm? _clientDecrypt;

    GcmBlockCipher _bcServerEncrypt = null!;
    GcmBlockCipher _bcClientDecrypt = null!;
    KeyParameter _bcKey = null!;

    ulong _clientCounter;
    ulong _serverCounter;
}
