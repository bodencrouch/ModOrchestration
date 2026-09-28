// Copyright 2021-2025 ModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ModSync.Tests.Fixtures
{
    /// <summary>
    /// Writers for the BioWare Odyssey container formats the mock KOTOR install needs.
    /// <para>
    /// Every byte produced here is synthesised from the format layout - no game asset is
    /// read, copied or embedded. The layouts mirror the writers that already live in this
    /// repository so the output parses with the same readers:
    /// TLK V3.0 (<c>src/HoloPatcher/Formats/TLK/TLK/TLKBinaryWriter.cs</c>),
    /// ERF/MOD V1.0 (<c>.../ERF/ERFBinaryWriter.cs</c>),
    /// RIM V1.0 (<c>.../RIM/RIMBinaryWriter.cs</c>) and
    /// 2DA V2.b (<c>.../TwoDA/TwoDABinaryWriter.cs</c>).
    /// KEY V1 and BIFF V1 have no writer in-tree; their layouts are the documented Odyssey
    /// ones and were confirmed against the header of a retail <c>chitin.key</c>/<c>party.bif</c>
    /// (offsets only - no content was copied).
    /// </para>
    /// <para>
    /// Output is deterministic: no timestamps, no random ids. The same arguments always
    /// produce the same bytes, so CI can assert on hashes.
    /// </para>
    /// </summary>
    public static class MockKotorBinaries
    {
        /// <summary>Fixed build stamp so generated containers are byte-reproducible.</summary>
        private const uint BuildYear = 103;
        private const uint BuildDay = 309;

        private static readonly Encoding Ascii = Encoding.ASCII;

        /// <summary>
        /// Writes a TLK V3.0 talk table containing <paramref name="strings"/>.
        /// Header is 20 bytes, each entry header is 40 bytes, text data follows.
        /// </summary>
        public static void WriteTlk(string path, IReadOnlyList<string> strings)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (strings is null)
            {
                throw new ArgumentNullException(nameof(strings));
            }

            const int FileHeaderSize = 20;
            const int EntrySize = 40;

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes("TLK "));
                w.Write(Ascii.GetBytes("V3.0"));
                w.Write((uint)0); // LanguageId: English
                w.Write((uint)strings.Count);
                w.Write((uint)(FileHeaderSize + (strings.Count * EntrySize))); // offset to text data

                int textOffset = 0;
                var encoded = new List<byte[]>(strings.Count);
                foreach (string s in strings)
                {
                    byte[] bytes = Ascii.GetBytes(s ?? string.Empty);
                    encoded.Add(bytes);

                    w.Write((uint)0x0001);       // flags: TEXT_PRESENT
                    w.Write(new byte[16]);       // sound ResRef (unused)
                    w.Write((uint)0);            // volume variance
                    w.Write((uint)0);            // pitch variance
                    w.Write((uint)textOffset);
                    w.Write((uint)bytes.Length);
                    w.Write((uint)0);            // sound length

                    textOffset += bytes.Length;
                }

                foreach (byte[] bytes in encoded)
                {
                    w.Write(bytes);
                }

                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>
        /// Writes a BIFF V1 archive with zero resources (20-byte header only).
        /// The mock install ships empty BIFs on purpose: real BIF payloads are game data.
        /// </summary>
        public static void WriteEmptyBif(string path)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes("BIFF"));
                w.Write(Ascii.GetBytes("V1  "));
                w.Write((uint)0);   // variable resource count
                w.Write((uint)0);   // fixed resource count
                w.Write((uint)20);  // offset to variable resource table

                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>
        /// Writes a KEY V1 index referencing <paramref name="bifRelativePaths"/> and containing
        /// no resource keys. Header is 64 bytes, then the file table (12 bytes per BIF), then the
        /// null-terminated filename block, then the (empty) key table.
        /// </summary>
        public static void WriteKey(string path, IReadOnlyList<string> bifRelativePaths, IReadOnlyList<long> bifSizes)
        {
            if (bifRelativePaths is null)
            {
                throw new ArgumentNullException(nameof(bifRelativePaths));
            }

            if (bifSizes is null)
            {
                throw new ArgumentNullException(nameof(bifSizes));
            }

            if (bifRelativePaths.Count != bifSizes.Count)
            {
                throw new ArgumentException("A size must be supplied for every BIF.", nameof(bifSizes));
            }

            const uint HeaderSize = 64;
            const uint FileTableEntrySize = 12;

            uint bifCount = (uint)bifRelativePaths.Count;
            uint offsetToFileTable = HeaderSize;
            uint filenameBlockStart = offsetToFileTable + (bifCount * FileTableEntrySize);

            // KEY stores BIF paths relative to the install root using backslashes.
            var nameBytes = new List<byte[]>(bifRelativePaths.Count);
            foreach (string relative in bifRelativePaths)
            {
                nameBytes.Add(Ascii.GetBytes(relative.Replace('/', '\\') + "\0"));
            }

            uint filenameBlockSize = 0;
            foreach (byte[] n in nameBytes)
            {
                filenameBlockSize += (uint)n.Length;
            }

            uint offsetToKeyTable = filenameBlockStart + filenameBlockSize;

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes("KEY "));
                w.Write(Ascii.GetBytes("V1  "));
                w.Write(bifCount);
                w.Write((uint)0); // key count
                w.Write(offsetToFileTable);
                w.Write(offsetToKeyTable);
                w.Write(BuildYear);
                w.Write(BuildDay);
                w.Write(new byte[32]); // reserved

                uint filenameOffset = filenameBlockStart;
                for (int i = 0; i < bifRelativePaths.Count; i++)
                {
                    w.Write((uint)bifSizes[i]);
                    w.Write(filenameOffset);
                    w.Write((ushort)nameBytes[i].Length);
                    w.Write((ushort)1); // drives bitmask: installed on drive 0
                    filenameOffset += (uint)nameBytes[i].Length;
                }

                foreach (byte[] n in nameBytes)
                {
                    w.Write(n);
                }

                // Key table is empty: the mock ships no BIF-backed resources.
                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>Writes a RIM V1.0 container with no resources (120-byte header only).</summary>
        public static void WriteEmptyRim(string path)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes("RIM "));
                w.Write(Ascii.GetBytes("V1.0"));
                w.Write((uint)0);   // reserved
                w.Write((uint)0);   // entry count
                w.Write((uint)120); // offset to keys
                w.Write(new byte[100]);

                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>
        /// Writes an ERF-family container with no resources (160-byte header only).
        /// <paramref name="fourCc"/> is <c>"MOD "</c> for <c>.mod</c> and <c>"ERF "</c> for <c>.erf</c>.
        /// </summary>
        public static void WriteEmptyErf(string path, string fourCc)
        {
            if (fourCc is null || fourCc.Length != 4)
            {
                throw new ArgumentException("A four-character type tag is required.", nameof(fourCc));
            }

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes(fourCc));
                w.Write(Ascii.GetBytes("V1.0"));
                w.Write((uint)0);   // language count
                w.Write((uint)0);   // localized string size
                w.Write((uint)0);   // entry count
                w.Write((uint)160); // offset to localized strings
                w.Write((uint)160); // offset to key list
                w.Write((uint)160); // offset to resource list
                w.Write(BuildYear);
                w.Write(BuildDay);
                w.Write(0xFFFFFFFF); // description StrRef: none
                w.Write(new byte[116]);

                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>Writes a 2DA V2.b table. Column and row values are ASCII.</summary>
        public static void Write2da(string path, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            if (headers is null)
            {
                throw new ArgumentNullException(nameof(headers));
            }

            if (rows is null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes("2DA "));
                w.Write(Ascii.GetBytes("V2.b"));
                w.Write(Ascii.GetBytes("\n"));

                foreach (string header in headers)
                {
                    w.Write(Ascii.GetBytes(header + "\t"));
                }

                w.Write((byte)0);
                w.Write((uint)rows.Count);

                for (int i = 0; i < rows.Count; i++)
                {
                    w.Write(Ascii.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t"));
                }

                // Cells are stored as offsets into a deduplicated value pool.
                var pool = new List<string>();
                var poolOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
                var cellOffsets = new List<int>();
                int dataSize = 0;

                foreach (IReadOnlyList<string> row in rows)
                {
                    for (int c = 0; c < headers.Count; c++)
                    {
                        string value = (c < row.Count ? row[c] : string.Empty) + "\0";
                        if (!poolOffsets.TryGetValue(value, out int offset))
                        {
                            offset = dataSize;
                            poolOffsets[value] = offset;
                            pool.Add(value);
                            dataSize += value.Length;
                        }

                        cellOffsets.Add(offset);
                    }
                }

                foreach (int offset in cellOffsets)
                {
                    w.Write((ushort)offset);
                }

                w.Write((ushort)dataSize);

                foreach (string value in pool)
                {
                    w.Write(Ascii.GetBytes(value));
                }

                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>Writes a GFF V3.2 file holding a single empty top-level struct.</summary>
        public static void WriteEmptyGff(string path, string fourCc)
        {
            if (fourCc is null || fourCc.Length != 4)
            {
                throw new ArgumentException("A four-character type tag is required.", nameof(fourCc));
            }

            const uint HeaderSize = 56;
            const uint AfterStructs = HeaderSize + 12;

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes(fourCc));
                w.Write(Ascii.GetBytes("V3.2"));
                w.Write(HeaderSize);
                w.Write((uint)1);          // struct count
                w.Write(AfterStructs);     // field offset
                w.Write((uint)0);          // field count
                w.Write(AfterStructs);     // label offset
                w.Write((uint)0);          // label count
                w.Write(AfterStructs);     // field data offset
                w.Write((uint)0);          // field data byte count
                w.Write(AfterStructs);     // field indices offset
                w.Write((uint)0);          // field indices byte count
                w.Write(AfterStructs);     // list indices offset
                w.Write((uint)0);          // list indices byte count

                w.Write(0xFFFFFFFF);       // top-level struct type
                w.Write((uint)0);          // data or data offset
                w.Write((uint)0);          // field count

                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>Writes an uncompressed 24-bit 2x2 Targa image.</summary>
        public static void WriteTga(string path)
        {
            var header = new byte[18];
            header[2] = 2;      // uncompressed true-colour
            header[12] = 2;     // width  low byte
            header[14] = 2;     // height low byte
            header[16] = 24;    // bits per pixel

            var pixels = new byte[2 * 2 * 3];
            using (var ms = new MemoryStream())
            {
                ms.Write(header, 0, header.Length);
                ms.Write(pixels, 0, pixels.Length);
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>Writes a RIFF/WAVE file holding a single silent PCM sample.</summary>
        public static void WriteWav(string path)
        {
            const int SampleBytes = 2;

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Ascii, leaveOpen: true))
            {
                w.Write(Ascii.GetBytes("RIFF"));
                w.Write((uint)(36 + SampleBytes));
                w.Write(Ascii.GetBytes("WAVE"));
                w.Write(Ascii.GetBytes("fmt "));
                w.Write((uint)16);      // PCM chunk size
                w.Write((ushort)1);     // PCM
                w.Write((ushort)1);     // mono
                w.Write((uint)22050);   // sample rate
                w.Write((uint)44100);   // byte rate
                w.Write((ushort)2);     // block align
                w.Write((ushort)16);    // bits per sample
                w.Write(Ascii.GetBytes("data"));
                w.Write((uint)SampleBytes);
                w.Write(new byte[SampleBytes]);

                w.Flush();
                WriteAllBytes(path, ms.ToArray());
            }
        }

        /// <summary>
        /// Writes a placeholder for a format this fixture does not synthesise properly.
        /// Used only for files that nothing in the install path parses - see the notes in
        /// <c>.mission/notes/08-mock-fixture-and-ci.md</c>.
        /// </summary>
        public static void WriteOpaqueStub(string path, string label)
        {
            WriteAllBytes(path, Ascii.GetBytes("ModSync synthetic placeholder: " + label + "\n"));
        }

        private static void WriteAllBytes(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, bytes);
        }
    }
}
