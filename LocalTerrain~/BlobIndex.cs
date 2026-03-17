using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace LocalTerrain
{
    /// <summary>
    /// TERRBLOB / IMGEBLOB 바이너리를 파싱하여
    /// Dictionary&lt;long, TileRef&gt; 인덱스를 구축합니다.
    ///
    /// 키 인코딩: (zoom &lt;&lt; 40) | (x &lt;&lt; 20) | y
    ///   - zoom : 최대 22  →  5비트면 충분, 여유롭게 24비트 할당
    ///   - x, y : 최대 ~1M → 각 20비트
    ///   - long 64비트에 수용
    ///
    /// 사용법:
    ///   var idx = new BlobIndex();
    ///   idx.BuildTerrainIndex(BlobLoader.TerrainBlob);
    ///   foreach (var b in BlobLoader.ImageryBlobs) idx.BuildImageryIndex(b);
    ///
    ///   if (idx.TryGetTerrain(9, 875, 360, out TileRef r)) { ... r.AsSpan() ... }
    /// </summary>
    public class BlobIndex
    {
        private const string TerrainMagic = "TERRBLOB";
        private const string ImageryMagic = "IMGEBLOB";
        private const int GlobalHeaderSize = 64;   // bytes
        private const int ZoomDirectoryEntry = 28;  // bytes: uint32 + uint64×3
        private const int IndexTableEntry = 20;  // bytes: uint32×2 + uint64 + uint32

        private readonly Dictionary<long, TileRef> _terrainIndex = new Dictionary<long, TileRef>();
        private readonly Dictionary<long, TileRef> _imageryIndex = new Dictionary<long, TileRef>();

        public int TerrainTileCount => _terrainIndex.Count;
        public int ImageryTileCount => _imageryIndex.Count;

        /// <summary>
        /// terrain.bin (TERRBLOB v3) 을 파싱하여 _terrainIndex 를 구축합니다.
        /// </summary>
        public void BuildTerrainIndex(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
                throw new ArgumentNullException(nameof(blob), "[BlobIndex] TerrainBlob이 null 또는 비어있습니다.");

            BuildIndex(blob, TerrainMagic, _terrainIndex, "TerrainIndex");
        }

        /// <summary>
        /// imagery_NNN.bin (IMGEBLOB v3) 을 파싱하여 _imageryIndex 에 추가합니다.
        /// 여러 Blob을 반복 호출하면 모두 같은 인덱스에 병합됩니다.
        /// </summary>
        public void BuildImageryIndex(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
                throw new ArgumentNullException(nameof(blob), "[BlobIndex] ImageryBlob이 null 또는 비어있습니다.");

            BuildIndex(blob, ImageryMagic, _imageryIndex, "ImageryIndex");
        }

        /// <summary>
        /// 지형 타일 조회. 성공하면 true + TileRef 반환.
        /// </summary>
        public bool TryGetTerrain(int zoom, int x, int y, out TileRef tileRef)
        {
            return _terrainIndex.TryGetValue(EncodeKey(zoom, x, y), out tileRef);
        }

        /// <summary>
        /// 이미지 타일 조회. 성공하면 true + TileRef 반환.
        /// </summary>
        public bool TryGetImagery(int zoom, int x, int y, out TileRef tileRef)
        {
            return _imageryIndex.TryGetValue(EncodeKey(zoom, x, y), out tileRef);
        }

        private static void BuildIndex(
            byte[] blob,
            string expectedMagic,
            Dictionary<long, TileRef> index,
            string label)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using var ms = new MemoryStream(blob, writable: false);
            using var reader = new BinaryReader(ms, Encoding.ASCII, leaveOpen: true);

            // offset  0 : char[8]  magic
            // offset  8 : uint32   version
            // offset 12 : uint32   zoom_start
            // offset 16 : uint32   zoom_end
            // offset 20 : uint32   zoom_count
            // offset 24 : uint64   dir_offset   (항상 64)
            // offset 32 : byte[32] reserved

            string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
            if (magic != expectedMagic)
                throw new InvalidDataException(
                    $"[BlobIndex] {label}: Magic 불일치. " +
                    $"기대='{expectedMagic}', 실제='{magic}'");

            uint version = reader.ReadUInt32();
            uint zoomStart = reader.ReadUInt32();
            uint zoomEnd = reader.ReadUInt32();
            uint zoomCount = reader.ReadUInt32();
            ulong dirOffset = reader.ReadUInt64();
            reader.ReadBytes(32); // reserved

            if (version != 3)
                Debug.LogWarning($"[BlobIndex] {label}: 버전 {version} (예상 3). 계속 진행합니다.");

            Debug.Log($"[BlobIndex] {label}: magic={magic}, version={version}, " +
                      $"zoom={zoomStart}~{zoomEnd} ({zoomCount}개), dirOffset={dirOffset}");

            // offset  0 : uint32  zoom
            // offset  4 : uint64  tile_count
            // offset 12 : uint64  index_offset
            // offset 20 : uint64  data_offset

            ms.Position = (long)dirOffset;

            var zoomEntries = new ZoomEntry[zoomCount];
            for (int i = 0; i < (int)zoomCount; i++)
            {
                zoomEntries[i] = new ZoomEntry
                {
                    Zoom = reader.ReadUInt32(),
                    TileCount = reader.ReadUInt64(),
                    IndexOffset = reader.ReadUInt64(),
                    DataOffset = reader.ReadUInt64(),
                };
            }

            // offset  0 : uint32  x
            // offset  4 : uint32  y
            // offset  8 : uint64  offset  (파일 절대 오프셋)
            // offset 16 : uint32  size

            int totalAdded = 0;
            foreach (var ze in zoomEntries)
            {
                ms.Position = (long)ze.IndexOffset;

                for (ulong t = 0; t < ze.TileCount; t++)
                {
                    uint x = reader.ReadUInt32();
                    uint y = reader.ReadUInt32();
                    ulong offset = reader.ReadUInt64();
                    uint size = reader.ReadUInt32();

                    // offset 값은 파일(= blob byte[]) 절대 오프셋
                    // int 범위 체크: 최대 ~386MB < int.MaxValue(~2GB) ∴ 안전
                    if (offset > int.MaxValue || size > int.MaxValue)
                        throw new InvalidDataException(
                            $"[BlobIndex] {label}: Zoom={ze.Zoom} 타일 ({x},{y}) — " +
                            $"offset={offset} 또는 size={size} 가 int 범위 초과");

                    long key = EncodeKey((int)ze.Zoom, (int)x, (int)y);
                    index[key] = new TileRef(blob, (int)offset, (int)size);
                    totalAdded++;
                }
            }

            sw.Stop();
            Debug.Log($"[BlobIndex] {label}: {totalAdded}개 타일 인덱스 완료 " +
                      $"({sw.ElapsedMilliseconds}ms)");
        }

        /// <summary>
        /// (zoom, x, y) → long 키
        /// 비트 레이아웃: [63..40]=zoom(24bit) [39..20]=x(20bit) [19..0]=y(20bit)
        /// </summary>
        public static long EncodeKey(int zoom, int x, int y)
        {
            return ((long)zoom << 40) | ((long)x << 20) | (long)y;
        }

        /// <summary>
        /// long 키 → (zoom, x, y) 역산 (디버깅 용도)
        /// </summary>
        public static (int zoom, int x, int y) DecodeKey(long key)
        {
            int zoom = (int)((key >> 40) & 0xFFFFFF);
            int x = (int)((key >> 20) & 0xFFFFF);
            int y = (int)(key & 0xFFFFF);
            return (zoom, x, y);
        }

        private struct ZoomEntry
        {
            public uint Zoom;
            public ulong TileCount;
            public ulong IndexOffset;
            public ulong DataOffset;
        }

        /// <summary>
        /// 인덱스 전체 통계를 로그로 출력합니다.
        /// </summary>
        public void LogStats()
        {
            Debug.Log($"[BlobIndex] 통계\n" +
                      $"  TerrainIndex : {_terrainIndex.Count:N0} 타일\n" +
                      $"  ImageryIndex : {_imageryIndex.Count:N0} 타일\n" +
                      $"  예상 메모리  : ~{EstimateMemoryKB():F0} KB");
        }

        /// <summary>
        /// 특정 타일의 존재 여부와 크기를 로그로 출력합니다. (개발용)
        /// </summary>
        public void DebugTile(bool isTerrain, int zoom, int x, int y)
        {
            bool found = isTerrain
                ? TryGetTerrain(zoom, x, y, out var r)
                : TryGetImagery(zoom, x, y, out r);

            if (found)
            {
                var r2 = isTerrain
                    ? (_terrainIndex.TryGetValue(EncodeKey(zoom, x, y), out var rr) ? rr : default)
                    : (_imageryIndex.TryGetValue(EncodeKey(zoom, x, y), out var rr2) ? rr2 : default);
                Debug.Log($"[BlobIndex] Tile({zoom}/{x}/{y}) " +
                          $"Offset={r2.Offset} Size={r2.Size} Bytes");
            }
            else
            {
                Debug.LogWarning($"[BlobIndex] failed Tile({zoom}/{x}/{y}) — 인덱스에 없음");
            }
        }

        private float EstimateMemoryKB()
        {
            // Dictionary 엔트리 1개 ≈ 키(8B) + TileRef(16B) + 오버헤드(~24B) ≈ 48B
            return (_terrainIndex.Count + _imageryIndex.Count) * 48f / 1024f;
        }
    }
}