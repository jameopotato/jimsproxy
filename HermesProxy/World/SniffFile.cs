using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HermesProxy.World;

public sealed class SniffFile
{
    public SniffFile(string fileName, ushort build)
    {
        string dir = "PacketsLog";
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string file = fileName + "_" + build + "_" + Time.UnixTime + ".pkt";
        string path = Path.Combine(dir, file);

        _fileWriter = new BinaryWriter(File.Open(path, FileMode.Create));
        _gameVersion = build;
    }
    BinaryWriter _fileWriter;
    ushort _gameVersion;
    readonly Lock _lock = new();

    public void WriteHeader()
    {
        _fileWriter.Write('P');
        _fileWriter.Write('K');
        _fileWriter.Write('T');
        UInt16 sniffVersion = 0x201;
        _fileWriter.Write(sniffVersion);
        _fileWriter.Write(_gameVersion);

        for (int i = 0; i < 40; i++)
        {
            byte zero = 0;
            _fileWriter.Write(zero);
        }
    }

    public void WritePacket(uint opcode, bool isFromClient, byte[] data)
    {
        lock (_lock)
        {
            //MIRASU: If CloseFile() has already run (concurrent disconnect paths),
            //MIRASU: _fileWriter is null and there's nothing to write — drop the packet
            //MIRASU: silently rather than crashing the session on shutdown.
            if (_fileWriter == null) return; //MIRASU

            byte direction = !isFromClient ? (byte)0xff : (byte)0x0;
            _fileWriter.Write(direction);

            uint unixtime = (uint)Time.UnixTime;
            _fileWriter.Write(unixtime);
            _fileWriter.Write(Environment.TickCount);

            if (isFromClient)
            {
                uint packetSize = (uint)(data.Length - 2 + sizeof(uint));
                _fileWriter.Write(packetSize);
                _fileWriter.Write(opcode);

                for (int i = 2; i < data.Length; i++)
                    _fileWriter.Write(data[i]);
            }
            else
            {
                uint packetSize = (uint)data.Length + sizeof(ushort);
                _fileWriter.Write(packetSize);
                ushort opcode2 = (ushort)opcode;
                _fileWriter.Write(opcode2);
                _fileWriter.Write(data);
            }
        }
    }

    public void CloseFile()
    {
        //MIRASU: Was crashing with NullReferenceException when two disconnect paths
        //MIRASU: (CMSG_LOG_DISCONNECT in WorldSocket + OnDisconnect in GlobalSessionData)
        //MIRASU: both called CloseFile concurrently during 2-box alt-F4. The null check
        //MIRASU: on _fileWriter could pass on thread B while thread A was mid-Flush and
        //MIRASU: about to null the field. Serialize with the same lock WritePacket uses,
        //MIRASU: capture to a local, then null the field — classic swap-and-dispose pattern.
        lock (_lock) //MIRASU
        { //MIRASU
            var writer = _fileWriter; //MIRASU: snapshot under lock
            _fileWriter = null!; //MIRASU: nullify before dispose so concurrent WritePacket fails fast
            if (writer != null && writer.BaseStream != null && writer.BaseStream.CanWrite) //MIRASU
            { //MIRASU
                writer.Flush(); //MIRASU
                writer.Close(); //MIRASU
            } //MIRASU
        } //MIRASU
    }
}
