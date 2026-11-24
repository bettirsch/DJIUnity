using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Rejects stale or incompatible native plugins before any Android player build.</summary>
public sealed class FinalBridgeArtifactGuard : IPreprocessBuildWithReport
{
    internal const string ManifestPath = "assets/roofcanopy-bridge-features.json";
    internal const string Getter = "Java_com_sok9hu_djibridge_DjiBoardVisionNative_isFourTagMultiStartExperimentEnabled";
    public int callbackOrder => -1000;

    [Serializable]
    internal sealed class Manifest
    {
        public int schemaVersion;
        public bool fourTagMultiStart;
        public NativeLibrary[] nativeLibraries;
    }

    [Serializable]
    internal sealed class NativeLibrary
    {
        public string abi;
        public string sha256;
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.Android)
            ValidateCurrentProject();
    }

    internal static void ValidateCurrentProject()
    {
        var persistent = PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Android)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (persistent.Any(symbol => symbol.StartsWith("DJI_", StringComparison.Ordinal)))
            throw new BuildFailedException("DJI capabilities must not be enabled through persistent scripting defines. Use the explicit diagnostic entry point.");
        try
        {
            ValidateAar(Path.Combine(Application.dataPath, "Plugins/Android/DJIUnityBridge.aar"));
        }
        catch (Exception exception) when (exception is not BuildFailedException)
        {
            throw new BuildFailedException("Required multi-start bridge validation failed: " + exception.Message);
        }
    }

    internal static void ValidateAar(string path)
    {
        using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var metadata = archive.GetEntry(ManifestPath) ?? throw new InvalidDataException("Missing bridge feature manifest.");
        Manifest manifest;
        using (var reader = new StreamReader(metadata.Open()))
            manifest = JsonUtility.FromJson<Manifest>(reader.ReadToEnd());
        if (manifest == null || manifest.schemaVersion != 1 || !manifest.fourTagMultiStart ||
            manifest.nativeLibraries == null || manifest.nativeLibraries.Length == 0)
            throw new InvalidDataException("Bridge manifest does not require four-tag multi-start.");
        if (manifest.nativeLibraries.Select(item => item.abi).Distinct().Count() != manifest.nativeLibraries.Length)
            throw new InvalidDataException("Duplicate ABI in bridge manifest.");

        bool arm64 = false;
        foreach (var library in manifest.nativeLibraries)
        {
            if (string.IsNullOrEmpty(library.abi) || library.abi.Contains('/') || library.abi.Contains('\\'))
                throw new InvalidDataException("Invalid bridge ABI.");
            var entry = archive.GetEntry($"jni/{library.abi}/libdjiunity.so") ??
                        throw new InvalidDataException("Missing native library for " + library.abi);
            byte[] bytes;
            using (var input = entry.Open())
            using (var buffer = new MemoryStream())
            {
                input.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            using var hash = SHA256.Create();
            var actual = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty);
            if (!string.Equals(actual, library.sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native library hash mismatch for " + library.abi);
            if (library.abi == "arm64-v8a")
            {
                ValidateArm64Getter(bytes);
                arm64 = true;
            }
        }
        if (!arm64)
            throw new InvalidDataException("Required ARM64 native library is missing.");
    }

    // This release pins the current ARM64 toolchain. Unknown getter code fails closed,
    // rather than trusting an asset declaration about an opaque native binary.
    internal static void ValidateArm64Getter(byte[] elf)
    {
        if (elf.Length < 64 || elf[0] != 0x7f || elf[1] != (byte)'E' || elf[2] != (byte)'L' ||
            elf[3] != (byte)'F' || elf[4] != 2 || elf[5] != 1 || U16(elf, 18) != 183)
            throw new InvalidDataException("Expected a little-endian ARM64 ELF library.");
        long sections = checked((long)U64(elf, 40));
        int size = U16(elf, 58), count = U16(elf, 60);
        if (size < 64 || count == 0)
            throw new InvalidDataException("Missing ELF section table.");
        int matches = 0;
        for (int section = 0; section < count; section++)
        {
            long header = sections + (long)section * size;
            if (U32(elf, header + 4) != 11) continue; // SHT_DYNSYM
            long symbols = checked((long)U64(elf, header + 24));
            long symbolBytes = checked((long)U64(elf, header + 32));
            long stride = checked((long)U64(elf, header + 56));
            uint stringSection = U32(elf, header + 40);
            if (stride < 24 || stringSection >= count) throw new InvalidDataException("Invalid ELF symbols.");
            long strings = checked((long)U64(elf, sections + stringSection * size + 24));
            for (long index = 0; index < symbolBytes; index += stride)
            {
                long symbol = symbols + index;
                long nameOffset = strings + U32(elf, symbol);
                if (CString(elf, nameOffset) != Getter) continue;
                byte info = elf[Offset(elf, symbol + 4, 1)];
                byte visibility = (byte)(elf[Offset(elf, symbol + 5, 1)] & 3);
                int binding = info >> 4;
                if ((info & 15) != 2 || (binding != 1 && binding != 2) ||
                    (visibility != 0 && visibility != 3) || U64(elf, symbol + 16) != 8)
                    throw new InvalidDataException("Multi-start getter is not an exported eight-byte function.");
                ushort owner = U16(elf, symbol + 6);
                if (owner == 0 || owner >= count) throw new InvalidDataException("Undefined multi-start getter.");
                long ownerHeader = sections + (long)owner * size;
                if (U32(elf, ownerHeader + 4) != 1 || (U64(elf, ownerHeader + 8) & 6) != 6)
                    throw new InvalidDataException("Multi-start getter is not in allocated executable code.");
                ulong address = U64(elf, symbol + 8), sectionAddress = U64(elf, ownerHeader + 16);
                if (address < sectionAddress) throw new InvalidDataException("Invalid getter address.");
                ulong codeSize = U64(elf, ownerHeader + 32);
                if (codeSize < 8 || address - sectionAddress > codeSize - 8)
                    throw new InvalidDataException("Multi-start getter extends beyond its code section.");
                long offset = checked((long)(U64(elf, ownerHeader + 24) + address - sectionAddress));
                if (U32(elf, offset) != 0x52800020 || U32(elf, offset + 4) != 0xd65f03c0)
                    throw new InvalidDataException("Native multi-start getter is not the required constant ON implementation.");
                matches++;
            }
        }
        if (matches != 1) throw new InvalidDataException("Required native multi-start getter not uniquely exported.");
    }

    private static int Offset(byte[] data, long offset, int length)
    {
        if (offset < 0 || offset > data.LongLength - length)
            throw new InvalidDataException("Truncated ELF data.");
        return checked((int)offset);
    }
    private static ushort U16(byte[] bytes, long offset) => BitConverter.ToUInt16(bytes, Offset(bytes, offset, 2));
    private static uint U32(byte[] bytes, long offset) => BitConverter.ToUInt32(bytes, Offset(bytes, offset, 4));
    private static ulong U64(byte[] bytes, long offset) => BitConverter.ToUInt64(bytes, Offset(bytes, offset, 8));
    private static string CString(byte[] bytes, long offset)
    {
        int start = Offset(bytes, offset, 1), end = start;
        while (end < bytes.Length && bytes[end] != 0 && end - start <= 512) end++;
        if (end == bytes.Length || end - start > 512) throw new InvalidDataException("Invalid ELF symbol name.");
        return Encoding.ASCII.GetString(bytes, start, end - start);
    }
}
