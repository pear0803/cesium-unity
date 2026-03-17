using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace LocalTerrain
{
    /// <summary>
    /// Loads terrain.bin and imagery_000~003.bin from StreamingAssets into memory.
    ///
    /// TERRBLOB / IMGEBLOB binary layout (both identical):
    ///   [Global Header  64 bytes            ]
    ///   [Zoom Directory zoom_count x 28 bytes]
    ///   [Zoom N Index   tile_count x 20 bytes]
    ///   [Zoom N Data    variable             ]
    ///   ...
    ///
    /// All 5 files are loaded in parallel (Task.WhenAll).
    /// Lazy loading is intentionally not used.
    /// </summary>
    public static class BlobLoader
    {
        // ----------------------------------------------------------------
        //  Public data (available after LoadAllAsync completes)
        // ----------------------------------------------------------------
        public static byte[] TerrainBlob { get; private set; }
        public static byte[][] ImageryBlobs { get; private set; } = new byte[4][];
        public static bool IsLoaded { get; private set; } = false;

        // ----------------------------------------------------------------
        //  File names
        // ----------------------------------------------------------------
        private const string TerrainFileName = "terrain.bin";
        private static readonly string[] ImageryFileNames =
        {
            "imagery_000.bin",   // zoom 6~7   13.3 MB
            "imagery_001.bin",   // zoom 8     20.4 MB
            "imagery_002.bin",   // zoom 9     87.1 MB
            "imagery_003.bin",   // zoom 10   140.0 MB
        };

        // Expected magic bytes
        private const string MagicTerrain = "TERRBLOB";
        private const string MagicImagery = "IMGEBLOB";

        // ----------------------------------------------------------------
        //  LoadAllAsync
        //  Loads all 5 files in parallel, then validates headers.
        //  terrainDataDir: Application.streamingAssetsPath + "/TerrainData"
        // ----------------------------------------------------------------
        public static async Task LoadAllAsync(string terrainDataDir)
        {
            if (IsLoaded)
            {
                Debug.LogWarning("[BlobLoader] Already loaded. Skipping.");
                return;
            }

            Debug.Log($"[BlobLoader] Loading from: {terrainDataDir}");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Launch all 5 reads in parallel
            var terrainTask = LoadFileAsync(Path.Combine(terrainDataDir, TerrainFileName));
            var imageryTask0 = LoadFileAsync(Path.Combine(terrainDataDir, ImageryFileNames[0]));
            var imageryTask1 = LoadFileAsync(Path.Combine(terrainDataDir, ImageryFileNames[1]));
            var imageryTask2 = LoadFileAsync(Path.Combine(terrainDataDir, ImageryFileNames[2]));
            var imageryTask3 = LoadFileAsync(Path.Combine(terrainDataDir, ImageryFileNames[3]));

            // Wait for all simultaneously
            await Task.WhenAll(terrainTask, imageryTask0, imageryTask1, imageryTask2, imageryTask3);

            TerrainBlob = terrainTask.Result;
            ImageryBlobs[0] = imageryTask0.Result;
            ImageryBlobs[1] = imageryTask1.Result;
            ImageryBlobs[2] = imageryTask2.Result;
            ImageryBlobs[3] = imageryTask3.Result;

            sw.Stop();

            // Validate all headers
            ValidateBlobHeader(TerrainBlob, MagicTerrain, TerrainFileName);
            ValidateBlobHeader(ImageryBlobs[0], MagicImagery, ImageryFileNames[0]);
            ValidateBlobHeader(ImageryBlobs[1], MagicImagery, ImageryFileNames[1]);
            ValidateBlobHeader(ImageryBlobs[2], MagicImagery, ImageryFileNames[2]);
            ValidateBlobHeader(ImageryBlobs[3], MagicImagery, ImageryFileNames[3]);

            IsLoaded = true;

            long totalBytes = TerrainBlob.LongLength
                + ImageryBlobs[0].LongLength + ImageryBlobs[1].LongLength
                + ImageryBlobs[2].LongLength + ImageryBlobs[3].LongLength;

            Debug.Log(
                $"[BlobLoader] All 5 files loaded in {sw.ElapsedMilliseconds} ms " +
                $"(total {totalBytes / 1024 / 1024.0f:F1} MB)\n" +
                $"  {TerrainFileName,-22} {TerrainBlob.Length / 1024 / 1024.0f,6:F1} MB  " +
                    $"zoom {ReadUInt32(TerrainBlob, 12)}~{ReadUInt32(TerrainBlob, 16)}  " +
                    $"tiles zoom_count={ReadUInt32(TerrainBlob, 20)}\n" +
                $"  {ImageryFileNames[0],-22} {ImageryBlobs[0].Length / 1024 / 1024.0f,6:F1} MB  " +
                    $"zoom {ReadUInt32(ImageryBlobs[0], 12)}~{ReadUInt32(ImageryBlobs[0], 16)}\n" +
                $"  {ImageryFileNames[1],-22} {ImageryBlobs[1].Length / 1024 / 1024.0f,6:F1} MB  " +
                    $"zoom {ReadUInt32(ImageryBlobs[1], 12)}~{ReadUInt32(ImageryBlobs[1], 16)}\n" +
                $"  {ImageryFileNames[2],-22} {ImageryBlobs[2].Length / 1024 / 1024.0f,6:F1} MB  " +
                    $"zoom {ReadUInt32(ImageryBlobs[2], 12)}~{ReadUInt32(ImageryBlobs[2], 16)}\n" +
                $"  {ImageryFileNames[3],-22} {ImageryBlobs[3].Length / 1024 / 1024.0f,6:F1} MB  " +
                    $"zoom {ReadUInt32(ImageryBlobs[3], 12)}~{ReadUInt32(ImageryBlobs[3], 16)}");
        }

        // ----------------------------------------------------------------
        //  ValidateBlobHeader
        //  Global Header layout (64 bytes, little-endian):
        //    [0 ..  7]  char[8]  magic        "TERRBLOB" or "IMGEBLOB"
        //    [8 .. 11]  uint32   version
        //    [12.. 15]  uint32   zoom_start
        //    [16.. 19]  uint32   zoom_end
        //    [20.. 23]  uint32   zoom_count
        //    [24.. 31]  uint64   dir_offset
        //    [32.. 63]  byte[32] reserved
        // ----------------------------------------------------------------
        private static void ValidateBlobHeader(byte[] blob, string expectedMagic, string fileName)
        {
            if (blob.Length < 64)
            {
                Debug.LogError($"[BlobLoader] {fileName}: too small for header ({blob.Length} bytes).");
                return;
            }

            // Magic check
            string magic = System.Text.Encoding.ASCII.GetString(blob, 0, 8);
            if (magic != expectedMagic)
            {
                Debug.LogError(
                    $"[BlobLoader] {fileName}: magic MISMATCH! " +
                    $"expected '{expectedMagic}', got '{magic}'");
                return;
            }

            uint version = ReadUInt32(blob, 8);
            uint zoomStart = ReadUInt32(blob, 12);
            uint zoomEnd = ReadUInt32(blob, 16);
            uint zoomCount = ReadUInt32(blob, 20);
            ulong dirOffset = ReadUInt64(blob, 24);

            // Sanity: dir_offset should be 64 (immediately after header)
            if (dirOffset != 64)
                Debug.LogWarning(
                    $"[BlobLoader] {fileName}: unexpected dir_offset={dirOffset} (expected 64)");

            // Sanity: zoom_count should match zoom range
            uint expectedCount = zoomEnd - zoomStart + 1;
            if (zoomCount != expectedCount)
                Debug.LogWarning(
                    $"[BlobLoader] {fileName}: zoom_count={zoomCount} " +
                    $"but zoom range {zoomStart}~{zoomEnd} implies {expectedCount}");

            Debug.Log(
                $"[BlobLoader] {fileName}: OK  magic={magic}  ver={version}  " +
                $"zoom={zoomStart}~{zoomEnd}  zoom_count={zoomCount}  " +
                $"dir_offset={dirOffset}  file_size={blob.Length}");
        }

        // ----------------------------------------------------------------
        //  LoadFileAsync: async buffered file read
        // ----------------------------------------------------------------
        private static async Task<byte[]> LoadFileAsync(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"[BlobLoader] File not found: {path}");

            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,  // 1MB buffer for large files
                useAsync: true);

            var buffer = new byte[fs.Length];
            int offset = 0;
            int remaining = buffer.Length;

            while (remaining > 0)
            {
                int read = await fs.ReadAsync(buffer, offset, remaining);
                if (read == 0) break;
                offset += read;
                remaining -= read;
            }

            return buffer;
        }

        // ----------------------------------------------------------------
        //  Binary read helpers (little-endian, matches TERRBLOB/IMGEBLOB spec)
        // ----------------------------------------------------------------
        public static uint ReadUInt32(byte[] blob, int offset)
            => BitConverter.ToUInt32(blob, offset);

        public static ulong ReadUInt64(byte[] blob, int offset)
            => BitConverter.ToUInt64(blob, offset);

        // ----------------------------------------------------------------
        //  ZoomDirEntry: parsed Zoom Directory entry
        //  Layout (28 bytes, little-endian):
        //    [0 ..  3]  uint32  zoom
        //    [4 .. 11]  uint64  tile_count
        //    [12.. 19]  uint64  index_offset
        //    [20.. 27]  uint64  data_offset
        // ----------------------------------------------------------------
        public struct ZoomDirEntry
        {
            public uint Zoom;
            public ulong TileCount;
            public ulong IndexOffset;
            public ulong DataOffset;
        }

        public static ZoomDirEntry ReadZoomDirEntry(byte[] blob, ulong dirOffset, int i)
        {
            int off = (int)(dirOffset + (ulong)(i * 28));
            return new ZoomDirEntry
            {
                Zoom = ReadUInt32(blob, off),
                TileCount = ReadUInt64(blob, off + 4),
                IndexOffset = ReadUInt64(blob, off + 12),
                DataOffset = ReadUInt64(blob, off + 20),
            };
        }

        // ----------------------------------------------------------------
        //  IndexEntry: single Index Table entry
        //  Layout (20 bytes, little-endian):
        //    [0 ..  3]  uint32  x
        //    [4 ..  7]  uint32  y
        //    [8 .. 15]  uint64  offset
        //    [16.. 19]  uint32  size
        // ----------------------------------------------------------------
        public struct IndexEntry
        {
            public uint X;
            public uint Y;
            public ulong Offset;
            public uint Size;
        }

        public static IndexEntry ReadIndexEntry(byte[] blob, ulong indexOffset, int i)
        {
            int off = (int)(indexOffset + (ulong)(i * 20));
            return new IndexEntry
            {
                X = ReadUInt32(blob, off),
                Y = ReadUInt32(blob, off + 4),
                Offset = ReadUInt64(blob, off + 8),
                Size = ReadUInt32(blob, off + 16),
            };
        }
    }
}